namespace Vibedev.Core;

/// <summary>Subsequence file matcher: "fipl" finds FilePlanner.cs via word-start (camel hump) hits.</summary>
public static class FuzzyMatch
{
    /// <summary>Returns a score (higher is better) or null when <paramref name="query"/> is not a subsequence of the path.</summary>
    public static int? Score(string relativePath, string query)
    {
        if (query.Length == 0) return 0;
        var name = Path.GetFileName(relativePath);
        var nameScore = Subsequence(name, query);
        if (nameScore is { } n) return 1000 + n - name.Length;
        var pathScore = Subsequence(relativePath, query);
        return pathScore is { } p ? p - relativePath.Length : null;
    }

    public static IReadOnlyList<string> Rank(IEnumerable<string> paths, string query, int limit)
    {
        var scored = new List<(string Path, int Score)>();
        foreach (var path in paths)
            if (Score(path, query.Trim()) is { } score) scored.Add((path, score));
        return scored.OrderByDescending(s => s.Score).ThenBy(s => s.Path.Length).ThenBy(s => s.Path, StringComparer.OrdinalIgnoreCase)
            .Take(limit).Select(s => s.Path).ToArray();
    }

    private static int? Subsequence(string text, string query)
    {
        var score = 0; var position = 0; var previous = -2;
        foreach (var c in query)
        {
            var index = text.IndexOf(c.ToString(), position, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return null;
            if (index == previous + 1) score += 5;
            if (index == 0 || !char.IsLetterOrDigit(text[index - 1]) || (char.IsUpper(text[index]) && char.IsLower(text[index - 1]))) score += 8;
            if (text[index] == c) score += 1;
            previous = index; position = index + 1;
        }
        return score;
    }
}
