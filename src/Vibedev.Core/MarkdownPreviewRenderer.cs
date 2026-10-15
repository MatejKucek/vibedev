using Markdig;
using Markdig.Renderers;

namespace Vibedev.Core;

public static class MarkdownPreviewRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseTaskLists().UseAutoLinks().UseEmphasisExtras().UseAutoIdentifiers().DisableHtml().Build();

    public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown";

    public static string Render(string source)
    {
        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer) { LinkRewriter = SafeLink };
        Pipeline.Setup(renderer);
        renderer.Render(Markdown.Parse(source, Pipeline));
        return writer.ToString();
    }

    private static string SafeLink(string url)
    {
        if (url.Any(char.IsControl)) return "";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is not ("http" or "https" or "mailto")) return "";
        return url;
    }
}
