using System.IO;
using System.Windows;
using System.Windows.Controls;
using Vibedev;
using Vibedev.Core;
using Vibedev.Views;

internal static partial class Program
{
    private static async Task ExecuteGitSyncAsync(MainWindow window, string root)
    {
        async Task Git(string path, params string[] args)
        {
            var result = await ProcessRunner.RunAsync(new("git", args, path));
            Check(result.Success, "Sync fixture: git " + string.Join(" ", args));
        }
        var remote = Path.Combine(root, "remote.git"); var repo = Path.Combine(root, "sync-project");
        await Git(root, "init", "--bare", "--initial-branch=main", remote);
        await Git(root, "clone", remote, repo);
        await Git(repo, "config", "user.name", "Sync test");
        await Git(repo, "config", "user.email", "sync@example.invalid");
        await Git(repo, "config", "commit.gpgsign", "false");
        await Git(repo, "config", "pull.rebase", "false");
        await Git(repo, "config", "core.hooksPath", Path.Combine(root, "no-hooks"));
        var file = Path.Combine(repo, "notes.txt"); await File.WriteAllTextAsync(file, "Initial text\n");
        await Git(repo, "add", "--all"); await Git(repo, "commit", "-m", "Initial"); await Git(repo, "push");
        window.AddPath(repo); var project = window.Projects.Single();
        window.OpenProject(project); var view = (ProjectView)window.Documents().Single().Content;
        await view.OpenFileAsync(file); view.ShowGit(); await view.GitView.RefreshAsync(); window.UpdateLayout();
        var message = Descendants<TextBox>(view.GitView).Single();
        var sync = Descendants<Button>(view.GitView).Single(b => b.Content?.ToString() == "Commit all, pull & push");
        Check(sync.IsVisible && sync.ActualWidth > 0, "Git tab shows the combined commit, pull and push button");
        message.Text = " "; sync.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!project.Busy, "Blank commit message does not start Git sync");
        var editor = view.Editors.Single(); editor.Editor.AppendText("Saved by one click\n");
        var newFile = Path.Combine(repo, "new.txt"); await File.WriteAllTextAsync(newFile, "New unstaged file\n");
        message.Text = "One click sync";
        sync.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(project.Busy && !sync.IsEnabled, "Combined Git action disables controls while saving and syncing");
        await WaitUntilAsync(() => !project.Busy, "Combined Git action finishes", 20000);
        await WaitUntilAsync(() => message.Text.Length == 0, "Successful sync clears the message");
        Check(!project.Failed && project.Activity == "sync completed", "Combined action reports success");
        Check(!editor.IsDirty && (await File.ReadAllTextAsync(file)).Contains("Saved by one click"), "Combined action saves open editor changes before committing");
        var published = await ProcessRunner.RunAsync(new("git", ["show", "main:notes.txt"], remote));
        Check(published.Success && published.Output.Contains("Saved by one click"), "One click publishes saved editor contents to the remote");
        published = await ProcessRunner.RunAsync(new("git", ["show", "main:new.txt"], remote));
        Check(published.Success && published.Output.Contains("New unstaged file"), "One click stages and publishes new files");
        Check((await window.Git.StatusAsync(repo)).Changes == 0, "Successful sync leaves no pending changes");
        await Git(repo, "config", "remote.origin.pushurl", Path.Combine(root, "missing.git"));
        await File.WriteAllTextAsync(newFile, "Retain local commit on failure\n");
        message.Text = "Push failure"; sync.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => !project.Busy, "Failed combined action finishes", 20000);
        Check(project.Failed && message.Text == "Push failure" && sync.IsEnabled, "Failed sync retains the message, reports failure and re-enables controls");
        Check(window.OperationOutput.Text.Contains("git push failed"), "Task output identifies the failed step");
        await CaptureAsync(window, "git-sync.png");
        File.WriteAllLines(Path.Combine(OutputDirectory, "git-sync-results.txt"), Results);
    }
}
