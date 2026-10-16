using Vibedev.Core;

namespace Vibedev.Tests;

public sealed class TaskMetadataTests
{
    [Fact]
    public void ParsesModelAndEffortFromFrontmatter()
    {
        var meta = TaskMetadata.Parse("---\nmodel: opus\nEffort: \"high\"\n---\n# Task\nmodel: ignored\n");
        Assert.Equal("opus", meta.Model);
        Assert.Equal("high", meta.Effort);
    }

    [Fact]
    public void ParsesRecommendationSentence()
    {
        var meta = TaskMetadata.Parse("Recommended: model `opus`, effort `xhigh` — offense staging; earlier runs showed marches that never arrive.\n");
        Assert.Equal(new TaskMetadata("opus", "xhigh"), meta);
    }

    [Fact]
    public void ProseMentionsWithoutValueAreIgnored() =>
        Assert.Equal(TaskMetadata.Empty, TaskMetadata.Parse("Rework the model loader so effort is spread out."));

    [Theory]
    [InlineData("")]
    [InlineData("---\ntitle: x\n---\n")]
    public void MissingFrontmatterValuesAreEmpty(string text) => Assert.Equal(TaskMetadata.Empty, TaskMetadata.Parse(text));

    [Fact]
    public void ReadsFromFileAndToleratesMissingFile()
    {
        using var temp = new TempDirectory();
        var path = temp.File("t.md", "---\nmodel: sonnet\neffort: low\n---\n");
        Assert.Equal(new TaskMetadata("sonnet", "low"), TaskMetadata.Read(path));
        Assert.Equal(TaskMetadata.Empty, TaskMetadata.Read(System.IO.Path.Combine(temp.Path, "missing.md")));
    }
}
