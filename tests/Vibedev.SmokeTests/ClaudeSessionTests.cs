using System.IO;
using System.Text.Json;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using Vibedev;
using Vibedev.Core;
using Vibedev.Views;

internal static partial class Program
{
    private static async Task ExecuteClaudeSessionsAsync(MainWindow window, string root)
    {
        var projectPath = Path.Combine(root, "claude-project"); Directory.CreateDirectory(projectPath);
        var sessionsPath = Path.Combine(root, "claude-sessions"); Directory.CreateDirectory(sessionsPath);
        window.AddPath(projectPath); window.OpenProject(window.Projects[0]);
        var project = window.Projects[0];
        Ellipse Indicator() { window.UpdateLayout(); return Descendants<Ellipse>(window.Dashboard).Single(); }
        async Task WriteSession(string status, string? waitingFor = null)
        {
            await File.WriteAllTextAsync(Path.Combine(sessionsPath, "10.json"), JsonSerializer.Serialize(new
            { pid = 10, sessionId = "smoke-session", cwd = Path.Combine(projectPath, "src"), name = "Dashboard test session", status, waitingFor }));
        }
        await WaitUntilAsync(() => Indicator().Tag is ClaudeActivityLight.Gray, "A project starts with a gray Claude indicator");
        var tasks = Path.Combine(projectPath, "tasks", "open", "nested"); Directory.CreateDirectory(tasks);
        await File.WriteAllTextAsync(Path.Combine(tasks, "task.md"), "# Open task\n");
        await WaitUntilAsync(() => project.OpenTaskCount == 1 && Indicator().Tag is ClaudeActivityLight.Red,
            "The task watcher turns the dashboard red for an open Markdown task");
        await WriteSession("busy");
        await WaitUntilAsync(() => Indicator().Tag is ClaudeActivityLight.Blue,
            "Polling matches a Claude session in a subfolder and turns the dashboard blue");
        Check(((SolidColorBrush)Indicator().Fill).Color == Color.FromRgb(0x60, 0xA5, 0xFA) &&
              Indicator().ToolTip.ToString()!.Contains("Dashboard test session"), "The blue indicator includes its matching session in the tooltip");
        await File.WriteAllTextAsync(Path.Combine(sessionsPath, "10.json"), "{unfinished");
        await WaitUntilAsync(() => Indicator().ToolTip.ToString()!.Contains("incomplete"),
            "A partial session write is exposed in the tooltip");
        Check(Indicator().Tag is ClaudeActivityLight.Blue, "A partial write preserves blue instead of falsely reporting completion");
        await WriteSession("waiting", "input needed");
        await WaitUntilAsync(() => Indicator().Tag is ClaudeActivityLight.Orange,
            "A waiting session turns orange");
        Check(Indicator().ToolTip.ToString()!.Contains("input needed"), "Orange explains why Claude is waiting");
        await WriteSession("idle");
        await WaitUntilAsync(() => Indicator().Tag is ClaudeActivityLight.Green,
            "A session returning to idle turns green despite remaining open tasks");
        File.Delete(Path.Combine(sessionsPath, "10.json"));
        await WaitUntilAsync(() => project.Claude.Sessions.Count == 0, "Polling removes deleted session records");
        Check(Indicator().Tag is ClaudeActivityLight.Green, "Green remains after the session exits");
        await WriteSession("busy");
        await WaitUntilAsync(() => Indicator().Tag is ClaudeActivityLight.Blue, "A later run turns green back to blue");
        await CaptureAsync(window, "dashboard-claude-blue.png");
        await WriteSession("waiting", "permission prompt");
        await WaitUntilAsync(() => Indicator().Tag is ClaudeActivityLight.Orange, "Permission prompts also show orange");
        await CaptureAsync(window, "dashboard-claude-orange.png");

        // Exercise the default production reader against this machine without modifying any Claude files.
        var live = await Task.Run(() => new ClaudeSessionReader().Read());
        Results.Add($"INFO: Production reader found {live.Sessions.Count} verified live Claude sessions ({live.Sessions.Count(s => s.Status == "busy")} busy)." +
            (live.Warning is null ? "" : " " + live.Warning));
        File.WriteAllLines(Path.Combine(OutputDirectory, "claude-session-results.txt"), Results);
        window.Close();
    }
}
