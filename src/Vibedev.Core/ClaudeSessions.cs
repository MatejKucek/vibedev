using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Vibedev.Core;

public sealed record ClaudeSession(int Pid, string SessionId, string Cwd, string Name, string Status,
    string? WaitingFor, string? ProcessStart);

public sealed record ClaudeSessionSnapshot(IReadOnlyList<ClaudeSession> Sessions, string? Warning);

// Read metadata only. Never open .key files, transcripts, or the messaging socket.
public sealed class ClaudeSessionReader(string? directory = null, Func<int, string?, bool>? isAlive = null)
{
    private readonly string directory = directory ?? Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"), "sessions");
    private readonly Func<int, string?, bool> isAlive = isAlive ?? IsClaudeProcess;
    private readonly Dictionary<string, ClaudeSession> previous = new(StringComparer.OrdinalIgnoreCase);

    public ClaudeSessionSnapshot Read()
    {
        string[] files;
        try { files = Directory.GetFiles(directory, "*.json").Order(StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch (DirectoryNotFoundException) { previous.Clear(); return new([], null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(previous.Values.ToArray(), "Cannot read Claude sessions: " + ex.Message); }
        var next = new Dictionary<string, ClaudeSession>(StringComparer.OrdinalIgnoreCase);
        List<string> warnings = [];
        foreach (var file in files)
        {
            ClaudeSession? session = null;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var json = JsonDocument.Parse(stream);
                var root = json.RootElement;
                string? Text(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                var pid = root.GetProperty("pid").GetInt32();
                var cwd = Text("cwd"); var status = Text("status");
                if (pid <= 0 || string.IsNullOrWhiteSpace(cwd) || string.IsNullOrWhiteSpace(status) || !Path.IsPathFullyQualified(cwd))
                    throw new JsonException("Missing process, working folder or status.");
                session = new(pid, Text("sessionId") ?? "", Path.GetFullPath(cwd), Text("name") ?? "Claude session", status,
                    Text("waitingFor"), Text("procStart"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException)
            {
                warnings.Add($"{Path.GetFileName(file)}: {ex.Message}");
                previous.TryGetValue(file, out session); // Claude may be rewriting the file. Keep the last verified state.
            }
            if (session is null) continue;
            try { if (isAlive(session.Pid, session.ProcessStart)) next[file] = session; }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                warnings.Add($"{Path.GetFileName(file)}: Cannot verify process: {ex.Message}");
                if (previous.TryGetValue(file, out var cached)) next[file] = cached;
            }
        }
        previous.Clear(); foreach (var pair in next) previous.Add(pair.Key, pair.Value);
        return new(next.Values.ToArray(), warnings.Count == 0 ? null : "Claude session data incomplete: " + string.Join("; ", warnings));
    }

    private static bool IsClaudeProcess(int pid, string? processStart)
    {
        Process process;
        try { process = Process.GetProcessById(pid); }
        catch (ArgumentException) { return false; }
        using (process)
        {
            if (process.HasExited || !process.ProcessName.Equals("claude", StringComparison.OrdinalIgnoreCase)) return false;
            // Claude stores the Windows process creation FILETIME. Checking it prevents PID reuse matching an old session.
            return processStart is null || (long.TryParse(processStart, CultureInfo.InvariantCulture, out var stamp) &&
                process.StartTime.ToFileTimeUtc() == stamp);
        }
    }
}

public static class ClaudeProjectMatcher
{
    public static IReadOnlyList<ClaudeSession> Match(string projectPath, IEnumerable<ClaudeSession> sessions)
    {
        var project = Normalize(projectPath); var projectGit = CommonGitDirectory(project);
        return sessions.Where(session =>
        {
            var cwd = Normalize(session.Cwd); var sessionGit = CommonGitDirectory(cwd);
            if (projectGit is not null && sessionGit is not null) return projectGit.Equals(sessionGit, StringComparison.OrdinalIgnoreCase);
            var prefix = Path.EndsInDirectorySeparator(project) ? project : project + Path.DirectorySeparatorChar;
            return cwd.Equals(project, StringComparison.OrdinalIgnoreCase) || cwd.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }).ToArray();
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string? CommonGitDirectory(string path)
    {
        try
        {
            for (var folder = new DirectoryInfo(path); folder is not null; folder = folder.Parent)
            {
                var marker = Path.Combine(folder.FullName, ".git"); string gitDirectory;
                if (Directory.Exists(marker)) gitDirectory = marker;
                else if (File.Exists(marker))
                {
                    var link = File.ReadAllText(marker).Trim();
                    if (!link.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase)) return null;
                    gitDirectory = Path.GetFullPath(link[7..].Trim(), folder.FullName);
                }
                else continue;
                var common = Path.Combine(gitDirectory, "commondir");
                return Normalize(File.Exists(common) ? Path.GetFullPath(File.ReadAllText(common).Trim(), gitDirectory) : gitDirectory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }
}

public enum ClaudeActivityLight { Gray, Red, Blue, Orange, Green }

public sealed class ProjectClaudeActivity
{
    public bool HasRun { get; private set; }
    public IReadOnlyList<ClaudeSession> Sessions { get; private set; } = [];
    public string? Warning { get; private set; }
    public void Update(IReadOnlyList<ClaudeSession> sessions, string? warning = null)
    {
        Sessions = sessions; Warning = warning;
        if (sessions.Any(s => s.Status is "busy" or "waiting")) HasRun = true;
    }
    public ClaudeActivityLight Light(int openTasks) =>
        Sessions.Any(s => s.Status == "waiting") ? ClaudeActivityLight.Orange :
        Sessions.Any(s => s.Status == "busy") ? ClaudeActivityLight.Blue :
        Sessions.Any(s => s.Status != "idle") ? ClaudeActivityLight.Gray :
        HasRun ? ClaudeActivityLight.Green : openTasks > 0 ? ClaudeActivityLight.Red : ClaudeActivityLight.Gray;

    public string Description(int openTasks)
    {
        var label = Light(openTasks) switch
        {
            ClaudeActivityLight.Orange => "Claude needs permission or input",
            ClaudeActivityLight.Blue => "Claude is running",
            ClaudeActivityLight.Green => "Claude activity stopped (observed during this app run)",
            ClaudeActivityLight.Red => "Open tasks; Claude is not running",
            _ => Sessions.Any(s => s.Status != "idle") ? "Unknown Claude session status" : "No Claude activity observed"
        };
        var detail = string.Join("\n", Sessions.Select(s => $"{s.Name}: {s.Status}{(s.WaitingFor is null ? "" : " — " + s.WaitingFor)}\n{s.Cwd}"));
        return $"{label}\n{openTasks} open tasks" + (detail.Length == 0 ? "" : "\n" + detail) + (Warning is null ? "" : "\n" + Warning);
    }
}
