using System.Collections.Concurrent;
using System.Text;

namespace Vibedev.Core;

public sealed record GitStatus(bool IsRepository, string Branch, int Changes, string? Error = null);
public sealed record GitFileChange(string Path, string? OriginalPath, char IndexStatus, char WorkTreeStatus)
{
    public bool Staged => IndexStatus is not ' ' and not '?';
    public bool Unstaged => WorkTreeStatus != ' ';
    public bool Untracked => IndexStatus == '?';
}

public sealed class GitService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<GitStatus> StatusAsync(string path, CancellationToken cancellation = default)
    {
        var result = await ExecuteAsync(path, ["status", "--porcelain=v1", "--branch"], null, cancellation);
        if (!result.Success) return new(false, "Unavailable", 0, result.Output);
        var lines = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return new(true, lines.FirstOrDefault()?.Replace("## ", "") ?? "No commits", Math.Max(0, lines.Length - 1));
    }

    public Task<CommandResult> PullAsync(string path, Action<string>? output = null, CancellationToken cancellation = default)
        => ExecuteAsync(path, ["pull"], output, cancellation);

    public Task<CommandResult> PushAsync(string path, Action<string>? output = null, CancellationToken cancellation = default)
        => ExecuteAsync(path, ["push"], output, cancellation);

    public async Task<IReadOnlyList<GitFileChange>> ChangesAsync(string path, CancellationToken cancellation = default)
    {
        var root = await ExecuteAsync(path, ["rev-parse", "--show-toplevel"], null, cancellation);
        if (!root.Success) throw new IOException(root.Output);
        var result = await ExecuteAsync(path, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], null, cancellation);
        if (!result.Success) throw new IOException(result.Output);
        var entries = result.Output.Split('\0'); var changes = new List<GitFileChange>();
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i]; if (entry.Length < 4) continue;
            string Relative(string name) => System.IO.Path.GetRelativePath(path, System.IO.Path.Combine(root.Output.Trim(), name));
            string? original = null;
            if ((entry[0] is 'R' or 'C' || entry[1] is 'R' or 'C') && i + 1 < entries.Length) original = Relative(entries[++i]);
            changes.Add(new(Relative(entry[3..]), original, entry[0], entry[1]));
        }
        return changes;
    }
    public Task<CommandResult> StageAsync(string path, IEnumerable<GitFileChange> files, CancellationToken cancellation = default)
    {
        var paths = files.SelectMany(f => f.OriginalPath is not null && f.WorkTreeStatus is 'R' or 'C' ? new[] { f.Path, f.OriginalPath } : new[] { f.Path }).Distinct().ToArray();
        if (paths.Length == 0) throw new ArgumentException("Select files to stage.");
        return ExecuteAsync(path, ["--literal-pathspecs", "add", "--all", "--", .. paths], null, cancellation);
    }
    public async Task<CommandResult> UnstageAsync(string path, IEnumerable<GitFileChange> files, CancellationToken cancellation = default)
    {
        var paths = files.SelectMany(f => f.OriginalPath is null ? new[] { f.Path } : new[] { f.Path, f.OriginalPath }).Distinct().ToArray();
        if (paths.Length == 0) throw new ArgumentException("Select files to unstage.");
        var head = await ExecuteAsync(path, ["rev-parse", "--verify", "HEAD"], null, cancellation);
        return head.Success
            ? await ExecuteAsync(path, ["--literal-pathspecs", "reset", "--quiet", "HEAD", "--", .. paths], null, cancellation)
            : await ExecuteAsync(path, ["--literal-pathspecs", "rm", "--cached", "--force", "--quiet", "--", .. paths], null, cancellation);
    }
    public Task<CommandResult> CommitStagedAsync(string path, string message, Action<string>? output = null, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("Enter a commit message.", nameof(message));
        return ExecuteAsync(path, ["commit", "-m", message], output, cancellation);
    }
    public async Task<string> DiffAsync(string path, GitFileChange file, bool staged, CancellationToken cancellation = default)
    {
        if (!staged && file.Untracked)
        {
            try
            {
                var text = (await TextFile.OpenAsync(System.IO.Path.GetFullPath(file.Path, path))).Text;
                return $"--- /dev/null\n+++ b/{file.Path}\n" + string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(line => "+" + line));
            }
            catch (IOException ex) { return "New file: " + ex.Message; }
        }
        var result = await ExecuteAsync(path, ["--literal-pathspecs", "diff", "--no-ext-diff", "--no-textconv", "--no-color", .. staged ? new[] { "--cached" } : Array.Empty<string>(), "--", file.Path, .. file.OriginalPath is null ? Array.Empty<string>() : new[] { file.OriginalPath }], null, cancellation);
        if (!result.Success) throw new IOException(result.Output);
        return result.Output.Length == 0 ? "No text differences (the file may have changed since refresh)." : result.Output;
    }

    public async Task<CommandResult> CommitAllAsync(string path, string message, Action<string>? output = null,
        CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("Enter a commit message.", nameof(message));
        var gate = locks.GetOrAdd(System.IO.Path.GetFullPath(path), _ => new(1));
        await gate.WaitAsync(cancellation);
        try
        {
            var stage = await GitAsync(path, ["add", "--all"], output, cancellation);
            if (!stage.Success) return stage;
            return await GitAsync(path, ["commit", "-m", message], output, cancellation);
        }
        finally { gate.Release(); }
    }

    private async Task<CommandResult> ExecuteAsync(string path, string[] args, Action<string>? output, CancellationToken cancellation)
    {
        var gate = locks.GetOrAdd(System.IO.Path.GetFullPath(path), _ => new(1));
        await gate.WaitAsync(cancellation);
        try { return await GitAsync(path, args, output, cancellation); }
        finally { gate.Release(); }
    }

    private static Task<CommandResult> GitAsync(string path, string[] args, Action<string>? output, CancellationToken cancellation)
        => ProcessRunner.RunAsync(new("git", ["--no-optional-locks", .. args], path, new Dictionary<string, string?>
        {
            ["GIT_TERMINAL_PROMPT"] = "0", ["GCM_INTERACTIVE"] = "auto"
        }, OutputEncoding: Encoding.UTF8), output, cancellation);
}
