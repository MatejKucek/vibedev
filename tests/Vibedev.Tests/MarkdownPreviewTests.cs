using Vibedev.Core;

namespace Vibedev.Tests;

public sealed class MarkdownPreviewTests
{
    [Fact]
    public void RendersDocumentStructureAndGithubStyleContent()
    {
        var html = MarkdownPreviewRenderer.Render("""
            # Project notes

            **Strong** and *emphasis* with `inline code` and ~~removed~~.

            > A useful note.

            - [x] Finished
            - [ ] Next

            | Feature | State |
            | --- | --- |
            | Preview | Ready |

            ```csharp
            var value = "<tag>";
            ```

            [Guide](docs/guide%20notes.md) and [heading](#project-notes).
            """);
        Assert.Contains("<h1 id=\"project-notes\">", html);
        Assert.Contains("<strong>Strong</strong>", html);
        Assert.Contains("<em>emphasis</em>", html);
        Assert.Contains("<del>removed</del>", html);
        Assert.Contains("<blockquote>", html);
        Assert.Contains("type=\"checkbox\"", html);
        Assert.Contains("checked=\"checked\"", html);
        Assert.Contains("<table>", html);
        Assert.Contains("language-csharp", html);
        Assert.Contains("&lt;tag&gt;", html);
        Assert.Contains("href=\"docs/guide%20notes.md\"", html);
        Assert.Contains("href=\"#project-notes\"", html);
    }

    [Fact]
    public void RawHtmlAndExecutableLinksCannotBecomeActiveContent()
    {
        var html = MarkdownPreviewRenderer.Render("""
            <script>alert('bad')</script>
            <img src="x" onerror="alert('bad')">

            [Script](javascript:alert%281%29)
            [File](file:///C:/Windows/notepad.exe)
            [Command](vscode://some-command)
            [Web](https://example.com/docs)
            """);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<img ", html);
        Assert.DoesNotContain("href=\"javascript:", html);
        Assert.DoesNotContain("href=\"file:", html);
        Assert.DoesNotContain("href=\"vscode:", html);
        Assert.Contains("href=\"https://example.com/docs\"", html);
    }

    [Theory]
    [InlineData("readme.md", true)]
    [InlineData("TASK.MD", true)]
    [InlineData("notes.Markdown", true)]
    [InlineData("notes.md.bak", false)]
    [InlineData("Program.cs", false)]
    public void RecognizesMarkdownExtensions(string path, bool expected)
        => Assert.Equal(expected, MarkdownPreviewRenderer.Supports(path));
}
