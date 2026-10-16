namespace Vibedev.Core;

/// <summary>Model and effort declared in a task file's frontmatter (`---` block with `model:` and `effort:` keys).</summary>
public sealed record TaskMetadata(string Model, string Effort)
{
    public static readonly TaskMetadata Empty = new("", "");

    /// <summary>Reads the frontmatter, or, without one, a recommendation sentence such as "model `opus`, effort `xhigh`" near the top.</summary>
    public static TaskMetadata Parse(string text)
    {
        using var reader = new StringReader(text);
        string model = "", effort = "";
        var first = reader.ReadLine();
        if (first?.Trim() == "---")
        {
            while (reader.ReadLine() is { } line)
            {
                if (line.Trim() == "---") break;
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var key = line[..colon].Trim(); var value = line[(colon + 1)..].Trim().Trim('"', '\'');
                if (key.Equals("model", StringComparison.OrdinalIgnoreCase)) model = value;
                else if (key.Equals("effort", StringComparison.OrdinalIgnoreCase)) effort = value;
            }
            if (model.Length > 0 || effort.Length > 0) return new(model, effort);
            text = reader.ReadToEnd();
        }
        var head = text.Length > 800 ? text[..800] : text;
        return new(Find(head, "model"), Find(head, "effort"));
    }

    private static string Find(string text, string key)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"\b" + key + @"\b\s*[:=]?\s*(?:`([^`\r\n]+)`|""([^""\r\n]+)""|(?<=:\s*)([\w.\-\[\]]+))", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? (match.Groups[1].Success ? match.Groups[1] : match.Groups[2].Success ? match.Groups[2] : match.Groups[3]).Value.Trim() : "";
    }

    public static TaskMetadata Read(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            var buffer = new char[4096]; var length = reader.ReadBlock(buffer, 0, buffer.Length);
            return Parse(new string(buffer, 0, length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Empty; }
    }
}
