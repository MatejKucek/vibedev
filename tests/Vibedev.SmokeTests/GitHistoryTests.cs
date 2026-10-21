using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vibedev;
using Vibedev.Core;
using Vibedev.Views;

internal static partial class Program
{
    private static async Task ExecuteGitHistoryAsync(MainWindow window, string root)
    {
        async Task Git(params string[] args)
        {
            var result = await ProcessRunner.RunAsync(new("git", args, root));
            Check(result.Success, "History fixture: git " + string.Join(" ", args));
        }
        await Git("init", "--initial-branch=main");
        await Git("config", "user.name", "History test"); await Git("config", "user.email", "history@example.invalid");
        await Git("config", "commit.gpgsign", "false"); await Git("config", "core.hooksPath", Path.Combine(root, "no-hooks"));
        await File.WriteAllTextAsync(Path.Combine(root, "notes.txt"), "Original text\n");
        await Git("add", "notes.txt"); await Git("commit", "-m", "Initial commit");
        await File.WriteAllTextAsync(Path.Combine(root, "notes.txt"), "Updated text\n");
        await File.WriteAllTextAsync(Path.Combine(root, "new ž file.txt"), "Added file\n");
        await Git("add", "notes.txt", "new ž file.txt"); await Git("commit", "-m", "Update notes and add a file");
        window.AddPath(root); window.OpenProject(window.Projects.Single());
        var view = (ProjectView)window.Documents().Single().Content; view.ShowGit(); await view.GitView.RefreshAsync(); window.UpdateLayout();
        var message = Descendants<TextBox>(view.GitView).Single(); message.Text = "Keep my draft";
        var sync = Descendants<Button>(view.GitView).Single(button => button.Content?.ToString() == "Commit all, pull & push");
        var tree = Descendants<TreeView>(view.GitView).Single();
        await WaitUntilAsync(() => tree.Items.Count == 2, "Git tab loads recent commits");
        Check(tree.IsVisible && message.IsVisible && sync.IsVisible && view.GitView.ChangesList.IsVisible, "Recent commits show below the working changes");
        Check(tree.TranslatePoint(new Point(), view.GitView).Y > message.TranslatePoint(new Point(), view.GitView).Y, "Recent commits sit at the bottom of the Git tab");
        var latest = (TreeViewItem)tree.Items[0]; var initial = (TreeViewItem)tree.Items[1];
        Check(((GitCommit)latest.Tag).Subject == "Update notes and add a file" && ((GitCommit)initial.Tag).Subject == "Initial commit", "Commits appear newest first");
        ((TextBlock)latest.Header).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        await WaitUntilAsync(() => latest.Items.OfType<TreeViewItem>().Count() == 2, "Clicking a commit expands its changed files");
        Check(latest.IsExpanded && latest.Items.OfType<TreeViewItem>().Any(item => item.Tag is GitCommitFile { Path: "new ž file.txt", Status: 'A' }) && latest.Items.OfType<TreeViewItem>().Any(item => item.Tag is GitCommitFile { Path: "notes.txt", Status: 'M' }), "Expanded commit shows file paths and change statuses");
        void DoubleClick(TreeViewItem file) => file.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
        var changedFile = latest.Items.OfType<TreeViewItem>().Single(item => item.Tag is GitCommitFile { Path: "notes.txt" });
        DoubleClick(changedFile);
        await WaitUntilAsync(() => Descendants<GitDiffTab>(view).Any(tab => tab.IsSelected && tab.Editor.Text.Contains("-Original text") && tab.Editor.Text.Contains("+Updated text")), "Double-clicking a changed file opens its commit comparison in the main file panel");
        var committedDiff = Descendants<GitDiffTab>(view).Single(tab => tab.IsSelected);
        var committedText = committedDiff.Editor.Text;
        Check(committedDiff.Editor.IsReadOnly && committedDiff.Key.Contains(((GitCommit)latest.Tag).Hash), "Historical diff is read-only and identifies its commit");
        DoubleClick(changedFile); await Task.Delay(150);
        Check(Descendants<GitDiffTab>(view).Count() == 1 && committedDiff.IsSelected, "Double-clicking the same commit file reuses its diff tab");
        await view.GitView.RefreshAsync();
        Check(committedDiff.Editor.Text == committedText, "Git refresh preserves the historical comparison instead of replacing it with working changes");
        initial.IsExpanded = true;
        await WaitUntilAsync(() => initial.Items.OfType<TreeViewItem>().Any(item => item.Tag is GitCommitFile { Path: "notes.txt", Status: 'A' }), "Expanding a root commit shows its added files");
        DoubleClick(initial.Items.OfType<TreeViewItem>().Single());
        await WaitUntilAsync(() => Descendants<GitDiffTab>(view).Count() == 2 && Descendants<GitDiffTab>(view).Any(tab => tab.IsSelected && tab.Editor.Text.Contains("--- /dev/null") && tab.Editor.Text.Contains("+Original text")), "The same file in another commit opens a separate comparison, including root additions");
        DoubleClick(changedFile);
        await WaitUntilAsync(() => committedDiff.IsSelected, "Double-clicking an already open comparison activates its main-panel tab");
        await view.GitView.RefreshAsync();
        Check(ReferenceEquals(tree.Items[0], latest) && latest.IsExpanded && initial.IsExpanded, "Refresh preserves expanded commits and loaded files");
        Check(message.Text == "Keep my draft" && latest.IsExpanded, "Refresh preserves the draft commit message and commit expansion");
        Check(((TextBlock)latest.Header).Text.StartsWith(((GitCommit)latest.Tag).Date.LocalDateTime.ToString("MM.dd HH:mm")), "Commit rows start with an MM.dd HH:mm timestamp");
        await Git("commit", "--allow-empty", "-m", "Latest empty commit"); await view.GitView.RefreshAsync();
        Check(tree.Items.Count == 3 && ((GitCommit)((TreeViewItem)tree.Items[0]).Tag).Subject == "Latest empty commit", "Refreshing history includes new commits");
        Check(ReferenceEquals(tree.Items[1], latest) && latest.IsExpanded, "New commits preserve expansion of older commits");
        window.BottomTabs.SelectedIndex = 1;
        await CaptureAsync(window, "git-history.png");
        File.WriteAllLines(Path.Combine(OutputDirectory, "git-history-results.txt"), Results);
    }
}
