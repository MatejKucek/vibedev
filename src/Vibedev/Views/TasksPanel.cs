using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Vibedev.Core;

namespace Vibedev.Views;

/// <summary>Dockable panel listing a project's open tasks with the model and effort from each task's frontmatter.</summary>
internal sealed class TasksPanel : UserControl, IDisposable
{
    private readonly string directory;
    private readonly ListBox files = new() { Margin = new(4) };
    private readonly TextBlock empty = Ui.Text("No open tasks in tasks/open.", 12, "#9898A6");
    private readonly TextBlock count = Ui.Text("0 tasks", 11, "#C4B5DB");
    private readonly DispatcherTimer timer;
    private string signature = "";
    internal ListBox List => files;
    internal Action? CloseRequested { get; set; }

    public TasksPanel(string projectPath, Func<string, Task> openFile)
    {
        directory = Path.Combine(projectPath, "tasks", "open");
        var root = new DockPanel { Background = Ui.Brush("#1D1D21") };
        var header = new DockPanel { Margin = new(12, 8, 6, 8) };
        var close = Ui.Button("×", () => CloseRequested?.Invoke()); close.Padding = new(8, 2, 8, 2); close.Background = System.Windows.Media.Brushes.Transparent; close.BorderThickness = new(0); close.ToolTip = "Hide tasks (click the task count to show again)";
        var reload = Ui.Button("↻", () => Refresh(true)); reload.Padding = new(6, 2, 6, 2); reload.ToolTip = "Refresh open tasks";
        DockPanel.SetDock(close, Dock.Right); DockPanel.SetDock(reload, Dock.Right); header.Children.Add(close); header.Children.Add(reload);
        var title = Ui.Section("TASKS"); title.Margin = new(0, 0, 10, 0);
        count.VerticalAlignment = VerticalAlignment.Center; header.Children.Add(title); header.Children.Add(count);
        var top = new StackPanel { Background = Ui.Brush("#282431") };
        top.Children.Add(header); top.Children.Add(Columns(new Border(), Ui.Section("TASK"), Ui.Section("MODEL"), Ui.Section("EFFORT")));
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        Grid.SetIsSharedSizeScope(root, true);
        var content = new Grid(); content.Children.Add(files); empty.Margin = new(14); empty.VerticalAlignment = VerticalAlignment.Top; content.Children.Add(empty);
        root.Children.Add(content);
        Content = root;
        files.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || files.SelectedItem is not ListBoxItem { Tag: string path }) return;
            e.Handled = true; await openFile(path);
        };
        files.MouseLeftButtonUp += async (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(files, e.OriginalSource as DependencyObject) is not ListBoxItem { Tag: string path }) return;
            e.Handled = true; await openFile(path);
        };
        timer = new(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher) ;
        Refresh(true);
    }

    private static Grid Columns(UIElement copy, UIElement name, UIElement model, UIElement effort)
    {
        var grid = new Grid { Margin = new(14, 4, 14, 4) };
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto, SharedSizeGroup = "copy", MinWidth = 44 });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto, SharedSizeGroup = "name", MinWidth = 80, MaxWidth = 280 });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto, SharedSizeGroup = "model", MinWidth = 90 });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto, SharedSizeGroup = "effort", MinWidth = 70 });
        Grid.SetColumn(name, 1); Grid.SetColumn(model, 2); Grid.SetColumn(effort, 3);
        foreach (var cell in new[] { copy, name, model, effort })
        {
            if (cell is FrameworkElement element && !ReferenceEquals(cell, copy)) element.Margin = new(12, 0, 0, 0);
            grid.Children.Add(cell);
        }
        return grid;
    }

    /// <summary>The `/task` command for a task file: its leading number (`123-fix-x.md` gives `123`), else the file name without extension.</summary>
    internal static string TaskCommand(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var digits = new string(stem.TakeWhile(char.IsDigit).ToArray());
        return "/task " + (digits.Length > 0 ? digits : stem);
    }

    public int Refresh(bool force = false)
    {
        var paths = ProjectTasksView.OpenTaskFiles(directory);
        var stamps = string.Join("|", paths.Select(p => p + "@" + File.GetLastWriteTimeUtc(p).Ticks));
        if (!force && stamps == signature) return paths.Length;
        signature = stamps;
        var selected = (files.SelectedItem as ListBoxItem)?.Tag as string;
        files.Items.Clear();
        foreach (var path in paths)
        {
            var meta = TaskMetadata.Read(path);
            var label = Ui.Text(Path.GetRelativePath(directory, path), 12); label.TextWrapping = TextWrapping.NoWrap; label.TextTrimming = TextTrimming.CharacterEllipsis; label.VerticalAlignment = VerticalAlignment.Center;
            var model = Ui.Text(meta.Model.Length > 0 ? meta.Model : "—", 12, meta.Model.Length > 0 ? "#C4B5DB" : "#777784"); model.TextWrapping = TextWrapping.NoWrap; model.VerticalAlignment = VerticalAlignment.Center;
            var effort = Ui.Text(meta.Effort.Length > 0 ? meta.Effort : "—", 12, meta.Effort.Length > 0 ? "#FBBF24" : "#777784"); effort.TextWrapping = TextWrapping.NoWrap; effort.VerticalAlignment = VerticalAlignment.Center;
            var command = TaskCommand(path);
            var copy = Ui.Button("copy", () => { try { Clipboard.SetText(command); } catch (System.Runtime.InteropServices.ExternalException) { } });
            copy.Padding = new(6, 0, 6, 0); copy.FontSize = 11; copy.VerticalAlignment = VerticalAlignment.Center; copy.ToolTip = $"Copy \"{command}\"";
            var row = Columns(copy, label, model, effort); row.Margin = new(0);
            var item = new ListBoxItem { Content = row, Tag = path, ToolTip = path, Padding = new(10, 5, 10, 5), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            files.Items.Add(item);
            if (path.Equals(selected, StringComparison.OrdinalIgnoreCase)) files.SelectedItem = item;
        }
        empty.Visibility = paths.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        count.Text = paths.Length == 1 ? "1 task" : $"{paths.Length} tasks";
        return paths.Length;
    }

    public void Dispose() => timer.Stop();
}
