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
    public static readonly DependencyProperty IsMultiSelectedProperty =
        DependencyProperty.RegisterAttached("IsMultiSelected", typeof(bool), typeof(ProjectFileTree), new FrameworkPropertyMetadata(false));
    public static bool GetIsMultiSelected(DependencyObject o) => (bool)o.GetValue(IsMultiSelectedProperty);
    public static void SetIsMultiSelected(DependencyObject o, bool value) => o.SetValue(IsMultiSelectedProperty, value);

    private readonly HashSet<string> selection = new(StringComparer.OrdinalIgnoreCase);
    private string? anchor;
    public event Action<string>? FileActivated;
    internal TreeView Tree => tree;
    internal IReadOnlyCollection<string> SelectedPaths => selection;

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
        tree.PreviewMouseLeftButtonDown += OnMouseDown;
        tree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete) { e.Handled = true; DeleteSelected(); return; }
            if (e.Key != Key.Enter || tree.SelectedItem is not TreeViewItem { Tag: string path } || !File.Exists(path)) return;
            e.Handled = true; FileActivated?.Invoke(path);
        };
        Refresh();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && FindParent<System.Windows.Controls.Primitives.ToggleButton>(src) is not null) return;
        var node = FindNode(e.OriginalSource as DependencyObject);
        if (node?.Tag is not string path) return;
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Shift) && anchor is not null)
        {
            var visible = VisibleNodes(tree.Items).Where(n => n.Tag is string).ToList();
            var a = visible.FindIndex(n => string.Equals((string)n.Tag, anchor, StringComparison.OrdinalIgnoreCase));
            var b = visible.FindIndex(n => ReferenceEquals(n, node));
            if (a < 0 || b < 0) return;
            if (!mods.HasFlag(ModifierKeys.Control)) selection.Clear();
            for (var i = Math.Min(a, b); i <= Math.Max(a, b); i++) selection.Add((string)visible[i].Tag);
        }
        else if (mods.HasFlag(ModifierKeys.Control))
        {
            if (!selection.Remove(path)) selection.Add(path);
            anchor = path;
        }
        else
        {
            selection.Clear(); selection.Add(path); anchor = path;
            ApplySelection();
            return; // default handling selects the node
        }
        e.Handled = true;
        node.Focus();
        ApplySelection();
    }

    private void ApplySelection()
    {
        foreach (var n in Nodes(tree.Items)) SetIsMultiSelected(n, n.Tag is string p && selection.Contains(p) && selection.Count > 1);
    }

    private static T? FindParent<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null && source is not T)
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        return source as T;
    }

    private static IEnumerable<TreeViewItem> VisibleNodes(ItemCollection items)
    {
        foreach (var node in items.OfType<TreeViewItem>())
        {
            yield return node;
            if (node.IsExpanded) foreach (var child in VisibleNodes(node.Items)) yield return child;
        }
    }

    private void DeleteSelected()
    {
        var paths = selection.Count > 0 ? selection.ToList() : (tree.SelectedItem as TreeViewItem)?.Tag is string one ? [one] : [];
        // drop entries whose ancestor folder is also selected
        paths = paths.Where(p => !paths.Any(o => !string.Equals(o, p, StringComparison.OrdinalIgnoreCase)
            && p.StartsWith(o.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToList();
        paths = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (paths.Count == 0) return;
        var text = paths.Count == 1
            ? $"Move '{Path.GetFileName(paths[0])}' to the Recycle Bin?"
            : $"Move {paths.Count} items to the Recycle Bin?\n\n" + string.Join("\n", paths.Take(8).Select(p => Path.GetFileName(p))) + (paths.Count > 8 ? "\n…" : "");
        if (MessageBox.Show(Window.GetWindow(this), text, "Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        foreach (var p in paths)
        {
            try
            {
                if (Directory.Exists(p)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(p, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(p, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { Ui.Error(ex.Message, Window.GetWindow(this)); }
        }
        selection.Clear(); anchor = null;
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
        AddChildren(tree.Items, directory);
        foreach (var node in tree.Items.OfType<TreeViewItem>()) Restore(node, expanded, selected);
        selection.IntersectWith(Nodes(tree.Items).Select(n => n.Tag).OfType<string>());
        ApplySelection();
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
                if (node.Items.Count == 1 && node.Items[0] is string) { node.Items.Clear(); AddChildren(node.Items, path); }
                folderIcon!.Data = Geometry.Parse("M1,5 L7,5 L9,7 L18,7 L15,15 L1,15 Z M1,5 L1,3 L7,3 L9,5 L16,5 L16,7");
            };
            node.Collapsed += (_, e) => { if (ReferenceEquals(e.OriginalSource, node)) folderIcon!.Data = Geometry.Parse("M1,3 L7,3 L9,5 L17,5 L17,14 L1,14 Z"); };
        }
        return node;
    }
    private void AddChildren(ItemCollection items, string path)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(path).Where(Visible).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)) items.Add(Node(dir, true));
            foreach (var file in Directory.EnumerateFiles(path).Where(Visible).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)) items.Add(Node(file, false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { items.Add(Ui.Text(ex.Message, 11, "#F87171")); }
    }
    private static bool Visible(string path) => !Path.GetFileName(path).Equals(".git", StringComparison.OrdinalIgnoreCase);

    public void Reveal(string file)
    {
        var relative = Path.GetRelativePath(directory, Path.GetFullPath(file));
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return;
        var items = tree.Items;
        TreeViewItem? node = null;
        var current = directory;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            var next = items.OfType<TreeViewItem>().FirstOrDefault(n => string.Equals(n.Tag as string, current, StringComparison.OrdinalIgnoreCase));
            if (next is null) return;
            node = next;
            if (Directory.Exists(current)) node.IsExpanded = true;
            items = node.Items;
        }
        if (node is null) return;
        node.IsSelected = true; if (IsVisible) node.BringIntoView();
    }
}
