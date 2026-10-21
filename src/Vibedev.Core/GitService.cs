using System.Collections.Concurrent;
using System.Text;

namespace Vibedev.Core;

public sealed record GitStatus(bool IsRepository, string Branch, int Changes, string? Error = null)
{
    /// <summary>Compact branch label: "main...origin/main [ahead 1, behind 2]" becomes "main ↑1 ↓2".</summary>
    public string Summary => IsRepository ? SummarizeBranch(Branch) : Branch;
    public static string SummarizeBranch(string header)
    {
        var bracket = header.IndexOf(" [", StringComparison.Ordinal);
        var name = bracket >= 0 ? header[..bracket] : header;
        var dots = name.IndexOf("...", StringComparison.Ordinal);
        if (dots >= 0) name = name[..dots];
        if (bracket < 0) return name;
        var tracking = header[(bracket + 2)..].TrimEnd(']').Split(", ").Select(part =>
            part.StartsWith("ahead ", StringComparison.Ordinal) ? "↑" + part[6..] :
            part.StartsWith("behind ", StringComparison.Ordinal) ? "↓" + part[7..] :
            part == "gone" ? "upstream gone" : part);
        return name + " " + string.Join(" ", tracking);
    }
}
public sealed record GitCommit(string Hash, string ShortHash, string Subject, string Author, DateTimeOffset Date);
public sealed record GitCommitFile(string Path, string? OriginalPath, char Status);
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

    public async Task<IReadOnlyList<GitCommit>> RecentCommitsAsync(string path, int count = 100, CancellationToken cancellation = default)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        var result = await ExecuteAsync(path, ["log", "--no-color", "--no-notes", "--no-show-signature", $"--max-count={count}", "-z", "--format=%H%x00%h%x00%s%x00%an%x00%aI"], null, cancellation);
        if (!result.Success)
        {
            var repository = await ExecuteAsync(path, ["rev-parse", "--git-dir"], null, cancellation);
            if (!repository.Success) throw new IOException(repository.Output);
            var head = await ExecuteAsync(path, ["rev-parse", "--verify", "--quiet", "HEAD"], null, cancellation);
            if (!head.Success) return [];
            throw new IOException(result.Output);
        }
        var fields = result.Output.Split('\0'); var commits = new List<GitCommit>();
        for (var i = 0; i + 4 < fields.Length; i += 5)
            commits.Add(new(fields[i], fields[i + 1], fields[i + 2], fields[i + 3], DateTimeOffset.Parse(fields[i + 4], System.Globalization.CultureInfo.InvariantCulture)));
        return commits;
    }

    public async Task<IReadOnlyList<GitCommitFile>> CommitFilesAsync(string path, string hash, CancellationToken cancellation = default)
    {
        ValidateCommitHash(hash);
        // Merge commits show their changes against the first parent; root commits show all added files.
        var result = await ExecuteAsync(path, ["show", "--no-color", "--no-notes", "--no-show-signature", "--format=", "--name-status", "-z", "--find-renames", "--first-parent", "--diff-merges=first-parent", "--root", "--no-ext-diff", "--no-textconv", hash, "--"], null, cancellation);
        if (!result.Success) throw new IOException(result.Output);
        var entries = result.Output.Split('\0'); var files = new List<GitCommitFile>();
        for (var i = 0; i + 1 < entries.Length;)
        {
            var status = entries[i++]; if (status.Length == 0) continue;
            var name = entries[i++]; string? original = null;
            if (status[0] is 'R' or 'C' && i < entries.Length) { original = name; name = entries[i++]; }
            files.Add(new(name, original, status[0]));
        }
        return files;
    }

    public async Task<string> CommitDiffAsync(string path, string hash, GitCommitFile file, CancellationToken cancellation = default)
    {
        ValidateCommitHash(hash);
        // History paths are repository-relative, even when the project is opened at a subfolder.
        var paths = file.OriginalPath is null ? new[] { file.Path } : new[] { file.Path, file.OriginalPath };
        var result = await ExecuteAsync(path, ["show", "--no-color", "--no-notes", "--no-show-signature", "--format=", "--patch", "--find-renames", "--first-parent", "--diff-merges=first-parent", "--root", "--no-ext-diff", "--no-textconv", hash, "--", .. paths.Select(name => ":(top,literal)" + name)], null, cancellation);
        if (!result.Success) throw new IOException(result.Output);
        return result.Output.Length == 0 ? "No text differences for this file in this commit." : result.Output;
    }

    private static void ValidateCommitHash(string hash)
    {
        if (hash.Length is not (40 or 64) || !hash.All(char.IsAsciiHexDigit)) throw new ArgumentException("Use a full commit hash.", nameof(hash));
    }

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
    /// <summary>Throws away working-tree changes: tracked files are restored from the index, untracked files are deleted. Irreversible.</summary>
    public async Task<CommandResult> DiscardAsync(string path, IEnumerable<GitFileChange> files, CancellationToken cancellation = default)
    {
        var all = files.ToArray();
        if (all.Length == 0) throw new ArgumentException("Select files to discard.");
        var tracked = all.Where(f => !f.Untracked).Select(f => f.Path).Distinct().ToArray();
        var untracked = all.Where(f => f.Untracked).Select(f => f.Path).Distinct().ToArray();
        CommandResult result = new(0, "");
        if (tracked.Length > 0)
        {
            result = await ExecuteAsync(path, ["--literal-pathspecs", "restore", "--worktree", "--", .. tracked], null, cancellation);
            if (!result.Success) return result;
        }
        if (untracked.Length > 0)
        {
            var clean = await ExecuteAsync(path, ["--literal-pathspecs", "clean", "-f", "-d", "--quiet", "--", .. untracked], null, cancellation);
            return clean.Success ? new(0, (result.Output + "\n" + clean.Output).Trim()) : clean;
        }
        return result;
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

    public async Task<CommandResult> CommitAllAndSyncAsync(string path, string message, Action<string>? output = null,
        CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("Enter a commit message.", nameof(message));
        var gate = locks.GetOrAdd(System.IO.Path.GetFullPath(path), _ => new(1));
        await gate.WaitAsync(cancellation);
        try
        {
            string[][] commands = [["add", "--all"], ["commit", "-m", message], ["pull"], ["push"]];
            var result = new CommandResult(0, "");
            foreach (var args in commands)
            {
                output?.Invoke("git " + (args[0] == "add" ? "add --all" : args[0]));
                result = await GitAsync(path, args, output, cancellation);
                if (!result.Success)
                {
                    output?.Invoke($"git {args[0]} failed · exit {result.ExitCode}");
                    return new(result.ExitCode, $"git {args[0]} failed:{Environment.NewLine}{result.Output}");
                }
            }
            return result;
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
