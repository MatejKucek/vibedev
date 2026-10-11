using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Vibedev.Core;

/// <summary>A stdio LSP session. Each project panel owns its server and unsaved document versions.</summary>
public sealed class LanguageServerClient : IDisposable
{
    private readonly Process process;
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly string root;
    private int nextId;
    private bool disposed;
    public event Action<string, JsonElement>? Notification;
    public event Action<string>? Failed;

    public LanguageServerClient(string executable, string directory)
    {
        root = Path.GetFullPath(directory);
        process = new() { StartInfo = new(executable) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.Start();
        _ = ReadLoopAsync();
        _ = DrainErrorsAsync();
    }

    public async Task InitializeAsync()
    {
        await RequestAsync("initialize", new
        {
            processId = Environment.ProcessId, rootUri = new Uri(root + Path.DirectorySeparatorChar).AbsoluteUri,
            workspaceFolders = new[] { new { uri = new Uri(root + Path.DirectorySeparatorChar).AbsoluteUri, name = Path.GetFileName(root) } },
            clientInfo = new { name = "Vibedev", version = "1.0" },
            capabilities = new
            {
                workspace = new { configuration = true, workspaceFolders = true },
                general = new { positionEncodings = new[] { "utf-16" } },
                textDocument = new
                {
                    synchronization = new { didSave = true },
                    completion = new { completionItem = new { snippetSupport = false, documentationFormat = new[] { "plaintext" }, resolveSupport = new { properties = new[] { "documentation", "detail", "textEdit", "additionalTextEdits" } } } },
                    hover = new { contentFormat = new[] { "plaintext" } },
                    publishDiagnostics = new { versionSupport = true }
                }
            }
        }, TimeSpan.FromSeconds(90));
        await NotifyAsync("initialized", new { });
    }

    public Task OpenAsync(string path, string text, int version) => NotifyAsync("textDocument/didOpen", new { textDocument = new { uri = FileUri(path), languageId = "csharp", version, text } });
    public Task ChangeAsync(string path, string text, int version) => NotifyAsync("textDocument/didChange", new { textDocument = new { uri = FileUri(path), version }, contentChanges = new[] { new { text } } });
    public Task SaveAsync(string path) => NotifyAsync("textDocument/didSave", new { textDocument = new { uri = FileUri(path) } });
    public Task CloseAsync(string path) => NotifyAsync("textDocument/didClose", new { textDocument = new { uri = FileUri(path) } });
    public Task<JsonElement> AtAsync(string method, string path, int line, int character) => RequestAsync(method, new { textDocument = new { uri = FileUri(path) }, position = new { line, character } });
    public static string FileUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    public async Task<JsonElement> RequestAsync(string method, object parameters, TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters });
            return await completion.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(20), lifetime.Token);
        }
        catch (TimeoutException)
        {
            await NotifyAsync("$/cancelRequest", new { id });
            throw;
        }
        finally { pending.TryRemove(id, out _); }
    }
    public Task NotifyAsync(string method, object parameters) => SendAsync(new { jsonrpc = "2.0", method, @params = parameters });
    private async Task SendAsync(object message)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        await writer.WaitAsync(lifetime.Token);
        try
        {
            var output = process.StandardInput.BaseStream;
            await output.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), lifetime.Token);
            await output.WriteAsync(body, lifetime.Token);
            await output.FlushAsync(lifetime.Token);
        }
        finally { writer.Release(); }
    }

    // Byte framing is required: Content-Length counts UTF-8 bytes, not characters.
    public static async Task<JsonElement?> ReadMessageAsync(Stream input, CancellationToken cancellation = default)
    {
        var header = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await input.ReadAsync(one, cancellation) == 0) return null;
            header.Add(one[0]);
            if (header.Count > 8192) throw new IOException("LSP header is too large.");
            if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
        }
        var lengthLine = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        if (lengthLine is null || !int.TryParse(lengthLine[15..].Trim(), out var length) || length < 0 || length > 32 * 1024 * 1024) throw new IOException("Invalid LSP Content-Length.");
        var bytes = new byte[length];
        await input.ReadExactlyAsync(bytes, cancellation);
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }
    private async Task ReadLoopAsync()
    {
        try
        {
            while (await ReadMessageAsync(process.StandardOutput.BaseStream, lifetime.Token) is { } message)
            {
                if (message.TryGetProperty("method", out var method))
                {
                    if (message.TryGetProperty("id", out var serverId)) await ReplyAsync(serverId, method.GetString()!, message);
                    else if (message.TryGetProperty("params", out var parameters)) Notification?.Invoke(method.GetString()!, parameters.Clone());
                }
                else if (message.TryGetProperty("id", out var id) && id.TryGetInt32(out var requestId) && pending.TryRemove(requestId, out var completion))
                {
                    if (message.TryGetProperty("error", out var error)) completion.TrySetException(new IOException(error.ToString()));
                    else completion.TrySetResult(message.GetProperty("result").Clone());
                }
            }
            if (!disposed) throw new IOException("The C# language server exited. Use C# server to restart it.");
        }
        catch (Exception ex)
        {
            foreach (var request in pending.Values) request.TrySetException(ex);
            pending.Clear();
            if (!disposed) Failed?.Invoke(ex.Message);
        }
    }
    private Task ReplyAsync(JsonElement id, string method, JsonElement message)
    {
        object? result = method switch
        {
            "workspace/configuration" => message.GetProperty("params").GetProperty("items").EnumerateArray().Select(_ => new { }).ToArray(),
            "workspace/workspaceFolders" => new[] { new { uri = new Uri(root + Path.DirectorySeparatorChar).AbsoluteUri, name = Path.GetFileName(root) } },
            "window/workDoneProgress/create" or "client/registerCapability" or "client/unregisterCapability" => null,
            _ => null
        };
        return method is "workspace/configuration" or "workspace/workspaceFolders" or "window/workDoneProgress/create" or "client/registerCapability" or "client/unregisterCapability"
            ? SendAsync(new { jsonrpc = "2.0", id, result })
            : SendAsync(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Unsupported client method: " + method } });
    }
    private async Task DrainErrorsAsync()
    {
        try { while (await process.StandardError.ReadLineAsync(lifetime.Token) is not null) { } }
        catch (Exception) when (disposed || lifetime.IsCancellationRequested) { }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; lifetime.Cancel();
        foreach (var request in pending.Values) request.TrySetCanceled();
        pending.Clear();
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        process.Dispose();
    }
}
