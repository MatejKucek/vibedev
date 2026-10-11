using System.Text.RegularExpressions;

namespace Vibedev.Core;

public sealed record BuildLocation(string FilePath, int Line, int Column)
{
    // MSBuild/compiler locations and the path:line:column form used by other tools.
    private static readonly Regex Pattern = new(@"(?<path>(?:[A-Za-z]:[\\/]|\\\\|\.{0,2}[\\/])?[^\r\n<>|\""?*]+?\.[A-Za-z0-9]+)(?:\((?<line>\d+)(?:,(?<column>\d+))?(?:,\d+,\d+)?\)|:(?<line>\d+)(?::(?<column>\d+))?)", RegexOptions.Compiled);
    private static readonly Regex Ansi = new(@"\x1b\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);

    public static BuildLocation? Parse(string text, string directory)
    {
        text = Ansi.Replace(text, "");
        // The trailing project path disambiguates relative compiler paths in solution builds.
        var project = Regex.Match(text, @"\[(?<project>[^\[\]]+\.\w*proj)\]\s*$");
        if (project.Success)
        {
            try { directory = Path.GetDirectoryName(Path.GetFullPath(project.Groups["project"].Value, directory))!; }
            catch (ArgumentException) { }
        }
        foreach (Match match in Pattern.Matches(text))
        {
            var path = match.Groups["path"].Value.Trim();
            // Output can carry timestamps or a project prefix before an absolute path.
            var absolute = Regex.Match(path, @"[A-Za-z]:[\\/]|\\\\");
            if (absolute.Success) path = path[absolute.Index..];
            else path = Regex.Replace(path, @"^(?:\d{2}:\d{2}:\d{2}\s+)?(?:\[[^\]]+\]\s*)?(?:\d+>)?", "");
            if (!int.TryParse(match.Groups["line"].Value, out var line) || line < 1) continue;
            var column = int.TryParse(match.Groups["column"].Value, out var col) ? Math.Max(1, col) : 1;
            try
            {
                var fullPath = Path.GetFullPath(path.Trim(), directory);
                if (File.Exists(fullPath)) return new(fullPath, line, column);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        }
        return null;
    }
}
