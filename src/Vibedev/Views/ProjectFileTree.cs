using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;

namespace Vibedev.Views;

public sealed class ProjectFileTree : UserControl
{
    private readonly string directory;
    private readonly TreeView tree = new()
    {
        Padding = new(4, 4, 0, 8), BorderThickness = new(0), Background = Ui.Brush("#1D1D21"),
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
    public event Action<string>? FileActivated;
    internal TreeView Tree => tree;

    public ProjectFileTree(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        Content = tree;
        ScrollViewer.SetHorizontalScrollBarVisibility(tree, ScrollBarVisibility.Disabled);
        tree.MouseDoubleClick += (_, e) =>
        {
            var node = FindNode(e.OriginalSource as DependencyObject);
            if (node?.Tag is not string path || !File.Exists(path)) return;
            e.Handled = true; FileActivated?.Invoke(path);
        };
        tree.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || tree.SelectedItem is not TreeViewItem { Tag: string path } || !File.Exists(path)) return;
            e.Handled = true; FileActivated?.Invoke(path);
        };
        Refresh();
    }

    private static TreeViewItem? FindNode(DependencyObject? source)
    {
        while (source is not null && source is not TreeViewItem)
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        return source as TreeViewItem;
    }
    public void Refresh()
    {
        var expanded = Nodes(tree.Items).Where(n => n.IsExpanded && n.Tag is string).Select(n => (string)n.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = (tree.SelectedItem as TreeViewItem)?.Tag as string;
        tree.Items.Clear();
        if (!Directory.Exists(directory)) { tree.Items.Add(Ui.Text("Project directory is unavailable.", 12, "#F87171")); return; }
        var root = Node(directory, true);
        tree.Items.Add(root); root.IsExpanded = true;
        Restore(root, expanded, selected);
    }
    private void Restore(TreeViewItem node, HashSet<string> expanded, string? selected)
    {
        if (node.Tag is string path)
        {
            if (expanded.Contains(path)) node.IsExpanded = true;
            if (string.Equals(path, selected, StringComparison.OrdinalIgnoreCase)) node.IsSelected = true;
        }
        foreach (var child in node.Items.OfType<TreeViewItem>()) Restore(child, expanded, selected);
    }
    private static IEnumerable<TreeViewItem> Nodes(ItemCollection items)
    {
        foreach (var node in items.OfType<TreeViewItem>())
        {
            yield return node;
            foreach (var child in Nodes(node.Items)) yield return child;
        }
    }
    private TreeViewItem Node(string path, bool folder)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        var header = new Grid(); header.ColumnDefinitions.Add(new() { Width = new(22) }); header.ColumnDefinitions.Add(new());
        FrameworkElement icon;
        ShapePath? folderIcon = null;
        if (folder)
        {
            folderIcon = new ShapePath { Data = Geometry.Parse("M1,3 L7,3 L9,5 L17,5 L17,14 L1,14 Z"), Stroke = Ui.Brush("#C6A66D"), Fill = Ui.Brush("#C6A66D18"), StrokeThickness = 1.2, Width = 18, Height = 17, Stretch = Stretch.Uniform };
            icon = folderIcon;
        }
        else
        {
            var (label, color) = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".cs" => ("C#", "#6DBB8D"), ".csproj" or ".props" or ".targets" => ("◇", "#6DBB8D"),
                ".json" or ".jsonc" => ("{}", "#D4B867"), ".xaml" or ".xml" or ".html" => ("<>", "#77B9DC"),
                ".md" => ("M↓", "#A98DD4"), ".js" or ".ts" => ("JS", "#D4B867"),
                ".css" => ("#", "#77B9DC"), ".sln" or ".slnx" => ("◇", "#A98DD4"),
                _ => ("≡", "#8C8C9C")
            };
            icon = Ui.Text(label, 10, color); icon.VerticalAlignment = VerticalAlignment.Center;
            ((TextBlock)icon).FontFamily = new("Consolas");
        }
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        header.Children.Add(icon);
        var title = Ui.Text(name, 12); title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(title, 1); header.Children.Add(title);
        if (folder && name is "bin" or "obj" or "node_modules" or ".vs") title.Foreground = Ui.Brush("#777784");
        var node = new TreeViewItem { Header = header, Tag = path, ToolTip = path, Style = (Style)Application.Current.FindResource("ProjectTreeItem") };
        if (folder && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
        {
            node.Items.Add("Loading");
            node.Expanded += (_, e) =>
            {
                if (!ReferenceEquals(e.OriginalSource, node)) return;
                if (node.Items.Count == 1 && node.Items[0] is string) { node.Items.Clear(); AddChildren(node, path); }
                folderIcon!.Data = Geometry.Parse("M1,5 L7,5 L9,7 L18,7 L15,15 L1,15 Z M1,5 L1,3 L7,3 L9,5 L16,5 L16,7");
            };
            node.Collapsed += (_, e) => { if (ReferenceEquals(e.OriginalSource, node)) folderIcon!.Data = Geometry.Parse("M1,3 L7,3 L9,5 L17,5 L17,14 L1,14 Z"); };
        }
        return node;
    }
    private void AddChildren(TreeViewItem parent, string path)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(path).Where(Visible).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)) parent.Items.Add(Node(dir, true));
            foreach (var file in Directory.EnumerateFiles(path).Where(Visible).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)) parent.Items.Add(Node(file, false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { parent.Items.Add(Ui.Text(ex.Message, 11, "#F87171")); }
    }
    private static bool Visible(string path) => !Path.GetFileName(path).Equals(".git", StringComparison.OrdinalIgnoreCase);

    public void Reveal(string file)
    {
        var relative = Path.GetRelativePath(directory, Path.GetFullPath(file));
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return;
        if (tree.Items.OfType<TreeViewItem>().FirstOrDefault() is not { } node) return;
        var current = directory; node.IsExpanded = true;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            var next = node.Items.OfType<TreeViewItem>().FirstOrDefault(n => string.Equals(n.Tag as string, current, StringComparison.OrdinalIgnoreCase));
            if (next is null) return;
            node = next;
            if (Directory.Exists(current)) node.IsExpanded = true;
        }
        node.IsSelected = true; if (IsVisible) node.BringIntoView();
    }
}
