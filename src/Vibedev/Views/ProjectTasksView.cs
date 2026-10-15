using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Vibedev.Views;

internal sealed class ProjectTasksView : UserControl
{
    private readonly string directory;
    private readonly ListBox files = new() { Margin = new(4) };
    private readonly TextBlock empty = Ui.Text("No open tasks in tasks/open.", 12, "#9898A6");

    public ProjectTasksView(string projectPath, Func<string, Task> openFile, Action refresh)
    {
        directory = Path.Combine(projectPath, "tasks", "open");
        var root = new DockPanel { Background = Ui.Brush("#1D1D21") };
        var header = new DockPanel { Margin = new(10, 8, 4, 8) };
        var reload = Ui.Button("↻", refresh); reload.Padding = new(6, 2, 6, 2); reload.ToolTip = "Refresh open tasks";
        DockPanel.SetDock(reload, Dock.Right); header.Children.Add(reload); header.Children.Add(Ui.Section("OPEN TASKS"));
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var content = new Grid(); content.Children.Add(files); empty.Margin = new(14); content.Children.Add(empty);
        root.Children.Add(content); Content = root;
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
    }

    public int Refresh()
    {
        string[] paths;
        try
        {
            paths = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetRelativePath(directory, path), StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (DirectoryNotFoundException) { paths = []; }
        var selected = (files.SelectedItem as ListBoxItem)?.Tag as string;
        files.Items.Clear();
        foreach (var path in paths)
        {
            var label = Ui.Text(Path.GetRelativePath(directory, path), 12); label.TextWrapping = TextWrapping.NoWrap; label.TextTrimming = TextTrimming.CharacterEllipsis;
            var item = new ListBoxItem { Content = label, Tag = path, ToolTip = path, Padding = new(10, 7, 10, 7) };
            files.Items.Add(item);
            if (path.Equals(selected, StringComparison.OrdinalIgnoreCase)) files.SelectedItem = item;
        }
        empty.Visibility = paths.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        return paths.Length;
    }
}
