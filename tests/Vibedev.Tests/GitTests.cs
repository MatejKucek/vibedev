using Vibedev.Core;

namespace Vibedev.Tests;

public sealed class GitTests
{
    private static async Task Git(string path, params string[] args)
    {
        var result = await ProcessRunner.RunAsync(new("git", args, path)); Assert.True(result.Success, result.Output);
    }
    private static async Task Configure(string path)
    {
        await Git(path, "config", "user.name", "Vibedev Test"); await Git(path, "config", "user.email", "test@example.invalid");
        await Git(path, "config", "commit.gpgsign", "false"); await Git(path, "config", "pull.rebase", "false");
        await Git(path, "config", "core.hooksPath", System.IO.Path.Combine(path, ".no-hooks"));
    }
    [Fact]
    public async Task CommitAllPushAndPullWorkUsingExistingGit()
    {
        using var temp = new TempDirectory(); var remote = System.IO.Path.Combine(temp.Path, "remote.git");
        await Git(temp.Path, "init", "--bare", "--initial-branch=main", remote);
        var first = System.IO.Path.Combine(temp.Path, "first"); var second = System.IO.Path.Combine(temp.Path, "second");
        await Git(temp.Path, "clone", remote, first); await Configure(first);
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(first, "first.txt"), "hello");
        var service = new GitService();
        Assert.True((await service.CommitAllAsync(first, "Initial \"quoted\" message & $(literal)")).Success);
        Assert.True((await service.PushAsync(first)).Success);
        await Git(temp.Path, "clone", remote, second); await Configure(second);
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(first, "second.txt"), "new file");
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(first, "first.txt"), "changed");
        Assert.Equal(2, (await service.StatusAsync(first)).Changes);
        Assert.True((await service.CommitAllAsync(first, "Both changes")).Success); Assert.True((await service.PushAsync(first)).Success);
        Assert.True((await service.PullAsync(second)).Success);
        Assert.Equal("new file", await System.IO.File.ReadAllTextAsync(System.IO.Path.Combine(second, "second.txt")));
        Assert.Equal(0, (await service.StatusAsync(second)).Changes);
    }
    [Fact]
    public async Task CommitAllAndSyncStagesChangesPullsRemoteCommitsAndPushes()
    {
        using var temp = new TempDirectory(); var remote = System.IO.Path.Combine(temp.Path, "remote.git");
        await Git(temp.Path, "init", "--bare", "--initial-branch=main", remote);
        var first = System.IO.Path.Combine(temp.Path, "first"); var second = System.IO.Path.Combine(temp.Path, "second");
        await Git(temp.Path, "clone", remote, first); await Configure(first);
        var service = new GitService();
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(first, "delete.txt"), "delete me");
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(first, "edit.txt"), "base");
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(first, ".gitignore"), "ignored.txt\n");
        Assert.True((await service.CommitAllAsync(first, "Initial")).Success); await Git(first, "push");
        await Git(temp.Path, "clone", remote, second); await Configure(second);
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(first, "remote.txt"), "pulled from remote");
        Assert.True((await service.CommitAllAsync(first, "Remote update")).Success); await Git(first, "push");
        System.IO.File.Delete(System.IO.Path.Combine(second, "delete.txt"));
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(second, "edit.txt"), "updated");
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(second, "new.txt"), "new file");
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(second, "ignored.txt"), "stay ignored");
        await Git(second, "add", "edit.txt");
        const string message = "All changes: \"quoted\" & $(literal) ž";
        var output = new List<string>();
        var result = await service.CommitAllAndSyncAsync(second, message, output.Add);
        Assert.True(result.Success, result.Output);
        Assert.Equal(new[] { "git add --all", "git commit", "git pull", "git push" }, output.Where(line => line.StartsWith("git ")));
        Assert.Equal(0, (await service.StatusAsync(second)).Changes);
        Assert.Equal("pulled from remote", await System.IO.File.ReadAllTextAsync(System.IO.Path.Combine(second, "remote.txt")));
        await Git(first, "pull");
        Assert.False(System.IO.File.Exists(System.IO.Path.Combine(first, "delete.txt")));
        Assert.False(System.IO.File.Exists(System.IO.Path.Combine(first, "ignored.txt")));
        Assert.Equal("updated", await System.IO.File.ReadAllTextAsync(System.IO.Path.Combine(first, "edit.txt")));
        Assert.Equal("new file", await System.IO.File.ReadAllTextAsync(System.IO.Path.Combine(first, "new.txt")));
        var log = await ProcessRunner.RunAsync(new("git", ["log", "--format=%B"], first, OutputEncoding: System.Text.Encoding.UTF8));
        Assert.Contains(message, log.Output);
    }
    [Fact]
    public async Task CommitAllAndSyncStopsWhenCommitFails()
    {
        using var temp = new TempDirectory(); await Git(temp.Path, "init", "--initial-branch=main"); await Configure(temp.Path);
        var output = new List<string>();
        var result = await new GitService().CommitAllAndSyncAsync(temp.Path, "Nothing to commit", output.Add);
        Assert.False(result.Success); Assert.Contains("git commit failed", result.Output);
        Assert.DoesNotContain("git pull", output); Assert.DoesNotContain("git push", output);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitAllAndSyncReportsPullOrPushFailureAndKeepsLocalCommit(bool failPush)
    {
        using var temp = new TempDirectory(); var remote = System.IO.Path.Combine(temp.Path, "remote.git");
        await Git(temp.Path, "init", "--bare", "--initial-branch=main", remote);
        var first = System.IO.Path.Combine(temp.Path, "first"); var second = System.IO.Path.Combine(temp.Path, "second");
        await Git(temp.Path, "clone", remote, first); await Configure(first);
        var service = new GitService(); await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(first, "file.txt"), "base\n");
        Assert.True((await service.CommitAllAsync(first, "Initial")).Success); await Git(first, "push");
        await Git(temp.Path, "clone", remote, second); await Configure(second);
        if (failPush) await Git(second, "config", "remote.origin.pushurl", System.IO.Path.Combine(temp.Path, "missing.git"));
        else
        {
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(first, "file.txt"), "remote change\n");
            Assert.True((await service.CommitAllAsync(first, "Remote update")).Success); await Git(first, "push");
        }
        var remoteHead = await ProcessRunner.RunAsync(new("git", ["rev-parse", "refs/heads/main"], remote));
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(second, "file.txt"), "local change\n");
        var output = new List<string>();
        var result = await service.CommitAllAndSyncAsync(second, "Keep this local commit", output.Add);
        Assert.False(result.Success); Assert.Contains(failPush ? "git push failed" : "git pull failed", result.Output);
        if (!failPush) Assert.DoesNotContain("git push", output);
        var localMessage = await ProcessRunner.RunAsync(new("git", ["log", "-1", "--format=%s"], second));
        Assert.Equal("Keep this local commit", localMessage.Output);
        Assert.Equal(remoteHead.Output, (await ProcessRunner.RunAsync(new("git", ["rev-parse", "refs/heads/main"], remote))).Output);
        if (!failPush) Assert.Contains(await service.ChangesAsync(second), file => file.IndexStatus == 'U');
    }
    [Fact]
    public async Task FailedPullLeavesLocalChangesAndReportsFailure()
    {
        using var temp = new TempDirectory(); var remote = System.IO.Path.Combine(temp.Path, "remote.git");
        await Git(temp.Path, "init", "--bare", "--initial-branch=main", remote);
        var first = System.IO.Path.Combine(temp.Path, "first"); var second = System.IO.Path.Combine(temp.Path, "second");
        await Git(temp.Path, "clone", remote, first); await Configure(first);
        var service = new GitService(); var file = System.IO.Path.Combine(first, "file.txt"); await System.IO.File.WriteAllTextAsync(file, "base");
        await service.CommitAllAsync(first, "initial"); await service.PushAsync(first);
        await Git(temp.Path, "clone", remote, second); await Configure(second);
        await System.IO.File.WriteAllTextAsync(file, "remote change"); await service.CommitAllAsync(first, "update"); await service.PushAsync(first);
        var localFile = System.IO.Path.Combine(second, "file.txt"); await System.IO.File.WriteAllTextAsync(localFile, "local dirty change");
        var result = await service.PullAsync(second); Assert.False(result.Success); Assert.NotEmpty(result.Output);
        Assert.Equal("local dirty change", await System.IO.File.ReadAllTextAsync(localFile));
        var stashes = await ProcessRunner.RunAsync(new("git", ["stash", "list"], second)); Assert.Empty(stashes.Output);
    }
    [Fact]
    public async Task CommitRequiresMessage()
    {
        using var temp = new TempDirectory(); await Assert.ThrowsAsync<ArgumentException>(() => new GitService().CommitAllAsync(temp.Path, " "));
        await Assert.ThrowsAsync<ArgumentException>(() => new GitService().CommitAllAndSyncAsync(temp.Path, " "));
    }
    [Fact]
    public async Task CancellationTerminatesTheChildProcess()
    {
        using var temp = new TempDirectory(); using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunAsync(new("powershell.exe", ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"], temp.Path), cancellation: cancellation.Token));
    }
}
