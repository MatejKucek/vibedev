using Vibedev.Core;

namespace Vibedev.Tests;

public sealed class ProjectSearchTests
{
    [Fact]
    public async Task SearchStaysInsideTheSelectedProjectAndReturnsLocations()
    {
        using var temp = new TempDirectory();
        var path = Path.GetFullPath(temp.File("current/src/Service.cs", "first line\r\nHello project hello\r\n"));
        temp.File("other/Outside.cs", "hello");
        var result = await ProjectSearch.FindAsync(Path.Combine(temp.Path, "current"), "hello", ProjectSearchMode.Contents);
        Assert.Equal(2, result.Hits.Count);
        Assert.All(result.Hits, h => { Assert.Equal(path, h.FilePath); Assert.Equal(2, h.Line); });
        Assert.Equal(1, result.Hits[0].Column); Assert.Equal(15, result.Hits[1].Column);
    }
    [Fact]
    public async Task FilenameAndContentModesAreSeparate()
    {
        using var temp = new TempDirectory(); temp.File("Config.cs", "A setting"); temp.File("Other.cs", "config value");
        var names = await ProjectSearch.FindAsync(temp.Path, "config", ProjectSearchMode.FileNames);
        Assert.Equal("Config.cs", Assert.Single(names.Hits).RelativePath); Assert.Equal(0, names.Hits[0].Line);
        var contents = await ProjectSearch.FindAsync(temp.Path, "config", ProjectSearchMode.Contents);
        Assert.Equal("Other.cs", Assert.Single(contents.Hits).RelativePath);
        Assert.Equal(2, (await ProjectSearch.FindAsync(temp.Path, "config")).Hits.Count);
    }
    [Fact]
    public async Task SearchReadsUnsavedEditorBuffers()
    {
        using var temp = new TempDirectory(); var file = temp.File("Source.cs", "on disk");
        var result = await ProjectSearch.FindAsync(temp.Path, "unsaved", buffers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [file] = "unsaved buffer" });
        Assert.Equal(file, Assert.Single(result.Hits).FilePath);
        Assert.Empty((await ProjectSearch.FindAsync(temp.Path, "on disk", buffers: new Dictionary<string, string> { [file] = "unsaved buffer" })).Hits);
    }
    [Fact]
    public async Task BinaryAndBuildFilesAreSkippedAndGitignoreIsRespected()
    {
        using var temp = new TempDirectory();
        await ProcessRunner.RunAsync(new("git", ["init"], temp.Path));
        temp.File(".gitignore", "ignored.txt\n"); temp.File("ignored.txt", "needle"); temp.File("bin/output.cs", "needle"); temp.File("binary.dat", "needle\0"); temp.File("source.cs", "needle");
        Assert.Equal("source.cs", Assert.Single((await ProjectSearch.FindAsync(temp.Path, "needle", ProjectSearchMode.Contents)).Hits).RelativePath);
    }
    [Fact]
    public async Task ResultLimitAndCancellationAreExplicit()
    {
        using var temp = new TempDirectory(); temp.File("Many.cs", string.Join('\n', Enumerable.Repeat("needle", 510)));
        var result = await ProjectSearch.FindAsync(temp.Path, "needle"); Assert.True(result.Truncated); Assert.Equal(ProjectSearch.ResultLimit, result.Hits.Count);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectSearch.FindAsync(temp.Path, "needle", cancellation: cancellation.Token));
    }
}

public sealed class FuzzyMatchTests
{
    [Fact]
    public void CamelHumpQueryFindsFilePlanner()
    {
        var ranked = FuzzyMatch.Rank(["src/Other.cs", "src/FilePlanner.cs", "src/Profile.cs"], "fipl", 10);
        Assert.Equal("src/FilePlanner.cs", ranked[0]);
        Assert.DoesNotContain("src/Other.cs", ranked);
    }
    [Fact]
    public async Task ListFilesSkipsBuildFolders()
    {
        using var temp = new TempDirectory(); temp.File("src/A.cs", "x"); temp.File("bin/B.cs", "x");
        Assert.Equal(["src" + Path.DirectorySeparatorChar + "A.cs"], await ProjectSearch.ListFilesAsync(temp.Path));
    }
}
