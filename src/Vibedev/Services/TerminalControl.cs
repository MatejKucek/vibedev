using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Vibedev.Core;
using Vibedev.Views;

namespace Vibedev.Services;

public sealed class TerminalControl : UserControl, IDisposable
{
    private readonly WebView2CompositionControl web = new();
    private readonly ConcurrentQueue<(string Text, string Directory)> pending = new();
    private readonly DispatcherTimer outputTimer;
    private readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PseudoConsole? session;
    private CancellationTokenSource? running;
    private string directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private string outputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private bool ready, disposed, hasSession;
    private int columns = 120, rows = 24;
    public bool IsRunning => running is not null;
    public bool IsReady => ready;
    public string CurrentDirectory => outputDirectory;
    public event Action<string>? StateChanged;
    public event Action<string>? Log;
    public event Action<string, string>? TaskOutput;
    public event Action<BuildLocation>? LocationActivated;

    public TerminalControl()
    {
        Content = web;
        outputTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(35), DispatcherPriority.Background, (_, _) => Flush(), Dispatcher);
        Loaded += async (_, _) =>
        {
            if (web.CoreWebView2 is not null || disposed) return;
            try
            {
                var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vibedev", "WebView2");
                var env = await CoreWebView2Environment.CreateAsync(userDataFolder: cache);
                await web.EnsureCoreWebView2Async(env);
                if (disposed) return;
                web.CoreWebView2!.Settings.AreDefaultContextMenusEnabled = false;
                web.CoreWebView2.Settings.AreDevToolsEnabled = false;
                web.CoreWebView2.SetVirtualHostNameToFolderMapping("terminal.vibedev.local", Path.Combine(AppContext.BaseDirectory, "Terminal"), CoreWebView2HostResourceAccessKind.DenyCors);
                web.CoreWebView2.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("https://terminal.vibedev.local/", StringComparison.Ordinal)) e.Cancel = true; };
                web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
                web.CoreWebView2.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                web.CoreWebView2.WebMessageReceived += (_, e) =>
                {
                    if (e.Source != "https://terminal.vibedev.local/index.html") return;
                    using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                    var msg = doc.RootElement;
                    switch (msg.GetProperty("type").GetString())
                    {
                        case "ready": ready = true; StartShell(); initialized.TrySetResult(); break;
                        case "input": session?.Write(msg.GetProperty("data").GetString()!); break;
                        case "resize": columns = msg.GetProperty("cols").GetInt32(); rows = msg.GetProperty("rows").GetInt32(); session?.Resize(columns, rows); break;
                        case "location":
                            if (msg.TryGetProperty("text", out var text) && msg.TryGetProperty("directory", out var cwd) && BuildLocation.Parse(text.GetString() ?? "", cwd.GetString() ?? outputDirectory) is { } location) LocationActivated?.Invoke(location);
                            break;
                    }
                };
                web.Source = new Uri("https://terminal.vibedev.local/index.html");
            }
            catch (Exception ex)
            {
                Content = Ui.Text("Terminal could not start. Install the Microsoft Edge WebView2 Runtime and reopen the app.\n\n" + ex.Message);
                Log?.Invoke("Terminal initialization failed: " + ex.Message);
                initialized.TrySetException(ex);
            }
        };
    }

    public void SetDirectory(string path)
    {
        if (!Directory.Exists(path) || directory.Equals(path, StringComparison.OrdinalIgnoreCase)) return;
        directory = path;
        if (!IsRunning) outputDirectory = path;
        if (ready && !IsRunning) session?.Write("Set-Location -LiteralPath '" + path.Replace("'", "''") + "'\r");
    }
    private void StartShell()
    {
        if (disposed || !ready || IsRunning) return;
        outputDirectory = directory;
        if (hasSession) ArchiveScreen();
        hasSession = true;
        session?.Dispose();
        var shell = new PseudoConsole(); session = shell;
        shell.Output += Write;
        shell.Exited += code => Dispatcher.BeginInvoke(() =>
        {
            if (disposed || !ReferenceEquals(session, shell)) return;
            Write($"\r\n[Shell exited: {code}. Starting a new shell.]\r\n");
            StartShell();
        });
        try { shell.Start(new("powershell.exe", ["-NoLogo", "-NoProfile", "-NoExit"], directory), columns, rows); StateChanged?.Invoke("PowerShell · shared"); }
        catch (Exception ex) { shell.Dispose(); Log?.Invoke("Shell failed: " + ex.Message); }
    }

    public async Task RunAsync(string name, LaunchPlan plan)
    {
        if (!ready) await initialized.Task.WaitAsync(TimeSpan.FromSeconds(30));
        if (IsRunning) throw new InvalidOperationException("Stop the active project before running another.");
        using var cancellation = new CancellationTokenSource();
        running = cancellation;
        directory = plan.Command.WorkingDirectory;
        outputDirectory = directory;
        StateChanged?.Invoke(name + " · running");
        ArchiveScreen();
        session?.Dispose(); session = null;
        try
        {
            Write($"\r\n\x1b[35m── {name} ──\x1b[0m\r\n");
            foreach (var task in plan.BeforeLaunch)
            {
                outputDirectory = task.WorkingDirectory;
                Log?.Invoke($"[task] {task.FileName} {string.Join(" ", task.Arguments)}");
                var result = await ProcessRunner.RunAsync(task, line => { TaskOutput?.Invoke(line, task.WorkingDirectory); Write(line + "\r\n"); }, cancellation.Token);
                if (!result.Success) throw new InvalidOperationException($"Build task failed (exit {result.ExitCode}). See Git & Task Output.");
            }
            cancellation.Token.ThrowIfCancellationRequested();
            outputDirectory = plan.Command.WorkingDirectory;
            var console = new PseudoConsole(); session = console;
            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            console.Output += Write;
            console.Exited += code => completion.TrySetResult(code);
            console.Start(plan.Command, columns, rows);
            using var cancel = cancellation.Token.Register(() => { console.Dispose(); completion.TrySetCanceled(cancellation.Token); });
            var exitCode = await completion.Task;
            Write($"\r\n\x1b[{(exitCode == 0 ? "32" : "31")}m[{name} exited with code {exitCode}]\x1b[0m\r\n");
            Log?.Invoke($"{name} exited with code {exitCode}.");
        }
        catch (OperationCanceledException) { Write($"\r\n[{name} stopped]\r\n"); }
        finally
        {
            session?.Dispose(); session = null; running = null;
            if (!disposed) StartShell();
        }
    }

    public void Stop() => running?.Cancel();
    public void Write(string data) { if (!disposed) pending.Enqueue((data, outputDirectory)); }
    private void Flush()
    {
        if (!ready || disposed || pending.IsEmpty) return;
        var count = 0;
        while (count < 65536 && pending.TryDequeue(out var chunk)) { count += chunk.Text.Length; Send(new { type = "output", data = chunk.Text, directory = chunk.Directory }); }
    }
    private void Send(object message) => web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    private void ArchiveScreen() { Flush(); Send(new { type = "archive" }); }
    public void Clear() => Send(new { type = "clear" });
    public void FocusTerminal() { web.Focus(); if (ready) Send(new { type = "focus" }); }
    public Task CaptureAsync(Stream stream) => web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
    internal Task<string> EvaluateAsync(string script) => web.CoreWebView2.ExecuteScriptAsync(script);
    public void Dispose()
    {
        disposed = true; initialized.TrySetCanceled(); running?.Cancel(); session?.Dispose(); outputTimer.Stop(); web.Dispose();
    }
}
