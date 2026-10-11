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
    }
    [Fact]
    public async Task CancellationTerminatesTheChildProcess()
    {
        using var temp = new TempDirectory(); using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunAsync(new("powershell.exe", ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"], temp.Path), cancellation: cancellation.Token));
    }
}
