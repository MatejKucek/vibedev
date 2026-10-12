using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vibedev.Core;
using Vibedev.Services;

namespace Vibedev.Views;

public sealed class ProjectView : UserControl, IDisposable
{
    private readonly MainWindow main;
    private readonly ProjectFileTree tree;
    private readonly TabControl navigation = new();
    private readonly ProjectSearchView search;
    private readonly ProjectGitView gitView;
    private readonly TabControl tabs = new();
    private readonly CSharpLanguageService language;
    private readonly ProjectFileMonitor? monitor;
    private bool disposed;
    private readonly Dictionary<string, string> refreshWarnings = new(StringComparer.OrdinalIgnoreCase);
    private readonly ComboBox launches = new() { MinWidth = 160, MaxWidth = 260, Margin = new(0, 0, 6, 0), DisplayMemberPath = "Name", ToolTip = ".vscode/launch.json" };
    private readonly Button run;
    private readonly TextBlock placeholder = Ui.Text("Open a file from the explorer\n\nCtrl+S  Save    ·    Ctrl+F  Find    ·    Ctrl+H  Replace", 13, "#777784");
    public ProjectState Project { get; }
    public IEnumerable<EditorTab> Editors => tabs.Items.OfType<EditorTab>();
    public Action<Orientation>? SplitRequested { get; set; }
    public Action? FloatRequested { get; set; }
    public Action? CloseRequested { get; set; }
    internal Action<Point>? DragRequested { get; set; }
    internal TextBlock ProjectTitle { get; }
    internal Button CloseProjectButton { get; }
    public LaunchConfiguration? SelectedLaunch => launches.SelectedItem as LaunchConfiguration;
    internal ProjectFileTree FileTree => tree;
    internal ProjectSearchView Search => search;
    internal ProjectGitView GitView => gitView;
    internal EditorLanguageFeatures? LanguageFeatures(EditorTab editor) => language.Features(editor);

    public ProjectView(MainWindow main, ProjectState project)
    {
        this.main = main; Project = project;
        language = new(this, text => main.Log($"[{Project.Entry.Name}] {text}"));
        tree = new(project.Entry.Path);
        tree.FileActivated += async path => await OpenFileAsync(path);
        search = new(this);
        gitView = new(main, this);
        tabs.Style = (Style)Application.Current.FindResource(typeof(TabControl));
        var root = new Grid(); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        var header = new Grid(); header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star), MinWidth = 180, MaxWidth = 360 }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var identity = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 12, 0) };
        identity.ColumnDefinitions.Add(new() { Width = GridLength.Auto, MaxWidth = 160 }); identity.ColumnDefinitions.Add(new());
        ProjectTitle = Ui.Text(project.Entry.Name, 16);
        ProjectTitle.FontWeight = FontWeights.SemiBold; ProjectTitle.TextWrapping = TextWrapping.NoWrap; ProjectTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        ProjectTitle.Margin = new(0, 0, 10, 0); ProjectTitle.VerticalAlignment = VerticalAlignment.Center; ProjectTitle.ToolTip = project.Entry.Path + "\nDrag the name to move or dock this panel\nRight-click for Git, float/dock, close and panel layout actions";
        ProjectDragHandle.Attach(ProjectTitle, point => DragRequested?.Invoke(point));
        identity.Children.Add(ProjectTitle);
        var projectPath = Ui.Text(project.Entry.Path, 11, "#B0A5C3"); projectPath.TextWrapping = TextWrapping.NoWrap; projectPath.TextTrimming = TextTrimming.CharacterEllipsis; projectPath.VerticalAlignment = VerticalAlignment.Center; projectPath.ToolTip = project.Entry.Path; Grid.SetColumn(projectPath, 1); identity.Children.Add(projectPath);
        header.Children.Add(identity);
        CloseProjectButton = Ui.Button("×", () => CloseRequested?.Invoke()); CloseProjectButton.Padding = new(8, 4, 8, 4); CloseProjectButton.Margin = new(0); CloseProjectButton.VerticalAlignment = VerticalAlignment.Center; CloseProjectButton.ToolTip = "Close project panel"; Grid.SetColumn(CloseProjectButton, 2); header.Children.Add(CloseProjectButton);
        var headerScroll = new ScrollViewer { Content = header, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        root.Children.Add(new Border { Background = Ui.Brush("#30283F"), BorderBrush = Ui.Brush("#6E568F"), BorderThickness = new(0, 0, 0, 1), CornerRadius = new(5, 5, 0, 0), Padding = new(14, 6, 10, 6), Child = headerScroll });
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 6, 0) }; Grid.SetColumn(tools, 1);
        tools.Children.Add(launches); run = Ui.Button("▶ Run", () => main.RunProject(this), true); tools.Children.Add(run);
        var reloadLaunches = Ui.Button("↻", LoadLaunches); reloadLaunches.ToolTip = "Reload .vscode/launch.json";
        tools.Children.Add(reloadLaunches);
        tools.Children.Add(Ui.Button("Split →", () => SplitRequested?.Invoke(Orientation.Horizontal))); tools.Children.Add(Ui.Button("Split ↓", () => SplitRequested?.Invoke(Orientation.Vertical))); tools.Children.Add(Ui.Button("↗ Float", () => FloatRequested?.Invoke()));
        header.Children.Add(tools);
        var csharp = Ui.Button("C# server", async () => { try { await language.EnableAsync(); } catch (Exception ex) { main.Log("C# server: " + ex.Message); } });
        csharp.ToolTip = "Install the C# language server, or restart it after a failure"; tools.Children.Add(csharp);
        var work = new Grid(); work.ColumnDefinitions.Add(new() { Width = new(240), MinWidth = 130 }); work.ColumnDefinitions.Add(new() { Width = new(4) }); work.ColumnDefinitions.Add(new() { MinWidth = 120 });
        var explorer = new DockPanel { Background = Ui.Brush("#1D1D21") };
        var fileHeader = new DockPanel { Margin = new(10, 8, 4, 8) }; var refresh = Ui.Button("↻", RefreshTree); refresh.Padding = new(6, 2, 6, 2); refresh.ToolTip = "Refresh files, preserving expanded folders"; DockPanel.SetDock(refresh, Dock.Right); fileHeader.Children.Add(refresh); fileHeader.Children.Add(Ui.Text("FILES", 10, "#9898A6")); DockPanel.SetDock(fileHeader, Dock.Top); explorer.Children.Add(fileHeader); explorer.Children.Add(tree);
        navigation.Style = (Style)Application.Current.FindResource(typeof(TabControl));
        navigation.Items.Add(new TabItem { Header = "EXPLORER", Content = explorer, ToolTip = "Browse project files", Style = (Style)Application.Current.FindResource(typeof(TabItem)), Padding = new Thickness(10, 8, 10, 8) });
        navigation.Items.Add(new TabItem { Header = "SEARCH", Content = search, ToolTip = "Search files and contents (Ctrl+Shift+F), or filenames (Ctrl+P)", Style = (Style)Application.Current.FindResource(typeof(TabItem)), Padding = new Thickness(10, 8, 10, 8) });
        navigation.Items.Add(new TabItem { Header = "GIT", Content = gitView, ToolTip = "Review diffs, stage files and commit changes", Style = (Style)Application.Current.FindResource(typeof(TabItem)), Padding = new Thickness(10, 8, 10, 8) });
        navigation.SelectionChanged += async (_, e) => { if (ReferenceEquals(e.Source, navigation) && navigation.SelectedIndex == 2) await gitView.RefreshAsync(); };
        navigation.SelectedIndex = 0; work.Children.Add(navigation);
        var splitter = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch }; Grid.SetColumn(splitter, 1); work.Children.Add(splitter);
        var editing = new Grid(); editing.Children.Add(tabs); placeholder.TextAlignment = TextAlignment.Center; placeholder.HorizontalAlignment = HorizontalAlignment.Center; placeholder.VerticalAlignment = VerticalAlignment.Center; editing.Children.Add(placeholder); Grid.SetColumn(editing, 2); work.Children.Add(editing);
        Grid.SetRow(work, 1); root.Children.Add(work);
        Content = new Border { BorderBrush = Ui.Brush("#6E568F"), BorderThickness = new(1), CornerRadius = new(6), Margin = new(4), Background = Ui.Brush("#18181B"), Child = root };
        launches.SelectionChanged += (_, _) => RefreshState();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { e.Handled = true; ShowSearch(); }
            if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; ShowSearch(true); }
        };
        LoadLaunches(); RefreshTree(); RefreshState();
        try { monitor = new(Project.Entry.Path, Dispatcher, RefreshFilesAsync, text => main.Log($"[{Project.Entry.Name}] {text}")); }
        catch (Exception ex) { main.Log($"[{Project.Entry.Name}] Live file updates unavailable: {ex.Message}"); }
    }
    public void LoadLaunches()
    {
        try { var name = SelectedLaunch?.Name; launches.ItemsSource = LaunchService.Read(Project.Entry.Path); launches.SelectedItem = launches.Items.OfType<LaunchConfiguration>().FirstOrDefault(c => c.Name == name) ?? launches.Items.OfType<LaunchConfiguration>().FirstOrDefault(); }
        catch (Exception ex) { launches.ItemsSource = null; main.Log($"[{Project.Entry.Name}] launch.json: {ex.Message}"); }
        launches.ToolTip = launches.Items.Count == 0 ? "No configurations. Add .vscode/launch.json, then refresh." : ".vscode/launch.json"; RefreshState();
    }
    public void RefreshState()
    {
        run.IsEnabled = launches.Items.Count > 0 && main.RunningProject is null;
        gitView.RefreshStatus();
        if (navigation.SelectedIndex == 2) _ = gitView.RefreshAsync();
    }
    public void RefreshTree() => tree.Refresh();
    public void ShowExplorer() => navigation.SelectedIndex = 0;
    internal void ShowGit() => navigation.SelectedIndex = 2;
    public void ShowSearch(bool filenames = false) { navigation.SelectedIndex = 1; search.FocusSearch(filenames); }
    public async Task OpenSearchHitAsync(ProjectSearchHit hit)
    {
        await OpenFileAsync(hit.FilePath);
        var tab = Editors.FirstOrDefault(e => e.FilePath.Equals(hit.FilePath, StringComparison.OrdinalIgnoreCase));
        if (tab is null) return;
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
            var existing = Editors.FirstOrDefault(e => e.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) { tabs.SelectedItem = existing; tree.Reveal(path); return; }
            var file = await TextFile.OpenAsync(path);
            existing = Editors.FirstOrDefault(e => e.FilePath.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) { tabs.SelectedItem = existing; return; }
            if (disposed) return;
            var tab = new EditorTab(file, async t => { if (await t.CanCloseAsync()) { t.Dispose(); language.Detach(t); tabs.Items.Remove(t); placeholder.Visibility = tabs.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed; main.OnEditorChanged(this); } }, () => main.OnEditorChanged(this));
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
        RefreshTree(); LoadLaunches();
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
        if (navigation.SelectedIndex == 2) await gitView.RefreshAsync();
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
        var diff = new GitDiffTab(key, Path.GetFileName(file.Path) + (staged ? " · staged diff" : " · diff"), text, file, staged, t => { tabs.Items.Remove(t); placeholder.Visibility = tabs.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed; });
        tabs.Items.Add(diff); tabs.SelectedItem = diff; placeholder.Visibility = Visibility.Collapsed;
    }
    internal async Task RefreshSelectedDiffAsync(IReadOnlyList<GitFileChange> files)
    {
        if (disposed || tabs.SelectedItem is not GitDiffTab diff) return;
        var current = files.FirstOrDefault(f => f.Path.Equals(diff.Change.Path, StringComparison.OrdinalIgnoreCase));
        var text = current is null || (diff.Staged ? !current.Staged : !current.Unstaged)
            ? diff.Staged ? "No staged changes for this file." : "No unstaged changes for this file."
            : await main.Git.DiffAsync(Project.Entry.Path, current, diff.Staged);
        if (!disposed && tabs.SelectedItem == diff && diff.Editor.Text != text) diff.Editor.Text = text;
    }
    public void Dispose() { if (disposed) return; disposed = true; monitor?.Dispose(); foreach (var editor in Editors) editor.Dispose(); language.Dispose(); gitView.Dispose(); }
}
