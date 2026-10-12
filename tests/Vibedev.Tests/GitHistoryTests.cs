using Vibedev.Core;

namespace Vibedev.Tests;

public sealed class GitHistoryTests
{
    private static async Task Git(string path, params string[] args)
    {
        var result = await ProcessRunner.RunAsync(new("git", args, path));
        Assert.True(result.Success, result.Output);
    }

    private static async Task Initialize(string path)
    {
        await Git(path, "init", "--initial-branch=main");
        await Git(path, "config", "user.name", "History Test");
        await Git(path, "config", "user.email", "history@example.invalid");
        await Git(path, "config", "commit.gpgsign", "false");
        await Git(path, "config", "core.hooksPath", System.IO.Path.Combine(path, ".no-hooks"));
        // Machine/user preferences must not inject ANSI escapes into parsed history.
        await Git(path, "config", "color.ui", "always");
    }

    [Fact]
    public async Task HistoryIsNewestFirstAndListsRootChangesRenamesAndDeletedFiles()
    {
        using var temp = new TempDirectory(); await Initialize(temp.Path);
        var service = new GitService();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "old name.txt"), "Keep this content when renamed.\n");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "delete.txt"), "delete\n");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "edit.txt"), "before\n");
        Assert.True((await service.CommitAllAsync(temp.Path, "Initial ž commit")).Success);
        var initial = Assert.Single(await service.RecentCommitsAsync(temp.Path));
        Assert.Equal("Initial ž commit", initial.Subject);
        Assert.Equal("History Test", initial.Author);
        Assert.Equal(40, initial.Hash.Length);
        Assert.StartsWith(initial.ShortHash, initial.Hash);
        var initialFiles = await service.CommitFilesAsync(temp.Path, initial.Hash);
        Assert.Equal(3, initialFiles.Count); Assert.All(initialFiles, file => Assert.Equal('A', file.Status));
        var rootDiff = await service.CommitDiffAsync(temp.Path, initial.Hash, initialFiles.Single(file => file.Path == "edit.txt"));
        Assert.Contains("--- /dev/null", rootDiff); Assert.Contains("+before", rootDiff);
        Assert.DoesNotContain("old name.txt", rootDiff);

        const string renamed = "renamed [ž] file.txt";
        File.Move(Path.Combine(temp.Path, "old name.txt"), Path.Combine(temp.Path, renamed));
        File.Delete(Path.Combine(temp.Path, "delete.txt"));
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "edit.txt"), "after\n");
        Directory.CreateDirectory(Path.Combine(temp.Path, "nested"));
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "nested", "new file.txt"), "new\n");
        const string subject = "Update \"quoted\" & $(literal) ž";
        Assert.True((await service.CommitAllAsync(temp.Path, subject)).Success);
        var history = await service.RecentCommitsAsync(temp.Path);
        Assert.Equal(new[] { subject, initial.Subject }, history.Select(commit => commit.Subject));
        Assert.Equal(history[0], Assert.Single(await service.RecentCommitsAsync(temp.Path, 1)));
        var changed = await service.CommitFilesAsync(temp.Path, history[0].Hash);
        Assert.Equal(4, changed.Count);
        Assert.Contains(changed, file => file is { Path: renamed, OriginalPath: "old name.txt", Status: 'R' });
        Assert.Contains(changed, file => file is { Path: "delete.txt", Status: 'D' });
        Assert.Contains(changed, file => file is { Path: "edit.txt", Status: 'M' });
        Assert.Contains(changed, file => file is { Path: "nested/new file.txt", Status: 'A' });
        // History remains repository-wide when the project is a subfolder.
        Assert.Equal(history, await service.RecentCommitsAsync(Path.Combine(temp.Path, "nested")));
        Assert.Equal(changed, await service.CommitFilesAsync(Path.Combine(temp.Path, "nested"), history[0].Hash));
        var edit = changed.Single(file => file.Path == "edit.txt");
        var editDiff = await service.CommitDiffAsync(temp.Path, history[0].Hash, edit);
        Assert.Contains("-before", editDiff); Assert.Contains("+after", editDiff);
        Assert.DoesNotContain("delete.txt", editDiff); Assert.DoesNotContain("\u001b", editDiff, StringComparison.Ordinal);
        Assert.Equal(editDiff, await service.CommitDiffAsync(Path.Combine(temp.Path, "nested"), history[0].Hash, edit));
        var deletedDiff = await service.CommitDiffAsync(temp.Path, history[0].Hash, changed.Single(file => file.Status == 'D'));
        Assert.Contains("+++ /dev/null", deletedDiff); Assert.Contains("-delete", deletedDiff);
        var renamedDiff = await service.CommitDiffAsync(temp.Path, history[0].Hash, changed.Single(file => file.Status == 'R'));
        Assert.Contains("rename from old name.txt", renamedDiff); Assert.Contains("rename to", renamedDiff);
        // A history diff always compares committed content, regardless of current index/worktree changes.
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "edit.txt"), "Uncommitted text\n");
        await Git(temp.Path, "add", "edit.txt");
        Assert.Equal(editDiff, await service.CommitDiffAsync(temp.Path, history[0].Hash, edit));
    }

    [Fact]
    public async Task MergeUsesFirstParentAndEmptyCommitHasNoFiles()
    {
        using var temp = new TempDirectory(); await Initialize(temp.Path);
        var service = new GitService();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "base.txt"), "base\n");
        Assert.True((await service.CommitAllAsync(temp.Path, "Initial")).Success);
        await Git(temp.Path, "switch", "-c", "feature");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "feature.txt"), "feature\n");
        Assert.True((await service.CommitAllAsync(temp.Path, "Feature")).Success);
        await Git(temp.Path, "switch", "main");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "main.txt"), "main\n");
        Assert.True((await service.CommitAllAsync(temp.Path, "Main")).Success);
        await Git(temp.Path, "merge", "--no-ff", "feature", "-m", "Merge feature");
        var merge = Assert.Single(await service.RecentCommitsAsync(temp.Path, 1));
        var feature = Assert.Single(await service.CommitFilesAsync(temp.Path, merge.Hash));
        Assert.Equal(new GitCommitFile("feature.txt", null, 'A'), feature);
        Assert.Contains("+feature", await service.CommitDiffAsync(temp.Path, merge.Hash, feature));
        await Git(temp.Path, "commit", "--allow-empty", "-m", "Empty");
        var empty = Assert.Single(await service.RecentCommitsAsync(temp.Path, 1));
        Assert.Empty(await service.CommitFilesAsync(temp.Path, empty.Hash));
    }

    [Fact]
    public async Task EmptyRepositoryHasNoCommitsAndNonRepositoryReportsAnError()
    {
        using var temp = new TempDirectory(); var service = new GitService();
        await Assert.ThrowsAsync<IOException>(() => service.RecentCommitsAsync(temp.Path));
        await Initialize(temp.Path);
        Assert.Empty(await service.RecentCommitsAsync(temp.Path));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.RecentCommitsAsync(temp.Path, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CommitFilesAsync(temp.Path, "--all"));
    }
}
