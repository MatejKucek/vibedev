using System.IO;
using System.Collections.Concurrent;
using System.Windows.Threading;

namespace Vibedev.Services;

/// <summary>Coalesces file-system bursts onto the UI thread; a failed refresh is retried.</summary>
internal sealed class ProjectFileMonitor : IDisposable
{
    private readonly FileSystemWatcher watcher;
    private readonly DispatcherTimer timer;
    private readonly Dispatcher dispatcher;
    private readonly Func<IReadOnlyDictionary<string, int>, Task> refresh;
    private readonly ConcurrentDictionary<string, int> changes = new(StringComparer.OrdinalIgnoreCase);
    private int queued;
    private bool disposed, busy, pending;
    public ProjectFileMonitor(string directory, Dispatcher dispatcher, Func<IReadOnlyDictionary<string, int>, Task> refresh, Action<string> error)
    {
        this.dispatcher = dispatcher; this.refresh = refresh;
        timer = new(TimeSpan.FromMilliseconds(450), DispatcherPriority.Background, async (_, _) => await RefreshAsync(), dispatcher);
        timer.Stop();
        watcher = new(directory) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
        watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed;
        watcher.Renamed += (_, e) => { Record(e.OldFullPath, 3); Record(e.FullPath, 1); };
        watcher.Error += (_, e) => { error("File watcher: " + e.GetException().Message); Schedule(); };
        watcher.EnableRaisingEvents = true;
    }
    private void Changed(object sender, FileSystemEventArgs e)
    {
        Record(e.FullPath, e.ChangeType == WatcherChangeTypes.Created ? 1 : e.ChangeType == WatcherChangeTypes.Deleted ? 3 : 2);
    }
    private void Record(string path, int kind)
    {
        var relative = Path.GetRelativePath(watcher.Path, path);
        if (relative.Split(Path.DirectorySeparatorChar).Contains(".git", StringComparer.OrdinalIgnoreCase) && Path.GetFileName(path) is not "index" and not "HEAD" || Path.GetFileName(relative).Contains(".vibedev-", StringComparison.Ordinal)) return;
        changes.AddOrUpdate(path, kind, (_, previous) => previous == 1 && kind == 2 ? 1 : kind); Schedule();
    }
    private void Schedule()
    {
        if (disposed || dispatcher.HasShutdownStarted || Interlocked.Exchange(ref queued, 1) != 0) return;
        dispatcher.BeginInvoke(() => { Interlocked.Exchange(ref queued, 0); if (disposed) return; pending = true; timer.Stop(); timer.Start(); });
    }
    private async Task RefreshAsync()
    {
        if (disposed || busy) return;
        timer.Stop(); if (!pending) return;
        pending = false; busy = true;
        var batch = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in changes.Keys) if (changes.TryRemove(path, out var kind)) batch[path] = kind;
        try { await refresh(batch); }
        catch (IOException) { pending = true; }
        catch (UnauthorizedAccessException) { pending = true; }
        finally { busy = false; if (pending && !disposed) timer.Start(); }
    }
    public void Dispose() { disposed = true; watcher.Dispose(); timer.Stop(); }
}
