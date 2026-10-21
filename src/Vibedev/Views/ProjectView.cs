using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Effects;
using Vibedev.Core;
using Vibedev.Services;

namespace Vibedev.Views;

public sealed class ProjectView : UserControl, IDisposable
{
    private const int GitTab = 0;
    private readonly MainWindow main;
    private readonly ProjectFileTree tree;
    private readonly TabControl navigation = new();
    private readonly ProjectSearchView search;
    private readonly ProjectGitView gitView;
    private readonly ProjectTasksView tasksView;
    private readonly TasksPanel tasksPanel;
    private readonly ColumnDefinition tasksColumn = new() { Width = GridLength.Auto, MinWidth = 200 };
    private readonly ColumnDefinition tasksSplitterColumn = new() { Width = new(4) };
    private readonly GridSplitter tasksSplitter = new() { Width = 4 };
    private GridLength tasksWidth = GridLength.Auto;
    internal bool TasksVisible => tasksPanel.Visibility == Visibility.Visible;
    private readonly TabControl tabs = new();
    private readonly CSharpLanguageService language;
    private readonly ProjectFileMonitor? monitor;
    private bool disposed;
    private readonly Dictionary<string, string> refreshWarnings = new(StringComparer.OrdinalIgnoreCase);
    private readonly ComboBox launches = new() { MinWidth = 60, MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new(0, 0, 6, 0), DisplayMemberPath = "Name", ToolTip = ".vscode/launch.json", Tag = "No launch configurations" };
    private readonly Button run;
    private readonly TextBlock placeholder = Ui.Text("Open a file to start editing\n\nF   Quick open a file\nCtrl+P   Find a file in the sidebar\nCtrl+Shift+F   Search this project", 13, "#9898A6");
    public ProjectState Project { get; }
    public IEnumerable<EditorTab> Editors => tabs.Items.OfType<EditorTab>();
    public Action<Orientation>? SplitRequested { get; set; }
    public Action? FloatRequested { get; set; }
    public Action? CloseRequested { get; set; }
    internal Action<Point>? DragRequested { get; set; }
    internal TextBlock ProjectTitle { get; }
    internal TextBlock TaskCount { get; } = Ui.Text("0 tasks", 11, "#C4B5DB");
    internal Button CloseProjectButton { get; }
    internal Border ProjectFrame { get; }
    public LaunchConfiguration? SelectedLaunch => launches.SelectedItem as LaunchConfiguration;
    internal ProjectFileTree FileTree => tree;
    internal ProjectSearchView Search => search;
    internal ProjectGitView GitView => gitView;
    internal EditorLanguageFeatures? LanguageFeatures(EditorTab editor) => language.Features(editor);

    public ProjectView(MainWindow main, ProjectState project)
    {
        this.main = main; Project = project; Focusable = true;
        language = new(this, text => main.Log($"[{Project.Entry.Name}] {text}"));
        tree = new(project.Entry.Path);
        tree.FileActivated += async path => await OpenFileAsync(path);
        search = new(this);
        gitView = new(main, this);
        tasksView = new(project.Entry.Path, OpenFileAsync, RefreshTasks);
        tasksPanel = new(project.Entry.Path, OpenFileAsync) { CloseRequested = () => SetTasksVisible(false) };        tabs.Style = (Style)Application.Current.FindResource(typeof(TabControl));
        var root = new Grid(); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        var header = new Grid(); header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var identity = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 12, 0) };
        identity.ColumnDefinitions.Add(new() { Width = GridLength.Auto, MaxWidth = 160 }); identity.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); identity.ColumnDefinitions.Add(new());
        ProjectTitle = Ui.Text(project.Entry.Name, 14);
        ProjectTitle.FontWeight = FontWeights.SemiBold; ProjectTitle.TextWrapping = TextWrapping.NoWrap; ProjectTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        ProjectTitle.Margin = new(0, 0, 10, 0); ProjectTitle.VerticalAlignment = VerticalAlignment.Center; ProjectTitle.ToolTip = project.Entry.Path + "\nDrag the name to move or dock this panel\nF: quick open file · Shift+F: toggle focus mode (outside text inputs)\nRight-click for Git, float/dock, close and panel layout actions";
        ProjectDragHandle.Attach(ProjectTitle, point => DragRequested?.Invoke(point));
        identity.Children.Add(ProjectTitle);
        TaskCount.Margin = new(0, 0, 10, 0); TaskCount.VerticalAlignment = VerticalAlignment.Center; TaskCount.TextWrapping = TextWrapping.NoWrap; TaskCount.ToolTip = "Click to hide tasks"; TaskCount.Cursor = Cursors.Hand; TaskCount.MouseLeftButtonUp += (_, e) => { e.Handled = true; SetTasksVisible(!TasksVisible); }; Grid.SetColumn(TaskCount, 1); identity.Children.Add(TaskCount);
        var projectPath = Ui.Text(project.Entry.Path, 11, "#9898A6"); projectPath.TextWrapping = TextWrapping.NoWrap; projectPath.TextTrimming = TextTrimming.CharacterEllipsis; projectPath.VerticalAlignment = VerticalAlignment.Center; projectPath.ToolTip = project.Entry.Path; Grid.SetColumn(projectPath, 2); identity.Children.Add(projectPath);
        header.Children.Add(identity);
        CloseProjectButton = Ui.Button("×", () => CloseRequested?.Invoke()); CloseProjectButton.Padding = new(8, 2, 8, 2); CloseProjectButton.Margin = new(0); CloseProjectButton.Background = System.Windows.Media.Brushes.Transparent; CloseProjectButton.BorderThickness = new(0); CloseProjectButton.VerticalAlignment = VerticalAlignment.Center; CloseProjectButton.ToolTip = "Close project panel"; Grid.SetColumn(CloseProjectButton, 1); header.Children.Add(CloseProjectButton);
        root.Children.Add(new Border { Background = Ui.Brush("#282431"), BorderBrush = Ui.Brush("#40364F"), BorderThickness = new(0, 0, 0, 1), CornerRadius = new(5, 5, 0, 0), Padding = new(12, 7, 8, 7), Child = header });
        var tools = new Grid { Margin = new(10, 7, 8, 7) };
        tools.ColumnDefinitions.Add(new() { MaxWidth = 360 }); tools.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        tools.Children.Add(launches);
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; Grid.SetColumn(actions, 1); tools.Children.Add(actions);
        run = Ui.Button("▶ Run", () => main.RunProject(this), true); actions.Children.Add(run);
        var reloadLaunches = Ui.Button("↻", LoadLaunches); reloadLaunches.ToolTip = "Reload .vscode/launch.json";
        actions.Children.Add(reloadLaunches);
        var panelMenu = new ContextMenu { Style = (Style)main.FindResource("ProjectContextMenu") };
        foreach (var (label, action) in new (string, Action)[] {
            ("Split →", () => SplitRequested?.Invoke(Orientation.Horizontal)), ("Split ↓", () => SplitRequested?.Invoke(Orientation.Vertical)),
            ("↗ Float", () => FloatRequested?.Invoke()), ("C# server", async () => { try { await language.EnableAsync(); } catch (Exception ex) { main.Log("C# server: " + ex.Message); } }) })
        {
            var item = new MenuItem { Header = label, Style = (Style)main.FindResource("ProjectMenuItem") };
            item.Click += (_, _) => action(); panelMenu.Items.Add(item);
        }
        var more = Ui.Button("⋯", () => panelMenu.IsOpen = true); more.ToolTip = "Panel actions: split, float and C# server"; more.ContextMenu = panelMenu;
        panelMenu.PlacementTarget = more; panelMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        more.Margin = new(0); actions.Children.Add(more);
        foreach (Button button in actions.Children) button.Padding = new(9, 5, 9, 5);
        Grid.SetRow(tools, 1); root.Children.Add(tools);
        var work = new Grid(); work.ColumnDefinitions.Add(tasksColumn); work.ColumnDefinitions.Add(tasksSplitterColumn);
        work.ColumnDefinitions.Add(new() { Width = new(340), MinWidth = 130 }); work.ColumnDefinitions.Add(new() { Width = new(4) }); work.ColumnDefinitions.Add(new() { MinWidth = 120 });
        work.Children.Add(tasksPanel); tasksSplitter.HorizontalAlignment = HorizontalAlignment.Stretch; Grid.SetColumn(tasksSplitter, 1); work.Children.Add(tasksSplitter);
        var explorer = new DockPanel { Background = Ui.Brush("#1D1D21") };
        var fileHeader = new DockPanel { Margin = new(10, 8, 4, 8) }; var refresh = Ui.Button("↻", RefreshTree); refresh.Padding = new(6, 2, 6, 2); refresh.ToolTip = "Refresh files, preserving expanded folders"; DockPanel.SetDock(refresh, Dock.Right); fileHeader.Children.Add(refresh); fileHeader.Children.Add(Ui.Section("FILES")); DockPanel.SetDock(fileHeader, Dock.Top); explorer.Children.Add(fileHeader); explorer.Children.Add(tree);
        navigation.Style = (Style)Application.Current.FindResource(typeof(TabControl));
        navigation.Items.Add(new TabItem { Header = "Git", Content = gitView, ToolTip = "Review diffs, stage files and commit changes", Style = (Style)Application.Current.FindResource(typeof(TabItem)), Padding = new Thickness(8, 8, 8, 8) });
        navigation.Items.Add(new TabItem { Header = "Explorer", Content = explorer, ToolTip = "Browse project files", Style = (Style)Application.Current.FindResource(typeof(TabItem)), Padding = new Thickness(8, 8, 8, 8) });
        navigation.Items.Add(new TabItem { Header = "Search", Content = search, ToolTip = "Search files and contents (Ctrl+Shift+F), or filenames (Ctrl+P)", Style = (Style)Application.Current.FindResource(typeof(TabItem)), Padding = new Thickness(8, 8, 8, 8) });
        navigation.Items.Add(new TabItem { Header = "Tasks", Content = tasksView, ToolTip = "Open task files in tasks/open", Style = (Style)Application.Current.FindResource(typeof(TabItem)), Padding = new Thickness(8, 8, 8, 8) });
        navigation.FontSize = 12;
        navigation.SelectionChanged += async (_, e) =>
        {
            if (!ReferenceEquals(e.Source, navigation)) return;
            if (navigation.SelectedIndex == GitTab) await gitView.RefreshAsync();
            if (navigation.SelectedItem is TabItem { Content: ProjectTasksView }) RefreshTasks();
        };
        navigation.SelectedIndex = 0; Grid.SetColumn(navigation, 2); work.Children.Add(navigation);
        var splitter = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch }; Grid.SetColumn(splitter, 3); work.Children.Add(splitter);
        var editing = new Grid(); editing.Children.Add(tabs); placeholder.Margin = new(16); placeholder.TextAlignment = TextAlignment.Center; placeholder.HorizontalAlignment = HorizontalAlignment.Center; placeholder.VerticalAlignment = VerticalAlignment.Center; editing.Children.Add(placeholder); Grid.SetColumn(editing, 4); work.Children.Add(editing);
        Grid.SetRow(work, 2); root.Children.Add(work);
        ProjectFrame = new Border { BorderBrush = Ui.Brush("#6E568F"), BorderThickness = new(1), CornerRadius = new(6), Margin = new(4), Background = Ui.Brush("#18181B"), Child = root };
        Content = ProjectFrame;
        launches.SelectionChanged += (_, _) => RefreshState();
        PreviewKeyDown += (_, e) =>
        {
            main.HandleFocusKey(e);
            if (e.Key == Key.F && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { e.Handled = true; ShowSearch(); }
            if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; ShowSearch(true); }
        };
        LoadLaunches(); RefreshTree(); RefreshState();
        try { monitor = new(Project.Entry.Path, Dispatcher, RefreshFilesAsync, text => main.Log($"[{Project.Entry.Name}] {text}")); }
        catch (Exception ex) { main.Log($"[{Project.Entry.Name}] Live file updates unavailable: {ex.Message}"); }
    }
    internal void SetActive(bool active)
    {
        if (ProjectFrame.Tag is bool previous && previous == active) return;
        ProjectFrame.Tag = active;
        ProjectFrame.BorderBrush = Ui.Brush(active ? "#3B9EFF" : "#6E568F");
        ProjectFrame.Effect = active ? new DropShadowEffect { Color = System.Windows.Media.Color.FromRgb(0x3B, 0x9E, 0xFF), BlurRadius = 6, ShadowDepth = 0, Opacity = 0.6 } : null;
    }
    public void LoadLaunches()
    {
        try
        {
            var name = SelectedLaunch?.Name; var index = launches.SelectedIndex;
            launches.ItemsSource = LaunchService.Read(Project.Entry.Path);
            var items = launches.Items.OfType<LaunchConfiguration>().ToArray();
            launches.SelectedItem = (index >= 0 && index < items.Length && items[index].Name == name ? items[index] : null) ?? items.FirstOrDefault(c => c.Name == name) ?? items.FirstOrDefault();
        }
        catch (Exception ex) { launches.ItemsSource = null; main.Log($"[{Project.Entry.Name}] launch.json: {ex.Message}"); }
        launches.IsEnabled = launches.Items.Count > 0;
        launches.ToolTip = launches.Items.Count == 0 ? "No configurations. Add .vscode/launch.json, then refresh." : ".vscode/launch.json"; RefreshState();
    }
    public void RefreshState()
    {
        run.IsEnabled = launches.Items.Count > 0 && main.RunningProject is null;
        gitView.RefreshStatus();
        if (navigation.SelectedIndex == GitTab) _ = gitView.RefreshAsync();
    }
    public void RefreshTree() { tree.Refresh(); RefreshTasks(); }
    internal void SetTasksVisible(bool visible)
    {
        if (visible == TasksVisible) return;
        if (!visible && tasksColumn.Width.Value > 0) tasksWidth = tasksColumn.Width;
        tasksPanel.Visibility = tasksSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        tasksColumn.MinWidth = visible ? 200 : 0; tasksColumn.Width = visible ? tasksWidth : new(0);
        tasksSplitterColumn.Width = new(visible ? 4 : 0);
        TaskCount.ToolTip = visible ? "Click to hide tasks" : "Click to show tasks";
    }
    private void RefreshTasks()
    {
        try { tasksPanel.Refresh(); var count = tasksView.Refresh(); TaskCount.Text = count == 1 ? "1 task" : $"{count} tasks"; main.UpdateOpenTaskCount(Project, count); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { main.Log($"[{Project.Entry.Name}] Cannot read open tasks: {ex.Message}"); }
    }
    public void ShowExplorer() => navigation.SelectedIndex = 1;
    internal void ShowGit() => navigation.SelectedIndex = GitTab;
    internal void ShowCommit() { ShowGit(); gitView.FocusMessage(); }
    public void ShowSearch(bool filenames = false) { navigation.SelectedIndex = 2; search.FocusSearch(filenames); }
    internal async Task ShowFileFinderAsync()
    {
        var dialog = new FileFinderDialog(Window.GetWindow(this)!, Project.Entry.Path);
        if (dialog.ShowDialog() == true && dialog.SelectedPath is { } relative) await OpenFileAsync(Path.GetFullPath(relative, Project.Entry.Path));
    }
    public async Task OpenSearchHitAsync(ProjectSearchHit hit)
    {
        await OpenFileAsync(hit.FilePath);
        var tab = Editors.FirstOrDefault(e => e.FilePath.Equals(hit.FilePath, StringComparison.OrdinalIgnoreCase));
        if (tab is null) return;
        tab.ShowMarkdownPreview(false);
        if (hit.Line > 0 && hit.Line <= tab.Editor.Document.LineCount)
        {
            var line = tab.Editor.Document.GetLineByNumber(hit.Line);
            var column = Math.Clamp(hit.Column - 1, 0, line.Length);
            tab.Editor.Select(line.Offset + column, Math.Min(hit.Length, line.Length - column));
            tab.Editor.ScrollToLine(hit.Line);
        }
        tab.Editor.Focus();
    }
    public async Task OpenFileAsync(string path)
    {
        try
        {
            if (ImageTab.IsImage(path))
            {
                var image = tabs.Items.OfType<ImageTab>().FirstOrDefault(t => t.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (image is null)
                {
                    image = new ImageTab(path, t => { tabs.Items.Remove(t); placeholder.Visibility = tabs.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed; });
                    tabs.Items.Add(image); placeholder.Visibility = Visibility.Collapsed;
                }
                tabs.SelectedItem = image; tree.Reveal(path); return;
            }
            var existing = Editors.FirstOrDefault(e => e.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) { tabs.SelectedItem = existing; tree.Reveal(path); return; }
            var file = await TextFile.OpenAsync(path);
            existing = Editors.FirstOrDefault(e => e.FilePath.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) { tabs.SelectedItem = existing; return; }
            if (disposed) return;
            var tab = new EditorTab(file, async t => { if (await t.CanCloseAsync()) { t.Dispose(); language.Detach(t); tabs.Items.Remove(t); placeholder.Visibility = tabs.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed; main.OnEditorChanged(this); } }, () => main.OnEditorChanged(this), Project.Entry.Path, OpenFileAsync);
            tabs.Items.Add(tab); tabs.SelectedItem = tab; placeholder.Visibility = Visibility.Collapsed;
            tree.Reveal(path);
            await language.AttachAsync(tab);
        }
        catch (Exception ex) { Ui.Error(ex.Message, Window.GetWindow(this)); }
    }
    public async Task<bool> SaveAllAsync() { foreach (var tab in Editors.ToArray()) if (!await tab.SaveAsync()) return false; return true; }
    public async Task<bool> CanCloseAsync() { foreach (var tab in Editors.ToArray()) if (!await tab.CanCloseAsync()) return false; return true; }
    public Task OpenLocationAsync(BuildLocation location) => OpenSearchHitAsync(new(location.FilePath, Path.GetRelativePath(Project.Entry.Path, location.FilePath), location.Line, location.Column, 0, ""));
    private async Task RefreshFilesAsync(IReadOnlyDictionary<string, int> changes)
    {
        if (disposed) return;
        RefreshTree();
        if (changes.Keys.Any(path => Path.GetFileName(path).Equals("launch.json", StringComparison.OrdinalIgnoreCase))) LoadLaunches();
        IOException? retry = null;
        foreach (var editor in Editors.ToArray())
        {
            if (disposed) return;
            try { await editor.RefreshFromDiskAsync(); refreshWarnings.Remove(editor.FilePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (ex is IOException io && (io.HResult & 0xffff) is 32 or 33) retry = io;
                if (!refreshWarnings.TryGetValue(editor.FilePath, out var previous) || previous != ex.Message)
                {
                    refreshWarnings[editor.FilePath] = ex.Message;
                    main.Log($"[{Project.Entry.Name}] Cannot refresh {editor.FilePath}: {ex.Message}");
                }
            }
        }
        if (navigation.SelectedIndex == GitTab) await gitView.RefreshAsync();
        await language.FilesChangedAsync(changes);
        main.ScheduleGitRefresh();
        if (retry is not null) throw retry;
    }
    internal async Task ShowDiffAsync(GitFileChange file, bool staged)
    {
        var key = Path.GetFullPath(file.Path, Project.Entry.Path) + (staged ? " [staged]" : " [working]");
        var text = await main.Git.DiffAsync(Project.Entry.Path, file, staged);
        if (disposed) return;
        var existing = tabs.Items.OfType<GitDiffTab>().FirstOrDefault(t => t.Key == key);
        if (existing is not null) { existing.Editor.Text = text; tabs.SelectedItem = existing; return; }
        var diff = new GitDiffTab(key, Path.GetFileName(file.Path), staged ? "staged" : null, text, file, staged, t => { tabs.Items.Remove(t); placeholder.Visibility = tabs.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed; });
        tabs.Items.Add(diff); tabs.SelectedItem = diff; placeholder.Visibility = Visibility.Collapsed;
    }
    internal async Task ShowCommitDiffAsync(GitCommit commit, GitCommitFile file, CancellationToken cancellation = default)
    {
        var key = $"{commit.Hash}:{file.Path} [commit]";
        var existing = tabs.Items.OfType<GitDiffTab>().FirstOrDefault(t => t.Key == key);
        if (existing is not null) { tabs.SelectedItem = existing; return; }
        var text = await main.Git.CommitDiffAsync(Project.Entry.Path, commit.Hash, file, cancellation);
        if (disposed) return;
        existing = tabs.Items.OfType<GitDiffTab>().FirstOrDefault(t => t.Key == key);
        if (existing is not null) { tabs.SelectedItem = existing; return; }
        var diff = new GitDiffTab(key, Path.GetFileName(file.Path), commit.ShortHash, text, null, false, t => { tabs.Items.Remove(t); placeholder.Visibility = tabs.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed; });
        tabs.Items.Add(diff); tabs.SelectedItem = diff; placeholder.Visibility = Visibility.Collapsed;
    }
    internal async Task RefreshSelectedDiffAsync(IReadOnlyList<GitFileChange> files)
    {
        if (disposed || tabs.SelectedItem is not GitDiffTab { Change: { } change } diff) return;
        var current = files.FirstOrDefault(f => f.Path.Equals(change.Path, StringComparison.OrdinalIgnoreCase));
        var text = current is null || (diff.Staged ? !current.Staged : !current.Unstaged)
            ? diff.Staged ? "No staged changes for this file." : "No unstaged changes for this file."
            : await main.Git.DiffAsync(Project.Entry.Path, current, diff.Staged);
        if (!disposed && tabs.SelectedItem == diff && diff.Editor.Text != text) diff.Editor.Text = text;
    }
    public void Dispose() { if (disposed) return; disposed = true; tasksPanel.Dispose(); monitor?.Dispose(); foreach (var editor in Editors) editor.Dispose(); language.Dispose(); gitView.Dispose(); }
}
