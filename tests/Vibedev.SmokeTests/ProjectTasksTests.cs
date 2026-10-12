using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vibedev;
using Vibedev.Services;
using Vibedev.Views;

internal static partial class Program
{
    private static async Task ExecuteProjectTasksAsync(MainWindow window, string root)
    {
        Check(window.WindowState == WindowState.Maximized, "The application starts maximized");
        var projectPath = Path.Combine(root, "task-project"); Directory.CreateDirectory(projectPath); window.AddPath(projectPath);
        window.OpenProject(window.Projects[0]);
        var view = (ProjectView)window.Documents().Single().Content;
        window.UpdateLayout();
        Check(view.TaskCount.Text == "0 tasks", "A project without tasks/open shows 0 tasks");
        var navigation = Descendants<TabControl>(view).Single(t => t.Items.OfType<TabItem>().Any(i => i.Header as string == "TASKS"));
        var tasksTab = navigation.Items.OfType<TabItem>().Single(t => t.Header as string == "TASKS");
        navigation.SelectedItem = tasksTab; window.UpdateLayout();
        var list = Descendants<ListBox>((DependencyObject)tasksTab.Content).Single();
        Check(list.Items.Count == 0 && Descendants<TextBlock>((DependencyObject)tasksTab.Content).Any(t => t.Text == "No open tasks in tasks/open." && t.IsVisible),
            "The Tasks tab shows an empty state when there are no open tasks");
        var directory = Path.Combine(projectPath, "tasks", "open"); Directory.CreateDirectory(directory);
        var alpha = Path.Combine(directory, "alpha.md"); var unicode = Path.Combine(directory, "ž-task.MD");
        await File.WriteAllTextAsync(alpha, "# First task\nRead this task.\n");
        await File.WriteAllTextAsync(unicode, "# Unicode task\nSecond task.\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "notes.txt"), "Not a task.");
        await File.WriteAllTextAsync(Path.Combine(directory, "backup.md.bak"), "Not a task.");
        Directory.CreateDirectory(Path.Combine(directory, "nested"));
        var nested = Path.Combine(directory, "nested", "alpha.md");
        await File.WriteAllTextAsync(nested, "# Nested task\n");
        Directory.CreateDirectory(Path.Combine(directory, "folder.md"));
        var closed = Path.Combine(projectPath, "tasks", "closed"); Directory.CreateDirectory(closed);
        await File.WriteAllTextAsync(Path.Combine(closed, "closed.md"), "Closed task.");
        await WaitUntilAsync(() => view.TaskCount.Text == "3 tasks" && list.Items.Count == 3,
            "The watcher updates the count and list for direct and nested Markdown files only");
        Check(list.Items.OfType<ListBoxItem>().Select(i => (string)i.Tag).SequenceEqual(new[] { alpha, nested, unicode }),
            "Open tasks are sorted by relative path and include Unicode and uppercase Markdown extensions");
        Check(list.Items.OfType<ListBoxItem>().Select(i => ((TextBlock)i.Content).Text).SequenceEqual(new[] { "alpha.md", Path.Combine("nested", "alpha.md"), Path.GetFileName(unicode) }),
            "Relative paths distinguish tasks with the same filename in subfolders");
        window.UpdateLayout();
        var first = list.Items.OfType<ListBoxItem>().First();
        ((TextBlock)first.Content).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
        await WaitUntilAsync(() => view.Editors.Any(e => e.FilePath == alpha), "A single click opens a task in the project's main file editor");
        Check(view.Editors.Single(e => e.FilePath == alpha).Editor.Text == await File.ReadAllTextAsync(alpha), "The editor displays the task file's Markdown contents");
        list.SelectedItem = list.Items[1];
        list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(list)!, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        await WaitUntilAsync(() => view.Editors.Count() == 2 && view.Editors.Any(e => e.FilePath == nested), "Enter opens the selected nested task file");
        ((TextBlock)first.Content).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
        await Task.Delay(100);
        Check(view.Editors.Count() == 2 && view.Editors.Single(e => e.FilePath == alpha).IsSelected, "Clicking an already open task selects its existing file tab");
        var renamed = Path.Combine(directory, "renamed.md"); File.Move(alpha, renamed);
        await WaitUntilAsync(() => list.Items.OfType<ListBoxItem>().Any(i => i.Tag as string == renamed) && view.TaskCount.Text == "3 tasks", "Renaming a task updates the list and preserves its count");
        File.Move(renamed, Path.Combine(closed, "renamed.md"));
        await WaitUntilAsync(() => view.TaskCount.Text == "2 tasks" && list.Items.Count == 2, "Moving a task out of open updates the list and count");
        File.Delete(nested);
        await WaitUntilAsync(() => view.TaskCount.Text == "1 task" && list.Items.Count == 1, "Deleting a nested task updates the list and singular count");
        var deepDirectory = Path.Combine(directory, "nested", "deeper"); Directory.CreateDirectory(deepDirectory);
        await File.WriteAllTextAsync(Path.Combine(deepDirectory, "deep.md"), "# Deep task\n");
        await WaitUntilAsync(() => view.TaskCount.Text == "2 tasks" && list.Items.Count == 2, "New Markdown tasks in deeper subfolders update the list and count");
        Directory.Delete(directory, true);
        await WaitUntilAsync(() => view.TaskCount.Text == "0 tasks" && list.Items.Count == 0, "Removing tasks/open returns the count and list to empty");
        Directory.CreateDirectory(directory); await File.WriteAllTextAsync(alpha, "# Existing task\n");
        using var another = new ProjectView(window, window.Projects[0]);
        Check(another.TaskCount.Text == "1 task", "Existing open task files are counted when a panel is constructed");
        var terminal = window.TerminalHost.Children.OfType<TerminalControl>().Single();
        await WaitUntilAsync(() => terminal.IsReady, "The terminal initializes during task navigation");
        await WaitUntilAsync(() => list.Items.Count == 1, "The task list updates after recreating tasks/open");
        await CaptureAsync(window, "project-tasks.png");
        File.WriteAllLines(Path.Combine(OutputDirectory, "tasks-results.txt"), Results);
        window.Close();
    }
}
