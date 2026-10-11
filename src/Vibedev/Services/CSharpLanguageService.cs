using System.IO;
using System.Windows.Threading;
using Vibedev.Core;
using Vibedev.Views;

namespace Vibedev.Services;

internal sealed class CSharpLanguageService : IDisposable
{
    public static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vibedev", "LanguageServer");
    private readonly ProjectView view;
    private readonly Action<string> log;
    private readonly Dictionary<EditorTab, EditorLanguageFeatures> editors = [];
    private LanguageServerClient? client;
    private Task? starting;
    private bool disposed;
    internal EditorLanguageFeatures? Features(EditorTab editor) => editors.GetValueOrDefault(editor);
    public CSharpLanguageService(ProjectView view, Action<string> log) => (this.view, this.log) = (view, log);
    private static string? FindServer()
    {
        var candidates = new[] { Path.Combine(InstallDirectory, "csharp-ls.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", "csharp-ls.exe") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(p => Path.Combine(p.Trim('"'), "csharp-ls.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }
    public async Task EnableAsync()
    {
        if (disposed) return;
        if (FindServer() is null)
        {
            foreach (var editor in view.Editors) editor.SetLanguageStatus("Installing C# server…");
            Directory.CreateDirectory(InstallDirectory);
            var result = await ProcessRunner.RunAsync(new("dotnet", ["tool", "install", "csharp-ls", "--version", "0.26.0", "--tool-path", InstallDirectory], view.Project.Entry.Path), log);
            if (!result.Success) { log("C# server installation failed. Check .NET SDK/network access and try again."); return; }
        }
        if (client is null) starting = null;
        foreach (var editor in view.Editors.ToArray()) await AttachAsync(editor);
    }
    public async Task AttachAsync(EditorTab editor)
    {
        if (disposed || !editor.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || editors.ContainsKey(editor)) return;
        if (FindServer() is not { } executable) { editor.SetLanguageStatus("C# support: click C# server to install"); return; }
        editor.SetLanguageStatus("C# server loading…");
        try
        {
            starting ??= StartAsync(executable);
            await starting;
            if (disposed || client is null || !view.Editors.Contains(editor) || editors.ContainsKey(editor)) return;
            var features = new EditorLanguageFeatures(editor, client, view.OpenLocationAsync, log);
            editors.Add(editor, features);
            await features.OpenAsync();
        }
        catch (Exception ex) { editor.SetLanguageStatus("C# unavailable: use C# server to retry"); log("C# server: " + ex.Message); }
    }
    private async Task StartAsync(string executable)
    {
        var server = new LanguageServerClient(executable, view.Project.Entry.Path);
        client = server;
        server.Failed += message => view.Dispatcher.BeginInvoke(() =>
        {
            if (disposed || !ReferenceEquals(client, server)) return;
            log(message);
            foreach (var features in editors.Values) features.Dispose();
            editors.Clear(); server.Dispose(); client = null; starting = null;
            foreach (var editor in view.Editors) editor.SetLanguageStatus("C# unavailable: use C# server to retry");
        });
        try { await server.InitializeAsync(); }
        catch { server.Dispose(); if (ReferenceEquals(client, server)) client = null; starting = null; throw; }
    }
    public void Detach(EditorTab editor)
    {
        if (editors.Remove(editor, out var features)) features.Dispose();
    }
    public async Task FilesChangedAsync(IReadOnlyDictionary<string, int> changes)
    {
        if (client is null || starting is null || !starting.IsCompletedSuccessfully) return;
        var relevant = changes.Where(c => Path.GetExtension(c.Key) is ".cs" or ".csproj" or ".sln" or ".slnx" or ".props" or ".targets").ToArray();
        if (relevant.Length == 0) return;
        try { await client.NotifyAsync("workspace/didChangeWatchedFiles", new { changes = relevant.Select(c => new { uri = LanguageServerClient.FileUri(c.Key), type = c.Value }).ToArray() }); }
        catch (Exception ex) { if (!disposed) log("C# file refresh: " + ex.Message); }
    }
    public void Dispose()
    {
        disposed = true;
        foreach (var features in editors.Values) features.Dispose();
        editors.Clear(); client?.Dispose(); client = null;
    }
}
