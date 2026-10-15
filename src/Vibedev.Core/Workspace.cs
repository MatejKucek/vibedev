using System.Text.Json;

namespace Vibedev.Core;

public sealed record ProjectEntry(string Path)
{
    public string Id => System.IO.Path.GetFullPath(Path).TrimEnd('\\', '/').ToUpperInvariant();
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
}

public sealed class WorkspaceState
{
    public List<ProjectEntry> Projects { get; set; } = [];
    public string? ScanRoot { get; set; }
    public Dictionary<string, List<string>> OpenFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> PanelProjects { get; set; } = [];
    public double Width { get; set; } = 1440;
    public double Height { get; set; } = 920;
    public bool DashboardVisible { get; set; } = true;
    public bool ProjectSidebarVisible { get; set; } = true;
    public double ProjectSidebarWidth { get; set; } = 300;
    public bool TerminalVisible { get; set; } = true;
    public double TerminalHeight { get; set; } = 240;
}

public sealed class WorkspaceStore(string directory)
{
    public string DirectoryPath { get; } = directory;
    public string LayoutPath => System.IO.Path.Combine(DirectoryPath, "layout.xml");
    public WorkspaceState Load()
    {
        var file = System.IO.Path.Combine(DirectoryPath, "workspace.json");
        return File.Exists(file)
            ? JsonSerializer.Deserialize<WorkspaceState>(File.ReadAllText(file)) ?? new()
            : new();
    }

    public void Save(WorkspaceState state)
    {
        Directory.CreateDirectory(DirectoryPath);
        var file = System.IO.Path.Combine(DirectoryPath, "workspace.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(file + ".tmp", file, true);
    }
}

public sealed record ScanResult(IReadOnlyList<string> Repositories, IReadOnlyList<string> Warnings);

public static class RepositoryDiscovery
{
    public static ScanResult Scan(string root, CancellationToken cancellation = default)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        List<string> repos = [], warnings = [];
        Stack<string> pending = new();
        pending.Push(System.IO.Path.GetFullPath(root));
        while (pending.TryPop(out var path))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var marker = System.IO.Path.Combine(path, ".git");
                if (Directory.Exists(marker) || File.Exists(marker)) repos.Add(path);
                // Keep descending inside repositories: monorepos can contain independent repositories.
                foreach (var child in Directory.EnumerateDirectories(path))
                {
                    if (System.IO.Path.GetFileName(child) is ".git" or "node_modules" or "bin" or "obj" or ".vs") continue;
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                    pending.Push(child);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                warnings.Add($"{path}: {ex.Message}");
            }
        }
        return new(repos.Order(StringComparer.OrdinalIgnoreCase).ToArray(), warnings);
    }
}
