namespace Vibedev.Core;

public enum ProjectSearchMode { Both, Contents, FileNames }

public sealed record ProjectSearchHit(string FilePath, string RelativePath, int Line, int Column, int Length, string Preview)
{
    public string Location => Line > 0 ? $"{RelativePath}:{Line}" : RelativePath;
}

public sealed record ProjectSearchResult(IReadOnlyList<ProjectSearchHit> Hits, int FilesSearched, bool Truncated, IReadOnlyList<string> Warnings);

public static class ProjectSearch
{
    public const int ResultLimit = 500;
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase) { ".git", ".vs", "bin", "obj", "node_modules" };

    public static Task<ProjectSearchResult> FindAsync(string root, string query, ProjectSearchMode mode = ProjectSearchMode.Both,
        IReadOnlyDictionary<string, string>? buffers = null, CancellationToken cancellation = default)
        => Task.Run(async () =>
        {
            cancellation.ThrowIfCancellationRequested();
            List<ProjectSearchHit> hits = []; List<string> warnings = [];
            if (string.IsNullOrWhiteSpace(query)) return new ProjectSearchResult(hits, 0, false, warnings);
            root = Path.GetFullPath(root);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            var files = await CandidatesAsync(root, warnings, cancellation);
            var searched = 0;
            foreach (var path in files)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!File.Exists(path) || LinkedPath(root, path)) continue;
                var relative = Path.GetRelativePath(root, path);
                if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(Excluded.Contains)) continue;
                searched++;
                if (mode != ProjectSearchMode.Contents && relative.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add(new(path, relative, 0, 0, query.Length, "Filename match"));
                    if (hits.Count >= ResultLimit) return new(hits, searched, true, warnings);
                }
                if (mode == ProjectSearchMode.FileNames) continue;
                string text;
                try
                {
                    text = buffers is not null && buffers.TryGetValue(path, out var buffer) ? buffer : (await TextFile.OpenAsync(path)).Text;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Binary/oversized files are expected in projects; unreadable files remain visible in warnings.
                    if (ex is UnauthorizedAccessException) warnings.Add($"{relative}: {ex.Message}");
                    continue;
                }
                using var reader = new StringReader(text);
                var lineNumber = 0;
                while (reader.ReadLine() is { } line)
                {
                    cancellation.ThrowIfCancellationRequested(); lineNumber++;
                    var start = 0;
                    while (start <= line.Length && line.IndexOf(query, start, StringComparison.OrdinalIgnoreCase) is var index && index >= 0)
                    {
                        var previewStart = Math.Max(0, index - 45);
                        var preview = (previewStart > 0 ? "…" : "") + line.Substring(previewStart, Math.Min(220, line.Length - previewStart)).Trim();
                        hits.Add(new(path, relative, lineNumber, index + 1, query.Length, preview));
                        if (hits.Count >= ResultLimit) return new(hits, searched, true, warnings);
                        start = index + query.Length;
                    }
                }
            }
            return new(hits, searched, false, warnings);
        }, cancellation);

    private static async Task<IReadOnlyList<string>> CandidatesAsync(string root, List<string> warnings, CancellationToken cancellation)
    {
        try
        {
            var git = await ProcessRunner.RunAsync(new("git", ["ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", "."], root), cancellation: cancellation);
            if (git.Success)
                return git.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(p => Path.GetFullPath(p, root))
                    .Where(p => Inside(root, p)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (System.ComponentModel.Win32Exception) { /* Non-Git folders still support search. */ }
        List<string> files = [];
        Stack<string> pending = new(); pending.Push(root);
        while (pending.TryPop(out var folder))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                files.AddRange(Directory.EnumerateFiles(folder));
                foreach (var child in Directory.EnumerateDirectories(folder))
                    if (!Excluded.Contains(Path.GetFileName(child)) && (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add($"{folder}: {ex.Message}"); }
        }
        return files.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static bool Inside(string root, string path) => Path.GetRelativePath(root, path) is var relative
        && !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    private static bool LinkedPath(string root, string path)
    {
        try
        {
            for (var current = path; !string.Equals(current, root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current)!)
                if (string.IsNullOrEmpty(current) || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }
}
