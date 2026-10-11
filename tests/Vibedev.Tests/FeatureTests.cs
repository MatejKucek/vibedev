using System.Text;
using System.Text.Json;
using Vibedev.Core;

namespace Vibedev.Tests;

public sealed class BuildLocationTests
{
    [Theory]
    [InlineData("{0}(12,4): error CS1002: ; expected", 12, 4)]
    [InlineData("{0}(12,4,12,9): warning CS1234", 12, 4)]
    [InlineData("{0}:12:4: error", 12, 4)]
    [InlineData("{0}(12): error", 12, 1)]
    [InlineData("23:59:01  [build] {0}(12,4): error", 12, 4)]
    [InlineData("\u001b[31m{0}(12,4): error\u001b[0m", 12, 4)]
    public void ParsesExistingFileLocations(string format, int line, int column)
    {
        using var temp = new TempDirectory(); var path = System.IO.Path.GetFullPath(temp.File("folder with spaces/žluťoučký.cs", "code"));
        var result = BuildLocation.Parse(string.Format(format, path), temp.Path);
        Assert.Equal(new(path, line, column), result);
        var relative = BuildLocation.Parse(string.Format(format, System.IO.Path.GetRelativePath(temp.Path, path)), temp.Path);
        Assert.Equal(new(path, line, column), relative);
    }
    [Fact]
    public void RelativeSolutionErrorsResolveAgainstTrailingProject()
    {
        using var temp = new TempDirectory(); var path = System.IO.Path.GetFullPath(temp.File("child/Program.cs", "code")); var project = temp.File("child/app.csproj", "project");
        Assert.Equal(new(path, 3, 2), BuildLocation.Parse($"Program.cs(3,2): error CS1002 [{project}]", temp.Path));
        Assert.Null(BuildLocation.Parse("missing.cs(1,1): error", temp.Path));
        Assert.Null(BuildLocation.Parse($"{path}(0,2): error", temp.Path));
        Assert.Null(BuildLocation.Parse("ordinary output", temp.Path));
    }
}

public sealed class LanguageServerFramingTests
{
    private static byte[] Frame(string text) => Encoding.ASCII.GetBytes($"Content-Length: {Encoding.UTF8.GetByteCount(text)}\r\nContent-Type: application/vscode-jsonrpc; charset=utf-8\r\n\r\n").Concat(Encoding.UTF8.GetBytes(text)).ToArray();
    [Fact]
    public async Task ReadsUtf8AndMultipleMessagesWithoutConsumingNextFrame()
    {
        using var input = new MemoryStream(Frame("{\"text\":\"žluťoučký 🐈\"}").Concat(Frame("{\"id\":2}")).ToArray());
        Assert.Equal("žluťoučký 🐈", (await LanguageServerClient.ReadMessageAsync(input))!.Value.GetProperty("text").GetString());
        Assert.Equal(2, (await LanguageServerClient.ReadMessageAsync(input))!.Value.GetProperty("id").GetInt32());
        Assert.Null(await LanguageServerClient.ReadMessageAsync(input));
    }
    [Fact]
    public async Task RejectsInvalidOrTruncatedMessages()
    {
        using var invalid = new MemoryStream(Encoding.ASCII.GetBytes("Content-Length: -1\r\n\r\n"));
        await Assert.ThrowsAsync<IOException>(() => LanguageServerClient.ReadMessageAsync(invalid));
        using var truncated = new MemoryStream(Encoding.ASCII.GetBytes("Content-Length: 30\r\n\r\n{}"));
        await Assert.ThrowsAsync<EndOfStreamException>(() => LanguageServerClient.ReadMessageAsync(truncated));
    }
}

public sealed class GitSelectionTests
{
    private static async Task Git(string path, params string[] args)
    {
        var result = await ProcessRunner.RunAsync(new("git", args, path)); Assert.True(result.Success, result.Output);
    }
    private static async Task Init(string path)
    {
        await Git(path, "init", "--initial-branch=main"); await Git(path, "config", "user.name", "Test"); await Git(path, "config", "user.email", "test@example.invalid");
        await Git(path, "config", "commit.gpgsign", "false"); await Git(path, "config", "core.hooksPath", System.IO.Path.Combine(path, "no-hooks"));
    }
    [Fact]
    public async Task SelectedStagingUnstagingAndCommitPreserveOtherFiles()
    {
        using var temp = new TempDirectory(); await Init(temp.Path); var service = new GitService();
        var first = temp.File("one file ž.cs", "before\n"); var second = temp.File("other.cs", "untouched\n");
        await service.CommitAllAsync(temp.Path, "initial");
        await System.IO.File.WriteAllTextAsync(first, "after\n"); await System.IO.File.WriteAllTextAsync(second, "other change\n");
        var changes = await service.ChangesAsync(temp.Path); var file = changes.Single(f => f.Path == "one file ž.cs");
        Assert.Contains("-before", await service.DiffAsync(temp.Path, file, false));
        Assert.Contains("+after", await service.DiffAsync(temp.Path, file, false));
        Assert.True((await service.StageAsync(temp.Path, [file])).Success);
        changes = await service.ChangesAsync(temp.Path); Assert.True(changes.Single(f => f.Path == file.Path).Staged); Assert.False(changes.Single(f => f.Path == "other.cs").Staged);
        Assert.Contains("+after", await service.DiffAsync(temp.Path, file, true));
        Assert.True((await service.UnstageAsync(temp.Path, [file])).Success); Assert.All(await service.ChangesAsync(temp.Path), f => Assert.False(f.Staged));
        Assert.Equal("after\n", await System.IO.File.ReadAllTextAsync(first));
        await service.StageAsync(temp.Path, [file]); Assert.True((await service.CommitStagedAsync(temp.Path, "selected")).Success);
        Assert.Equal("other.cs", Assert.Single(await service.ChangesAsync(temp.Path)).Path);
        await Assert.ThrowsAsync<ArgumentException>(() => service.StageAsync(temp.Path, []));
    }
    [Fact]
    public async Task HandlesNewFilesBeforeFirstCommitAndRenames()
    {
        using var temp = new TempDirectory(); await Init(temp.Path); var service = new GitService();
        temp.File("new.cs", "new source");
        var file = Assert.Single(await service.ChangesAsync(temp.Path)); Assert.True(file.Untracked); Assert.Contains("+new source", await service.DiffAsync(temp.Path, file, false));
        Assert.True((await service.StageAsync(temp.Path, [file])).Success);
        await System.IO.File.AppendAllTextAsync(System.IO.Path.Combine(temp.Path, file.Path), " edited after staging");
        Assert.True((await service.UnstageAsync(temp.Path, [file])).Success); Assert.True(System.IO.File.Exists(System.IO.Path.Combine(temp.Path, "new.cs")));
        Assert.Contains("edited after staging", await System.IO.File.ReadAllTextAsync(System.IO.Path.Combine(temp.Path, file.Path)));
        await service.CommitAllAsync(temp.Path, "initial"); await Git(temp.Path, "mv", "new.cs", "renamed space.cs");
        var renamed = Assert.Single(await service.ChangesAsync(temp.Path)); Assert.Equal("new.cs", renamed.OriginalPath); Assert.Equal("renamed space.cs", renamed.Path);
        await System.IO.File.AppendAllTextAsync(System.IO.Path.Combine(temp.Path, renamed.Path), "more");
        renamed = Assert.Single(await service.ChangesAsync(temp.Path)); Assert.True((await service.StageAsync(temp.Path, [renamed])).Success);
        Assert.True((await service.UnstageAsync(temp.Path, [renamed])).Success); Assert.All(await service.ChangesAsync(temp.Path), f => Assert.False(f.Staged));
    }
}
