using System.IO;
using System.Windows;
using System.Windows.Controls;
using Vibedev;
using Vibedev.Core;
using Vibedev.Views;

internal static partial class Program
{
    private static async Task ExecuteSidebarPullAsync(MainWindow window, string root)
    {
        async Task Git(string path, params string[] args)
        {
            var result = await ProcessRunner.RunAsync(new("git", args, path));
            Check(result.Success, "Sidebar pull fixture: git " + string.Join(" ", args));
        }
        async Task<string> Clone(string remote, string name)
        {
            var repo = System.IO.Path.Combine(root, name);
            await Git(root, "clone", remote, repo);
            await Git(repo, "config", "user.name", "Pull test");
            await Git(repo, "config", "user.email", "pull@example.invalid");
            await Git(repo, "config", "commit.gpgsign", "false");
            await Git(repo, "config", "pull.rebase", "false");
            await Git(repo, "config", "core.hooksPath", System.IO.Path.Combine(root, "no-hooks"));
            return repo;
        }
        var remote = System.IO.Path.Combine(root, "remote.git");
        await Git(root, "init", "--bare", "--initial-branch=main", remote);
        var good = await Clone(remote, "good-project");
        await File.WriteAllTextAsync(System.IO.Path.Combine(good, "notes.txt"), "Initial\n");
        await Git(good, "add", "--all"); await Git(good, "commit", "-m", "Initial"); await Git(good, "push", "-u", "origin", "main");
        var broken = await Clone(remote, "broken-project");
        await Git(broken, "remote", "set-url", "origin", System.IO.Path.Combine(root, "missing.git"));
        Directory.CreateDirectory(System.IO.Path.Combine(good, "tasks", "open", "nested"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(good, "tasks", "open", "one.md"), "# One\n");
        await File.WriteAllTextAsync(System.IO.Path.Combine(good, "tasks", "open", "nested", "two.md"), "# Two\n");
        await File.WriteAllTextAsync(System.IO.Path.Combine(good, "tasks", "open", "ignored.txt"), "not a task\n");

        window.AddPath(good); window.AddPath(broken);
        await window.RefreshStatusesAsync(); window.UpdateLayout();
        var goodProject = window.Projects.Single(p => p.Entry.Path == good);
        var brokenProject = window.Projects.Single(p => p.Entry.Path == broken);
        Check(window.ProjectsColumn.ActualWidth >= 260, "Projects sidebar is wide enough for the status columns");
        Check(goodProject.OpenTaskCount == 2 && brokenProject.OpenTaskCount == 0, "Task counts are read for projects that are not open");

        Grid Row(ProjectState project) => (Grid)((Border)window.ProjectList.Items.OfType<ListBoxItem>().Single(i => i.Tag == project).Content).Child;
        TextBlock Cell(ProjectState project, int column) => Row(project).Children.OfType<TextBlock>().Single(t => Grid.GetColumn(t) == column);
        Check(Cell(goodProject, 2).Text == "2" && Cell(brokenProject, 2).Text == "0", "Sidebar task column shows remaining task counts");

        Check(window.PullAllButton.IsEnabled, "Sidebar pull button is enabled with listed projects");
        window.PullAllButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(window.IsPulling && !window.PullAllButton.IsEnabled, "Sidebar pull disables its button while running");
        Check(Row(goodProject).Children.OfType<System.Windows.Shapes.Path>().Any(p => Grid.GetColumn(p) == 1), "Pulling projects show a spinner in the pull column");
        await WaitUntilAsync(() => !window.IsPulling, "Sidebar pull finishes", 30000);
        window.UpdateLayout();
        Check(goodProject.Pull == PullState.Succeeded && Cell(goodProject, 1).Text == "✓", "Successful pull shows a green check");
        Check(brokenProject.Pull == PullState.Failed && Cell(brokenProject, 1).Text == "✗", "Failed pull shows a red cross");
        Check(window.PullAllButton.IsEnabled, "Sidebar pull button is enabled again after the run");

        window.ProjectFilter.Text = "good"; window.UpdateLayout();
        window.PullAllButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => !window.IsPulling, "Filtered sidebar pull finishes", 30000);
        Check(goodProject.Pull == PullState.Succeeded && brokenProject.Pull == PullState.None, "Sidebar pull only pulls listed projects");
        window.ProjectFilter.Text = "";

        await CaptureAsync(window, "sidebar-pull.png");
        File.WriteAllLines(System.IO.Path.Combine(OutputDirectory, "sidebar-pull-results.txt"), Results);
    }
}
