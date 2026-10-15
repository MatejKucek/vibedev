using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Vibedev;
using Vibedev.Services;
using Vibedev.Views;

internal static partial class Program
{
    private static async Task ExecutePolishAsync(MainWindow window, string root)
    {
        var projectPath = Path.Combine(root, "workspace-api");
        Directory.CreateDirectory(projectPath);
        var filePath = Path.Combine(projectPath, "notes.txt");
        await File.WriteAllTextAsync(filePath, "original");
        window.AddPath(projectPath);
        window.OpenProject(window.Projects[0]);
        var document = window.Documents().Single();
        var view = (ProjectView)document.Content;
        await view.OpenFileAsync(filePath);
        var editor = view.Editors.Single();
        editor.Editor.Text = "local edit";
        await File.WriteAllTextAsync(filePath, "external edit");
        await WaitUntilAsync(() => Descendants<TextBlock>((Grid)editor.Content).Any(t => t.Text.Contains("Autosave paused")),
            "A disk conflict pauses autosave and preserves local edits");
        await File.WriteAllTextAsync(filePath, "original");
        editor.Editor.AppendText(" after conflict");
        await Task.Delay(1200);
        Check(editor.IsDirty && await File.ReadAllTextAsync(filePath) == "original",
            "Typing after a save failure keeps autosave paused until explicit resolution");
        Check(await editor.SaveAsync() && !editor.IsDirty && await File.ReadAllTextAsync(filePath) == "local edit after conflict",
            "Explicit save resolves the paused state without losing edits");
        editor.Editor.AppendText(" resumed");
        await WaitUntilAsync(() => !editor.IsDirty, "Autosave resumes after an explicit successful save");

        window.SplitWithProject(document, Orientation.Horizontal, window.Projects[0]);
        window.Width = 1100;
        window.SetTerminalVisible(false);
        await Task.Delay(250); window.UpdateLayout();
        foreach (var panel in window.Documents().Select(d => (ProjectView)d.Content))
        {
            var close = panel.CloseProjectButton;
            var position = close.TranslatePoint(new Point(), panel);
            Check(position.X >= 0 && position.X + close.ActualWidth <= panel.ActualWidth && position.Y >= 0,
                "Close project stays visible in a narrow split without horizontal scrolling");
            var run = Descendants<Button>(panel).Single(b => b.Content?.ToString() == "▶ Run");
            position = run.TranslatePoint(new Point(), panel);
            Check(position.X >= 0 && position.X + run.ActualWidth <= panel.ActualWidth,
                "Run stays visible in a narrow split");
        }
        window.BottomTabs.SelectedIndex = 1;
        await CaptureAsync(window, "polished-splits.png");

        var more = Descendants<Button>(view).Single(b => b.Content?.ToString() == "⋯");
        more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(more.ContextMenu.IsOpen && more.ContextMenu.Items.OfType<MenuItem>().Select(i => (string)i.Header)
                .SequenceEqual(new[] { "Split →", "Split ↓", "↗ Float", "C# server" }),
            "Panel actions menu exposes splitting, floating and C# setup");
        more.ContextMenu.IsOpen = false;

        Directory.CreateDirectory(Path.Combine(projectPath, ".vscode"));
        await File.WriteAllTextAsync(Path.Combine(projectPath, ".vscode", "launch.json"), JsonSerializer.Serialize(new
        {
            configurations = new[] { "Development", "Another launch configuration" }.Select(name => new
            {
                name, type = "coreclr", request = "launch",
                program = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                args = new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 30" }
            })
        }));
        view.LoadLaunches();
        var launches = Descendants<ComboBox>(view).Single(c => c.DisplayMemberPath == "Name");
        launches.IsDropDownOpen = true; window.UpdateLayout();
        Check(launches.IsEnabled && launches.IsDropDownOpen && launches.Items.Count == 2,
            "Launch dropdown opens and displays available configurations");
        launches.SelectedIndex = 1; launches.IsDropDownOpen = false;
        Check(view.SelectedLaunch?.Name == "Another launch configuration", "Launch selection updates the configuration used by Run");

        var other = window.Documents().Select(d => (ProjectView)d.Content).Single(p => p != view);
        var backgroundPath = Path.Combine(projectPath, "background.txt");
        await File.WriteAllTextAsync(backgroundPath, "old");
        await other.OpenFileAsync(backgroundPath);
        var backgroundEditor = other.Editors.Single();
        var terminal = window.TerminalHost.Children.OfType<TerminalControl>().Single();
        window.SetTerminalVisible(true);
        await WaitUntilAsync(() => terminal.IsReady, "Terminal is ready for the cross-panel Run regression");
        backgroundEditor.Editor.Text = "pending edit in another panel";
        window.RunProject(view);
        await WaitUntilAsync(() => terminal.IsRunning, "Run starts from the selected panel");
        var savedBeforeRun = !backgroundEditor.IsDirty && await File.ReadAllTextAsync(backgroundPath) == "pending edit in another panel";
        terminal.Stop();
        await WaitUntilAsync(() => window.RunningProject is null, "The regression run stops cleanly");
        await backgroundEditor.SaveAsync();
        Check(savedBeforeRun, "Run saves edits from every panel of the same project before launching");
        File.WriteAllLines(Path.Combine(OutputDirectory, "polish-results.txt"), Results);
    }
}
