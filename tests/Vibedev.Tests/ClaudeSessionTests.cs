using System.Diagnostics;
using System.Text.Json;
using Vibedev.Core;

namespace Vibedev.Tests;

public sealed class ClaudeSessionTests
{
    private static ClaudeSession Session(string cwd, string status = "busy") => new(10, "session-a", cwd, "Fix project", status, null, "123");

    [Fact]
    public void ActivityTransitionsPreserveGreenWithOpenTasksAndAfterSessionExits()
    {
        var activity = new ProjectClaudeActivity();
        Assert.Equal(ClaudeActivityLight.Gray, activity.Light(0));
        Assert.Equal(ClaudeActivityLight.Red, activity.Light(3));
        activity.Update([Session(Path.GetTempPath(), "idle")]);
        Assert.Equal(ClaudeActivityLight.Red, activity.Light(3)); // An idle record alone is not evidence we observed work.
        activity.Update([Session(Path.GetTempPath())]);
        Assert.Equal(ClaudeActivityLight.Blue, activity.Light(3));
        activity.Update([Session(Path.GetTempPath(), "waiting") with { WaitingFor = "permission prompt" }]);
        Assert.Equal(ClaudeActivityLight.Orange, activity.Light(3));
        Assert.Contains("permission prompt", activity.Description(3));
        activity.Update([Session(Path.GetTempPath(), "idle")]);
        Assert.Equal(ClaudeActivityLight.Green, activity.Light(3));
        activity.Update([]);
        Assert.Equal(ClaudeActivityLight.Green, activity.Light(3));
        activity.Update([Session(Path.GetTempPath())]);
        Assert.Equal(ClaudeActivityLight.Blue, activity.Light(0));
    }

    [Fact]
    public void WaitingTakesPrecedenceAcrossSessionsAndUnknownStatusIsNotReportedAsFinished()
    {
        var activity = new ProjectClaudeActivity();
        activity.Update([Session(Path.GetTempPath()), Session(Path.GetTempPath(), "waiting")]);
        Assert.Equal(ClaudeActivityLight.Orange, activity.Light(0));
        activity.Update([Session(Path.GetTempPath(), "new-status")]);
        Assert.Equal(ClaudeActivityLight.Gray, activity.Light(2));
        Assert.Contains("Unknown", activity.Description(2));
    }

    [Fact]
    public void ReaderKeepsLastKnownLiveStateDuringPartialWritesAndDropsExitedOrDeletedSessions()
    {
        using var temp = new TempDirectory();
        var live = true;
        var reader = new ClaudeSessionReader(temp.Path, (pid, stamp) => live && pid == 10 && stamp == "123");
        var record = new { pid = 10, sessionId = "session-a", cwd = temp.Path, name = "Unicode ž task", status = "busy", procStart = "123" };
        var file = temp.File("10.json", JsonSerializer.Serialize(record));
        temp.File("10.secret.key", "Do not read");
        Assert.Equal("Unicode ž task", Assert.Single(reader.Read().Sessions).Name);
        File.WriteAllText(file, "{\"pid\":");
        var partial = reader.Read();
        Assert.Equal("busy", Assert.Single(partial.Sessions).Status);
        Assert.NotNull(partial.Warning);
        live = false;
        Assert.Empty(reader.Read().Sessions);
        live = true;
        File.WriteAllText(file, JsonSerializer.Serialize(record));
        Assert.Single(reader.Read().Sessions);
        File.Delete(file);
        Assert.Empty(reader.Read().Sessions);
        Assert.True(File.Exists(Path.Combine(temp.Path, "10.secret.key")));
    }

    [Fact]
    public void ReaderRejectsBadRecordsAndUsesProcessCreationIdentity()
    {
        using var temp = new TempDirectory();
        temp.File("bad.json", "{}");
        temp.File("relative.json", """{"pid":10,"cwd":"relative","status":"busy"}""");
        temp.File("reused.json", JsonSerializer.Serialize(new { pid = 10, cwd = temp.Path, status = "busy", procStart = "wrong" }));
        var reader = new ClaudeSessionReader(temp.Path, (_, start) => start == "123");
        var snapshot = reader.Read();
        Assert.Empty(snapshot.Sessions); Assert.NotNull(snapshot.Warning);
        using var current = Process.GetCurrentProcess();
        temp.File("other-process.json", JsonSerializer.Serialize(new { pid = current.Id, cwd = temp.Path, status = "busy", procStart = current.StartTime.ToFileTimeUtc().ToString() }));
        Assert.Empty(new ClaudeSessionReader(temp.Path).Read().Sessions);
        Assert.Empty(new ClaudeSessionReader(Path.Combine(temp.Path, "missing")).Read().Sessions);
    }

    [Fact]
    public void MatchingUsesFolderBoundariesAndSharedGitWorktreesButExcludesNestedRepositories()
    {
        using var temp = new TempDirectory();
        var project = Path.Combine(temp.Path, "app");
        temp.File("app/.git/HEAD", "ref: refs/heads/main");
        temp.File("app/.git/worktrees/feature/commondir", "../..");
        temp.File("feature/.git", "gitdir: ../app/.git/worktrees/feature");
        temp.File("app/nested/.git/HEAD", "ref: refs/heads/main");
        temp.File("clone/.git/HEAD", "ref: refs/heads/main");
        var direct = Session(project);
        var child = Session(Path.Combine(project, "src"));
        var worktree = Session(Path.Combine(temp.Path, "feature", "src"));
        var nested = Session(Path.Combine(project, "nested"));
        var sibling = Session(Path.Combine(temp.Path, "app-other"));
        var clone = Session(Path.Combine(temp.Path, "clone"));
        Assert.Equal(new[] { direct, child, worktree }, ClaudeProjectMatcher.Match(project, [direct, child, worktree, nested, sibling, clone]));
        Assert.Single(ClaudeProjectMatcher.Match(Path.Combine(temp.Path, "local"), [Session(Path.Combine(temp.Path, "local", "sub")), Session(Path.Combine(temp.Path, "local-other"))]));
    }

    [Fact]
    public async Task MatchingRecognizesAnActualGitWorktreeOutsideTheProjectFolder()
    {
        using var temp = new TempDirectory();
        async Task Git(params string[] args)
        {
            var result = await ProcessRunner.RunAsync(new("git", args, temp.Path));
            Assert.True(result.Success, result.Output);
        }
        await Git("init", "--initial-branch=main", "repo");
        await Git("-C", "repo", "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=" + Path.Combine(temp.Path, "no-hooks"), "commit", "--allow-empty", "-m", "Initial");
        await Git("-C", "repo", "worktree", "add", "../linked", "-b", "feature");
        var session = Session(Path.Combine(temp.Path, "linked"));
        Assert.Equal(session, Assert.Single(ClaudeProjectMatcher.Match(Path.Combine(temp.Path, "repo"), [session])));
    }
}
