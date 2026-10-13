using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Layout;
using AvalonDock.Serializer.Xml;
using AvalonDock.Themes;
using Microsoft.Win32;
using Vibedev.Core;
using Vibedev.Services;
using Vibedev.Views;

namespace Vibedev;

public partial class MainWindow : Window
{
    private readonly WorkspaceStore store;
    private readonly WorkspaceState state;
    private readonly GitService git = new();
    private readonly DashboardView dashboard;
    private readonly TerminalControl terminal;
    private readonly Dictionary<string, ProjectView> views = [];
    private readonly HashSet<LayoutDocument> allowedClose = [];
    private readonly HashSet<LayoutDocument> replacing = [];
    private readonly List<(int Offset, string Directory)> outputContexts = [];
    private readonly DispatcherTimer refreshTimer;
    private readonly DispatcherTimer fileStatusTimer;
    private readonly DispatcherTimer claudeTimer;
    private readonly ClaudeSessionReader claudeSessions;
    private bool readingClaude;
    private bool closing, checkingClose, listing, refreshing, pulling, loaded;
    private ProjectFocusMode? focusMode;
    private LayoutDocument? focusedDocument;
    private (ILayoutElement Element, ILayoutContainer Parent)[] focusLayout = [];
    private Window? focusedFloatingWindow;
    private WindowState previousFloatingState;
    private CancellationTokenSource operations = new();
    public List<ProjectState> Projects { get; } = [];
    public IReadOnlyList<ProjectState> OpenProjects => Documents().Select(d => d.Content).OfType<ProjectView>().Select(v => v.Project).Distinct().ToArray();
    public ProjectState? RunningProject { get; private set; }
    public bool ProjectSidebarVisible => ProjectsSidebar.Visibility == Visibility.Visible;
    public bool TerminalVisible => TerminalPanel.Visibility == Visibility.Visible;
    public bool DashboardVisible => DashboardHost.Visibility == Visibility.Visible;
    public bool FocusModeActive => focusedDocument is not null;
    internal bool IsPulling => pulling;
    internal GitService Git => git;
    internal DashboardView Dashboard => dashboard;

    public MainWindow() : this(null) { }
    public MainWindow(string? stateDirectory, ClaudeSessionReader? claudeSessions = null)
    {
        this.claudeSessions = claudeSessions ?? new();
        InitializeComponent();
        store = new(stateDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vibedev"));
        try { state = store.Load(); }
        catch (Exception ex) { state = new(); Log("Could not read saved workspace: " + ex.Message); }
        Width = Math.Clamp(state.Width, 1000, SystemParameters.VirtualScreenWidth);
        Height = Math.Clamp(state.Height, 640, SystemParameters.VirtualScreenHeight);
        SetProjectSidebarVisible(state.ProjectSidebarVisible);
        SetTerminalVisible(state.TerminalVisible);
        foreach (var entry in state.Projects.DistinctBy(p => p.Id)) Projects.Add(new(entry));
        dashboard = new(this); DashboardHost.Child = dashboard; DashboardHost.ContextMenu = dashboard.ContextMenu; terminal = new(); TerminalHost.Children.Add(terminal);
        terminal.Log += Log;
        terminal.TaskOutput += (line, directory) => LogAt(line, directory);
        terminal.LocationActivated += async location => await OpenLocationAsync(location);
        OperationOutput.PreviewMouseLeftButtonDown += async (_, e) =>
        {
            if (e.ClickCount != 2 && Keyboard.Modifiers != ModifierKeys.Control) return;
            var index = OperationOutput.GetCharacterIndexFromPoint(e.GetPosition(OperationOutput), true);
            if (index < 0) return;
            var line = OperationOutput.GetLineIndexFromCharacterIndex(index);
            var directory = outputContexts.LastOrDefault(c => c.Offset <= index).Directory ?? Environment.CurrentDirectory;
            if (BuildLocation.Parse(OperationOutput.GetLineText(line), directory) is not { } location) return;
            e.Handled = true; await OpenLocationAsync(location);
        };
        OperationOutput.ToolTip = "Ctrl+click or double-click a build error to open its file and line";
        terminal.StateChanged += text => { TerminalLabel.Text = text; StopButton.IsEnabled = terminal.IsRunning; Redraw(); };
        Dock.Theme = new Vs2013DarkTheme();
        var tabMenu = new ContextMenu { Style = (Style)FindResource("ProjectContextMenu") };
        tabMenu.Opened += (_, _) =>
        {
            tabMenu.Items.Clear();
            if (tabMenu.DataContext is not LayoutItem { LayoutElement: LayoutDocument document }) return;
            PopulateProjectMenu(tabMenu, document);
        };
        Dock.DocumentContextMenu = tabMenu;
        Dock.LayoutChanged += (_, _) => { ExitFocusMode(); ConfigureProjectPanes(); };
        InitializeLayout();
        Dock.DocumentClosing += async (_, e) =>
        {
            if (focusedDocument == e.Document) ExitFocusMode();
            if (allowedClose.Remove(e.Document) || e.Document.Content is not ProjectView view) return;
            e.Cancel = true;
            if (await view.CanCloseAsync()) { allowedClose.Add(e.Document); e.Document.Close(); }
        };
        Dock.DocumentClosed += (_, e) =>
        {
            if (e.Document.ContentId is { } id && views.Remove(id, out var view)) view.Dispose();
            if (!closing) dashboard.Refresh();
        };
        Dock.ActiveContentChanged += (_, _) =>
        {
            if (focusedDocument is not null && !ReferenceEquals(Dock.ActiveContent, focusedDocument.Content)) ExitFocusMode();
            UpdateActiveProjectBorders();
            if (Dock.ActiveContent is ProjectView view) terminal.SetDirectory(view.Project.Entry.Path);
            UpdateDashboardButton();
        };
        SourceInitialized += (_, _) => { var flag = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref flag, sizeof(int)); };
        Loaded += async (_, _) =>
        {
            if (loaded) return; loaded = true;
            RestoreLayout(); Redraw();
            foreach (var (id, view) in views.ToArray())
                if (state.OpenFiles.TryGetValue(id, out var paths))
                    foreach (var path in paths.Where(File.Exists)) await view.OpenFileAsync(path);
            await RefreshStatusesAsync();
            await RefreshClaudeSessionsAsync();
        };
        Closing += Window_Closing;
        PreviewKeyDown += (_, e) =>
        {
            HandleFocusKey(e);
            if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; ToggleProjectSidebar(); }
            if (e.Key == Key.Oem3 && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; ToggleTerminal(); }
        };
        refreshTimer = new(TimeSpan.FromSeconds(15), DispatcherPriority.Background, async (_, _) => await RefreshStatusesAsync(), Dispatcher);
        fileStatusTimer = new(TimeSpan.FromMilliseconds(750), DispatcherPriority.Background, async (_, _) => { fileStatusTimer!.Stop(); await RefreshStatusesAsync(); }, Dispatcher);
        fileStatusTimer.Stop();
        claudeTimer = new(TimeSpan.FromSeconds(2), DispatcherPriority.Background, async (_, _) => await RefreshClaudeSessionsAsync(), Dispatcher);
    }
    internal async Task RefreshClaudeSessionsAsync()
    {
        if (readingClaude || closing || !loaded) return;
        readingClaude = true;
        var projects = Projects.ToArray();
        try
        {
            var updates = await Task.Run(() =>
            {
                var snapshot = claudeSessions.Read();
                return projects.Select(project => (Project: project,
                    Sessions: ClaudeProjectMatcher.Match(project.Entry.Path, snapshot.Sessions), snapshot.Warning)).ToArray();
            });
            if (closing) return;
            var changed = false;
            foreach (var update in updates)
            {
                if (!Projects.Contains(update.Project)) continue;
                var activity = update.Project.Claude;
                changed |= !activity.Sessions.SequenceEqual(update.Sessions) || activity.Warning != update.Warning;
                activity.Update(update.Sessions, update.Warning);
            }
            if (changed) dashboard.Refresh();
        }
        catch (Exception ex)
        {
            if (!closing)
            {
                foreach (var project in projects) project.Claude.Update(project.Claude.Sessions, "Cannot refresh Claude sessions: " + ex.Message);
                dashboard.Refresh();
            }
        }
        finally { readingClaude = false; }
    }
    internal void UpdateOpenTaskCount(ProjectState project, int count)
    {
        if (project.OpenTaskCount == count) return;
        project.OpenTaskCount = count;
        if (!closing) dashboard.Refresh();
    }
    private void ConfigureProjectPanes()
    {
        Dock.Layout.Updated -= ProjectLayoutUpdated;
        Dock.Layout.Updated += ProjectLayoutUpdated;
        ProjectLayoutUpdated(null, EventArgs.Empty);
    }
    private void ProjectLayoutUpdated(object? sender, EventArgs e)
    {
        if (FocusModeActive && !focusLayout.SequenceEqual(Dock.Layout.Descendents().Select(element => (element, element.Parent)))) ExitFocusMode();
        foreach (var pane in Dock.Layout.Descendents().OfType<LayoutDocumentPane>()) pane.ShowHeader = false;
        UpdateActiveProjectBorders();
        UpdateDashboardButton();
        if (!closing && !dashboard.DisplayedProjects.SequenceEqual(OpenProjects)) dashboard.Refresh();
    }
    private void InitializeLayout()
    {
        var pane = new LayoutDocumentPane();
        Dock.Layout = new LayoutRoot { RootPanel = new LayoutPanel(new LayoutDocumentPaneGroup(pane)) };
        SetDashboardVisible(state.DashboardVisible);
    }
    internal void RestoreLayout()
    {
        if (!File.Exists(store.LayoutPath)) return;
        try
        {
            var serializer = new XmlLayoutSerializer(Dock);
            serializer.LayoutSerializationCallback += (_, e) =>
            {
                var id = e.Model.ContentId;
                if (id == "dashboard") { e.Cancel = true; return; }
                if (id is null || !state.PanelProjects.TryGetValue(id, out var path) || Projects.FirstOrDefault(p => p.Entry.Id == new ProjectEntry(path).Id) is not { } project) { e.Cancel = true; return; }
                e.Content = CreateView(project, id);
            };
            serializer.Deserialize(store.LayoutPath);
            Dock.Layout.CollectGarbage();
            foreach (var pane in Dock.Layout.Descendents().OfType<LayoutDocumentPane>())
                if (!pane.Children.Any(d => d.IsSelected) && pane.Children.FirstOrDefault() is { } first) first.IsSelected = true;
            foreach (var doc in Documents()) WireView(doc);
            SetDashboardVisible(state.DashboardVisible);
        }
        catch (Exception ex) { foreach (var view in views.Values) view.Dispose(); views.Clear(); InitializeLayout(); Log("Saved layout could not be restored: " + ex.Message); }
    }
    internal IEnumerable<LayoutDocument> Documents() => Dock.Layout.Descendents().OfType<LayoutDocument>();
    private LayoutDocumentPane MainPane()
    {
        var pane = Dock.Layout.RootPanel.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault();
        if (pane is not null) return pane;
        pane = new(); Dock.Layout.RootPanel.Children.Add(new LayoutDocumentPaneGroup(pane)); return pane;
    }
    private ProjectView CreateView(ProjectState project, string id)
    {
        var view = new ProjectView(this, project); views[id] = view; return view;
    }
    private LayoutDocument NewProjectDocument(ProjectState project)
    {
        var id = "project-" + Guid.NewGuid().ToString("N");
        var doc = new LayoutDocument { ContentId = id, Title = project.Entry.Name, Content = CreateView(project, id), CanClose = true, CanFloat = true };
        WireView(doc); return doc;
    }
    private void WireView(LayoutDocument doc)
    {
        if (doc.Content is not ProjectView view) return;
        doc.IsActiveChanged -= ProjectActiveChanged;
        doc.IsActiveChanged += ProjectActiveChanged;
        view.SetActive(doc.IsActive);
        view.PreviewMouseDown += (_, e) =>
        {
            doc.IsSelected = true; doc.IsActive = true;
            if (!IsTextInput(e.OriginalSource as DependencyObject)) view.Focus();
        };
        view.GotKeyboardFocus += (_, _) => { doc.IsSelected = true; doc.IsActive = true; };
        view.SplitRequested = orientation => Split(doc, orientation);
        view.FloatRequested = () => { ExitFocusMode(); doc.Float(); };
        view.CloseRequested = () => doc.Close();
        view.DragRequested = point => DragProjectDocument(doc, point);
        var menu = CreateProjectContextMenu(() => doc);
        view.ContextMenu = menu;
        view.ProjectTitle.ContextMenu = menu;
        doc.ToolTip = view.Project.Entry.Path + "\nRight-click to reorder, replace or swap project panels";
    }
    private void ProjectActiveChanged(object? sender, EventArgs e) => UpdateActiveProjectBorders();
    private void UpdateActiveProjectBorders()
    {
        foreach (var document in Documents())
            if (document.Content is ProjectView view) view.SetActive(document.IsActive);
    }
    internal void HandleFocusKey(KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.F || Keyboard.Modifiers != ModifierKeys.None || IsTextInput(e.OriginalSource as DependencyObject)) return;
        if (e.IsRepeat) { e.Handled = true; return; }
        if (FocusModeActive || Documents().Any(d => d.IsActive && d.Content is ProjectView))
        {
            e.Handled = true;
            ToggleFocusMode();
        }
    }
    private static bool IsTextInput(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is TextBoxBase or PasswordBox or ICSharpCode.AvalonEdit.TextEditor or TerminalControl || element is ComboBox { IsEditable: true }) return true;
            element = element is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }
    public void ToggleFocusMode()
    {
        if (FocusModeActive) { ExitFocusMode(); return; }
        var document = Documents().FirstOrDefault(d => d.IsActive && d.Content is ProjectView);
        if (document?.Content is not ProjectView view) return;
        view.UpdateLayout();
        focusedDocument = document;
        focusLayout = Dock.Layout.Descendents().Select(element => (element, element.Parent)).ToArray();
        focusMode = new(view);
        if (document.IsFloating && Application.Current.Windows.OfType<LayoutFloatingWindowControl>()
            .FirstOrDefault(window => window.Model.Descendents().Contains(document)) is { } floating)
        {
            focusedFloatingWindow = floating; previousFloatingState = floating.WindowState;
            SetFloatingFocusState(floating, WindowState.Maximized);
        }
    }
    private void ExitFocusMode()
    {
        focusedDocument = null;
        focusMode?.Dispose(); focusMode = null; focusLayout = [];
        if (focusedFloatingWindow is { } floating)
        {
            focusedFloatingWindow = null;
            SetFloatingFocusState(floating, previousFloatingState);
        }
    }
    private static void SetFloatingFocusState(Window window, WindowState state)
    {
        // AvalonDock tracks native maximize/restore commands and rejects direct WindowState restores.
        SendMessage(new WindowInteropHelper(window).Handle, 0x0112 /* WM_SYSCOMMAND */,
            new IntPtr(state == WindowState.Maximized ? 0xF030 /* SC_MAXIMIZE */ : 0xF120 /* SC_RESTORE */), IntPtr.Zero);
    }
    internal ContextMenu CreateProjectContextMenu(ProjectState project) => CreateProjectContextMenu(() =>
        Documents().FirstOrDefault(d => d.Content is ProjectView v && v.Project == project && d.IsActive) ??
        Documents().FirstOrDefault(d => d.Content is ProjectView v && v.Project == project));

    private ContextMenu CreateProjectContextMenu(Func<LayoutDocument?> target)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("ProjectContextMenu") };
        menu.Opened += (_, _) =>
        {
            menu.Items.Clear();
            if (target() is { } document) PopulateProjectMenu(menu, document);
        };
        return menu;
    }
    internal void PopulateProjectMenu(ContextMenu menu, LayoutDocument document)
    {
        if (document.Content is ProjectView current)
        {
            var gitMenu = new MenuItem { Header = "Git", IsEnabled = current.Project.Git?.IsRepository == true && !current.Project.Busy };
            var review = new MenuItem { Header = "Review changes / commit staged" };
            review.Click += (_, _) => { document.IsActive = true; document.IsSelected = true; current.ShowGit(); };
            gitMenu.Items.Add(review);
            foreach (var (label, action) in new[] { ("Pull", "pull"), ("Push", "push"), ("Commit all changes…", "commit") })
            {
                var item = new MenuItem { Header = label };
                item.Click += (_, _) => GitAction(current.Project, action); gitMenu.Items.Add(item);
            }
            var refresh = new MenuItem { Header = "Refresh status" }; refresh.Click += (_, _) => RefreshStatuses();
            menu.Items.Add(gitMenu); menu.Items.Add(refresh); menu.Items.Add(new Separator());
            var swap = new MenuItem { Header = "Swap with project", ToolTip = "Exchange tab positions or split/floating panel positions" };
            var peers = Documents().Where(d => d != document && d.Content is ProjectView).ToArray();
            for (var i = 0; i < peers.Length; i++)
            {
                var target = peers[i]; var item = new MenuItem { Header = $"{i + 1}. {((ProjectView)target.Content).Project.Entry.Name}", ToolTip = ((ProjectView)target.Content).Project.Entry.Path };
                item.Click += (_, _) => SwapProjectDocuments(document, target); swap.Items.Add(item);
            }
            swap.IsEnabled = peers.Length > 0; menu.Items.Add(swap);
            var replace = new MenuItem { Header = "Replace project", ToolTip = "Open a different project in this panel, checking unsaved files first" };
            foreach (var project in Projects.Where(p => p != current.Project))
            {
                var item = new MenuItem { Header = project.Entry.Name, ToolTip = project.Entry.Path };
                item.Click += async (_, _) => await ReplaceProjectAsync(document, project); replace.Items.Add(item);
            }
            replace.IsEnabled = replace.Items.Count > 0 && !replacing.Contains(document); menu.Items.Add(replace);
            if (document.Parent is LayoutDocumentPane pane)
            {
                var left = new MenuItem { Header = "Move tab left", IsEnabled = pane.Children.IndexOf(document) > 0 };
                left.Click += (_, _) => { var index = pane.Children.IndexOf(document); if (index > 0) pane.MoveChild(index, index - 1); }; menu.Items.Add(left);
                var right = new MenuItem { Header = "Move tab right", IsEnabled = pane.Children.IndexOf(document) < pane.Children.Count - 1 };
                right.Click += (_, _) => { var index = pane.Children.IndexOf(document); if (index >= 0 && index < pane.Children.Count - 1) pane.MoveChild(index, index + 1); }; menu.Items.Add(right);
            }
            var isFloating = document.IsFloating;
            var floating = new MenuItem { Header = isFloating ? "Dock project" : "Float project", IsEnabled = isFloating ? document.CanMove : document.CanFloat && Dock.AllowFloatingWindows };
            floating.Click += (_, _) =>
            {
                ExitFocusMode();
                if (document.IsFloating) document.Dock(); else document.Float();
                document.IsActive = true; document.IsSelected = true; dashboard.Refresh(); SaveWorkspace();
            };
            menu.Items.Add(floating);
            menu.Items.Add(new Separator());
        }
        var close = new MenuItem { Header = "Close project", IsEnabled = document.CanClose }; close.Click += (_, _) => document.Close(); menu.Items.Add(close);
        foreach (var item in menu.Items.OfType<MenuItem>()) item.Style = (Style)FindResource("ProjectMenuItem");
    }
    internal async Task<bool> ReplaceProjectAsync(LayoutDocument document, ProjectState project)
    {
        if (closing || !Projects.Contains(project) || document.Content is not ProjectView previous || previous.Project == project || !replacing.Add(document)) return false;
        try
        {
            if (!await previous.CanCloseAsync() || closing || !ReferenceEquals(document.Content, previous) || !Documents().Contains(document)) return false;
            ExitFocusMode();
            var replacement = CreateView(project, document.ContentId);
            document.Content = replacement; document.Title = project.Entry.Name; WireView(document);
            previous.Dispose();
            document.IsSelected = true; document.IsActive = true; terminal.SetDirectory(project.Entry.Path);
            dashboard.Refresh(); SaveWorkspace(); return true;
        }
        finally { replacing.Remove(document); }
    }
    internal void SwapProjectDocuments(LayoutDocument first, LayoutDocument second)
    {
        if (first == second || first.Content is not ProjectView || second.Content is not ProjectView || first.Parent is not LayoutDocumentPane firstPane || second.Parent is not LayoutDocumentPane secondPane) return;
        ExitFocusMode();
        if (ReferenceEquals(firstPane, secondPane))
        {
            var a = firstPane.Children.IndexOf(first); var b = firstPane.Children.IndexOf(second);
            firstPane.MoveChild(a, b); firstPane.MoveChild(a < b ? b - 1 : b + 1, a);
        }
        else
        {
            var placeholder = new LayoutDocument();
            firstPane.ReplaceChild(first, placeholder); secondPane.ReplaceChild(second, first); firstPane.ReplaceChild(placeholder, second);
        }
        first.IsActive = true; dashboard.Refresh(); SaveWorkspace();
    }
    public void OpenProject(ProjectState project)
    {
        var existing = Documents().FirstOrDefault(d => d.Content is ProjectView v && v.Project == project);
        if (existing is null) { existing = NewProjectDocument(project); MainPane().Children.Add(existing); }
        existing.IsActive = true; existing.IsSelected = true; terminal.SetDirectory(project.Entry.Path);
        dashboard.Refresh();
    }
    internal LayoutDocument OpenProjectWindow(ProjectState project)
    {
        ExitFocusMode();
        var document = NewProjectDocument(project);
        document.FloatingWidth = Math.Clamp(Dock.ActualWidth, 640, 1000);
        document.FloatingHeight = Math.Clamp(Dock.ActualHeight, 400, 700);
        MainPane().Children.Add(document);
        document.IsSelected = true; document.IsActive = true;
        document.Float();
        dashboard.Refresh();
        return document;
    }
    private void DragProjectDocument(LayoutDocument document, Point screenPosition)
    {
        if (closing || !document.CanFloat || !document.CanMove || !Dock.AllowFloatingWindows || !Documents().Contains(document)) return;
        ExitFocusMode();
        document.IsSelected = true; document.IsActive = true;
        var floating = document.FindParent<LayoutFloatingWindow>();
        if (floating is null || floating.Descendents().OfType<LayoutDocument>().Count() > 1) document.Float();
        // Float queues its window creation. Start the native move after that window is ready;
        // AvalonDock's move loop then supplies its normal docking targets and drop handling.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (closing || !Documents().Contains(document)) return;
            var window = Application.Current.Windows.OfType<LayoutFloatingWindowControl>()
                .FirstOrDefault(w => w.Model.Descendents().Contains(document));
            if (window is null || !window.IsVisible) return;
            var transform = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice;
            var position = transform?.Transform(screenPosition) ?? screenPosition;
            window.WindowState = WindowState.Normal;
            window.Left = position.X - 24; window.Top = position.Y - 12;
            window.Activate();
            if (Mouse.LeftButton != MouseButtonState.Pressed) return;
            var coordinates = new IntPtr(unchecked((int)(((uint)(int)screenPosition.Y << 16) | ((uint)(int)screenPosition.X & 0xffff))));
            SendMessage(new WindowInteropHelper(window).Handle, 0x00A1 /* WM_NCLBUTTONDOWN */, new IntPtr(2) /* HTCAPTION */, coordinates);
            if (!closing) SaveWorkspace();
        });
    }
    private void Split(LayoutDocument source, Orientation orientation)
    {
        if (source.Parent is not LayoutDocumentPane pane) { Ui.Error("Dock this floating panel before splitting it.", this); return; }
        var project = ChooseProject(); if (project is null) return;
        SplitWithProject(source, orientation, project);
    }
    internal void SplitWithProject(LayoutDocument source, Orientation orientation, ProjectState project)
    {
        if (source.Parent is not LayoutDocumentPane pane || pane.Parent is not ILayoutContainer parent) return;
        ExitFocusMode();
        var group = new LayoutDocumentPaneGroup { Orientation = orientation, DockWidth = pane.DockWidth, DockHeight = pane.DockHeight };
        parent.ReplaceChild(pane, group); group.Children.Add(pane);
        var newPane = new LayoutDocumentPane(); var doc = NewProjectDocument(project); newPane.Children.Add(doc); group.Children.Add(newPane); doc.IsActive = true;
        dashboard.Refresh();
    }
    private ProjectState? ChooseProject()
    {
        var dialog = new Window { Title = "Choose project for new panel", Owner = this, Width = 480, Height = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new(20) };
        var text = Ui.Text("Choose a project to open beside this panel.", 14); text.Margin = new(0, 0, 0, 16); DockPanel.SetDock(text, System.Windows.Controls.Dock.Top); panel.Children.Add(text);
        var list = new ListBox(); foreach (var p in Projects) list.Items.Add(new ListBoxItem { Content = p.Entry.Name + "\n" + p.Entry.Path, Tag = p });
        var open = Ui.Button("Open panel", () => { if (list.SelectedItem is not null) dialog.DialogResult = true; }, true); open.Margin = new(0, 12, 0, 0); DockPanel.SetDock(open, System.Windows.Controls.Dock.Bottom); panel.Children.Add(open); panel.Children.Add(list);
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is not null) dialog.DialogResult = true; };
        dialog.Content = panel; return dialog.ShowDialog() == true ? (list.SelectedItem as ListBoxItem)?.Tag as ProjectState : null;
    }
    public void AddProject()
    {
        var picker = new OpenFolderDialog { Title = "Add existing coding project" };
        if (picker.ShowDialog(this) != true) return;
        AddPath(picker.FolderName); SaveWorkspace(); Redraw(); RefreshStatuses();
    }
    internal void AddPath(string path)
    {
        var entry = new ProjectEntry(Path.GetFullPath(path));
        if (Projects.All(p => p.Entry.Id != entry.Id)) Projects.Add(new(entry));
    }
    public async void Scan()
    {
        var picker = new OpenFolderDialog { Title = "Find Git repositories in this directory and all subdirectories" };
        if (state.ScanRoot is { } root && Directory.Exists(root)) picker.InitialDirectory = root;
        if (picker.ShowDialog(this) != true) return;
        state.ScanRoot = picker.FolderName; SetStatus("Scanning " + picker.FolderName + "…");
        try
        {
            var result = await Task.Run(() => RepositoryDiscovery.Scan(picker.FolderName, operations.Token));
            foreach (var path in result.Repositories) AddPath(path);
            foreach (var warning in result.Warnings) Log("[scan] " + warning);
            SetStatus($"Found {result.Repositories.Count} repositories" + (result.Warnings.Count > 0 ? $" · {result.Warnings.Count} unreadable directories" : ""));
            SaveWorkspace(); Redraw(); await RefreshStatusesAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Ui.Error(ex.Message, this); }
    }
    public async void RefreshStatuses() { await RefreshStatusesAsync(); await RefreshClaudeSessionsAsync(); }
    internal void ScheduleGitRefresh() { if (!closing) { fileStatusTimer.Stop(); fileStatusTimer.Start(); } }
    internal async Task<bool> SaveProjectFilesAsync(ProjectState project, IEnumerable<string> paths)
    {
        var selected = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var editor in views.Values.Where(v => v.Project == project).SelectMany(v => v.Editors).Where(e => selected.Contains(e.FilePath)).ToArray())
            if (!await editor.SaveAsync()) return false;
        return true;
    }
    internal async Task<bool> ChangeGitIndexAsync(ProjectState project, Func<Task<CommandResult>> action)
    {
        if (project.Busy || closing) return false;
        project.Busy = true; Redraw();
        try
        {
            var result = await action(); project.Failed = !result.Success;
            if (result.Output.Length > 0) Log($"[{project.Entry.Name}] {result.Output}");
            project.Activity = result.Success ? "" : "Git operation failed";
            project.Git = await git.StatusAsync(project.Entry.Path, operations.Token);
            return result.Success;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { project.Failed = true; project.Activity = ex.Message; Log($"[{project.Entry.Name}] {ex.Message}"); return false; }
        finally { project.Busy = false; Redraw(); foreach (var view in views.Values.Where(v => v.Project == project).ToArray()) _ = view.GitView.RefreshAsync(); }
    }
    private async Task RefreshStatusesAsync()
    {
        if (refreshing || closing) return; refreshing = true;
        try
        {
            foreach (var project in Projects.ToArray())
            {
                if (project.Busy) continue;
                try { project.Git = await git.StatusAsync(project.Entry.Path, operations.Token); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { project.Git = new(false, "Unavailable", 0, ex.Message); }
            }
            if (!closing) Redraw();
        }
        finally { refreshing = false; }
    }
    public async void GitAction(ProjectState project, string action)
    {
        if (project.Busy) return;
        string? message = null;
        if (action == "commit")
        {
            message = Ui.Prompt(this, "Commit all", $"Stage and commit all changed files in {project.Entry.Name}.\nIgnored files are excluded. Unsaved editor files will be saved first.");
            if (message is null) return;
            foreach (var view in views.Values.Where(v => v.Project == project)) if (!await view.SaveAllAsync()) return;
        }
        await GitActionAsync(project, action, message);
    }
    internal async Task<bool> GitActionAsync(ProjectState project, string action, string? message = null)
    {
        if (project.Busy || closing) return false;
        project.Busy = true; project.Activity = action + "…"; project.Failed = false; Redraw(); SetTerminalVisible(true); BottomTabs.SelectedIndex = 1;
        Log($"[{project.Entry.Name}] git {action}");
        try
        {
            if (action == "sync")
                foreach (var view in views.Values.Where(v => v.Project == project).ToArray())
                    if (!await view.SaveAllAsync()) { project.Activity = "sync cancelled · save cancelled"; Log($"[{project.Entry.Name}] {project.Activity}"); return false; }
            Action<string> output = line => Log($"[{project.Entry.Name}] {line}");
            var result = action switch
            {
                "pull" => await git.PullAsync(project.Entry.Path, output, operations.Token),
                "push" => await git.PushAsync(project.Entry.Path, output, operations.Token),
                "commit" => await git.CommitAllAsync(project.Entry.Path, message!, output, operations.Token),
                "sync" => await git.CommitAllAndSyncAsync(project.Entry.Path, message!, output, operations.Token),
                _ => throw new ArgumentException("Unknown Git action.")
            };
            project.Failed = !result.Success;
            project.Activity = result.Success ? $"{action} completed" : $"{action} failed · exit {result.ExitCode}";
            Log($"[{project.Entry.Name}] {project.Activity}");
            project.Git = await git.StatusAsync(project.Entry.Path, operations.Token);
            if (action is "pull" or "sync")
                foreach (var view in views.Values.Where(v => v.Project == project).ToArray())
                {
                    view.RefreshTree(); view.LoadLaunches();
                    foreach (var editor in view.Editors.ToArray())
                        try { await editor.RefreshFromDiskAsync(); }
                        catch (Exception ex) { Log($"[{project.Entry.Name}] Cannot refresh {editor.FilePath}: {ex.Message}"); }
                }
            return result.Success;
        }
        catch (OperationCanceledException) { project.Activity = action + " cancelled"; return false; }
        catch (Exception ex) { project.Failed = true; project.Activity = action + " failed"; Log($"[{project.Entry.Name}] {ex.Message}"); return false; }
        finally { project.Busy = false; SetStatus(project.Entry.Name + " · " + project.Activity); if (!closing) { Redraw(); foreach (var view in views.Values.Where(v => v.Project == project).ToArray()) _ = view.GitView.RefreshAsync(); } }
    }
    public async void PullAll(bool openOnly = false)
    {
        if (pulling) return; pulling = true; dashboard.Refresh();
        var success = 0; var failed = 0;
        var targets = openOnly ? OpenProjects.ToArray() : Projects.ToArray();
        try
        {
            foreach (var project in targets)
            {
                if (closing) break;
                if (await GitActionAsync(project, "pull")) success++; else failed++;
            }
            SetStatus($"Pull {(openOnly ? "open projects" : "all")} finished · {success} succeeded · {failed} failed or busy");
        }
        finally { pulling = false; dashboard.Refresh(); }
    }
    public async void RunProject(ProjectView view)
    {
        if (RunningProject is not null) { SetStatus("Stop the active project first."); return; }
        if (view.SelectedLaunch is not { } config) { SetStatus("Add a .NET launch configuration to .vscode/launch.json."); return; }
        RunningProject = view.Project; Redraw();
        try
        {
            if (!await view.SaveAllAsync()) return;
            var plan = LaunchService.Resolve(view.Project.Entry.Path, config);
            SetTerminalVisible(true); BottomTabs.SelectedIndex = 0;
            SetStatus($"Running {view.Project.Entry.Name} / {config.Name}");
            await terminal.RunAsync(view.Project.Entry.Name + " / " + config.Name, plan);
            SetStatus("Run finished · " + view.Project.Entry.Name);
        }
        catch (Exception ex) { Log("[run] " + ex.Message); Ui.Error(ex.Message, this); }
        finally { RunningProject = null; if (!closing) Redraw(); }
    }
    public void OnEditorChanged(ProjectView view)
    {
        var doc = Documents().FirstOrDefault(d => ReferenceEquals(d.Content, view));
        if (doc is not null) doc.Title = view.Project.Entry.Name + (view.Editors.Any(e => e.IsDirty) ? " ●" : "");
        view.ProjectTitle.Text = view.Project.Entry.Name + (view.Editors.Any(e => e.IsDirty) ? " ●" : "");
    }
    private void Redraw()
    {
        if (closing) return;
        listing = true;
        var selected = (ProjectList.SelectedItem as ListBoxItem)?.Tag as ProjectState;
        ProjectList.Items.Clear();
        foreach (var p in Projects.Where(p => p.Entry.Name.Contains(ProjectFilter.Text, StringComparison.OrdinalIgnoreCase) || p.Entry.Path.Contains(ProjectFilter.Text, StringComparison.OrdinalIgnoreCase)))
        {
            var row = new StackPanel(); row.Children.Add(Ui.Text("{ }  " + p.Entry.Name, 12));
            var branch = Ui.Text(p.Failed ? p.Activity : p.Git?.Branch ?? "Local project", 10, p.Failed ? "#F87171" : "#777784"); branch.TextTrimming = TextTrimming.CharacterEllipsis; branch.TextWrapping = TextWrapping.NoWrap; branch.Margin = new(21, 4, 0, 0); row.Children.Add(branch);
            var item = new ListBoxItem { Content = row, Tag = p, ToolTip = p.Entry.Path + "\nClick to open; drag out to create a new project window" }; ProjectList.Items.Add(item); if (p == selected) ProjectList.SelectedItem = item;
            ProjectDragHandle.Attach(item, point =>
            {
                if (!closing && Projects.Contains(p) && Dock.AllowFloatingWindows) DragProjectDocument(OpenProjectWindow(p), point);
            }, () => { if (ProjectList.SelectedItem == item) OpenProject(p); else ProjectList.SelectedItem = item; });
            var menu = new ContextMenu(); var remove = new MenuItem { Header = "Remove from workspace" }; remove.Click += async (_, _) => await RemoveProjectAsync(p); menu.Items.Add(remove); item.ContextMenu = menu;
        }
        listing = false; ProjectCount.Text = Projects.Count.ToString(); ScanRootLabel.Text = state.ScanRoot ?? "Find repositories in subfolders";
        dashboard.Refresh(); foreach (var view in views.Values.ToArray()) view.RefreshState();
    }
    private async Task RemoveProjectAsync(ProjectState project)
    {
        if (project.Busy || RunningProject == project) { SetStatus("Wait for this project's operation to finish before removing it."); return; }
        var docs = Documents().Where(d => d.Content is ProjectView v && v.Project == project).ToArray();
        foreach (var doc in docs) if (!await ((ProjectView)doc.Content).CanCloseAsync()) return;
        foreach (var doc in docs) { allowedClose.Add(doc); doc.Close(); }
        Projects.Remove(project); SaveWorkspace(); Redraw();
    }
    internal async Task OpenLocationAsync(BuildLocation location)
    {
        var project = Projects.Where(p => location.FilePath.StartsWith(Path.TrimEndingDirectorySeparator(p.Entry.Path) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).OrderByDescending(p => p.Entry.Path.Length).FirstOrDefault();
        if (project is null) { SetStatus("This file is outside the registered projects: " + location.FilePath); return; }
        var document = Documents().FirstOrDefault(d => d.IsActive && d.Content is ProjectView active && active.Project == project)
            ?? Documents().FirstOrDefault(d => d.Content is ProjectView existing && existing.Project == project);
        if (document is null) { OpenProject(project); document = Documents().First(d => d.Content is ProjectView v && v.Project == project); }
        document.IsActive = true; document.IsSelected = true;
        await ((ProjectView)document.Content).OpenLocationAsync(location);
    }
    public void Log(string line) => LogAt(line, terminal?.CurrentDirectory ?? Environment.CurrentDirectory);
    private void LogAt(string line, string directory)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => LogAt(line, directory)); return; }
        if (closing) return;
        outputContexts.Add((OperationOutput.Text.Length, directory));
        OperationOutput.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        if (OperationOutput.Text.Length > 400000)
        {
            var removed = OperationOutput.Text.Length - 300000;
            OperationOutput.Text = OperationOutput.Text[removed..];
            var retained = outputContexts.LastOrDefault(c => c.Offset <= removed).Directory ?? directory;
            var contexts = outputContexts.Where(c => c.Offset > removed).Select(c => (c.Offset - removed, c.Directory)).ToArray();
            outputContexts.Clear(); outputContexts.Add((0, retained)); outputContexts.AddRange(contexts);
        }
        OperationOutput.ScrollToEnd();
    }
    private void SetStatus(string text) { if (!closing) StatusLabel.Text = text; }
    internal void SaveWorkspace()
    {
        try
        {
            state.Projects = Projects.Select(p => p.Entry).ToList();
            state.Width = RestoreBounds.Width > 0 ? RestoreBounds.Width : Width; state.Height = RestoreBounds.Height > 0 ? RestoreBounds.Height : Height;
            state.PanelProjects = views.ToDictionary(v => v.Key, v => v.Value.Project.Entry.Path);
            state.OpenFiles = views.ToDictionary(v => v.Key, v => v.Value.Editors.Select(e => e.FilePath).ToList());
            state.ProjectSidebarVisible = ProjectSidebarVisible;
            state.DashboardVisible = DashboardVisible;
            state.TerminalVisible = TerminalVisible;
            if (TerminalVisible && TerminalRow.ActualHeight > 0) state.TerminalHeight = TerminalRow.ActualHeight;
            if (ProjectSidebarVisible && ProjectsColumn.ActualWidth > 0) state.ProjectSidebarWidth = ProjectsColumn.ActualWidth;
            store.Save(state);
        }
        catch (Exception ex) { Log("Workspace save failed: " + ex.Message); }
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closing) return; e.Cancel = true; if (checkingClose) return; checkingClose = true;
        try
        {
            foreach (var view in views.Values.ToArray()) if (!await view.CanCloseAsync()) return;
            if ((RunningProject is not null || Projects.Any(p => p.Busy)) && MessageBox.Show(this, "Stop the active run and Git operations, then close?", "Work is running", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            SaveWorkspace();
            ExitFocusMode();
            try
            {
                Directory.CreateDirectory(store.DirectoryPath);
                var temp = store.LayoutPath + ".tmp"; new XmlLayoutSerializer(Dock).Serialize(temp); File.Move(temp, store.LayoutPath, true);
            }
            catch (Exception ex) { Log("Layout save failed: " + ex.Message); }
            closing = true; refreshTimer.Stop(); fileStatusTimer.Stop(); claudeTimer.Stop(); operations.Cancel(); foreach (var view in views.Values) view.Dispose(); terminal.Dispose(); _ = Dispatcher.BeginInvoke(Close);
        }
        finally { checkingClose = false; }
    }
    public void SetProjectSidebarVisible(bool visible)
    {
        if (!visible && ProjectSidebarVisible && ProjectsColumn.ActualWidth > 0) state.ProjectSidebarWidth = ProjectsColumn.ActualWidth;
        ProjectsSidebar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ProjectsSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ProjectsColumn.MinWidth = visible ? 160 : 0;
        ProjectsColumn.Width = new GridLength(visible ? Math.Max(160, double.IsFinite(state.ProjectSidebarWidth) ? state.ProjectSidebarWidth : 224) : 0);
        ProjectsSplitterColumn.Width = new GridLength(visible ? 4 : 0);
        ProjectsToggleButton.Background = visible ? Views.Ui.Brush("#343044") : System.Windows.Media.Brushes.Transparent;
        state.ProjectSidebarVisible = visible;
    }
    public void ToggleProjectSidebar() { SetProjectSidebarVisible(!ProjectSidebarVisible); SaveWorkspace(); }
    private void ToggleProjects_Click(object sender, RoutedEventArgs e) => ToggleProjectSidebar();
    private void AddProject_Click(object sender, RoutedEventArgs e) => AddProject();
    private void Scan_Click(object sender, RoutedEventArgs e) => Scan();
    private void PullAll_Click(object sender, RoutedEventArgs e) => PullAll();
    public void SetDashboardVisible(bool visible)
    {
        DashboardHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible) dashboard.Refresh();
        state.DashboardVisible = visible;
        UpdateDashboardButton();
    }
    private void UpdateDashboardButton() => DashboardToggleButton.Background = DashboardVisible ? Ui.Brush("#343044") : System.Windows.Media.Brushes.Transparent;
    public void ToggleDashboard() { SetDashboardVisible(!DashboardVisible); SaveWorkspace(); }
    private void Dashboard_Click(object sender, RoutedEventArgs e) => ToggleDashboard();
    public void SetTerminalVisible(bool visible)
    {
        if (!visible && TerminalVisible && TerminalRow.ActualHeight > 0) state.TerminalHeight = TerminalRow.ActualHeight;
        TerminalPanel.Visibility = TerminalSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TerminalRow.MinHeight = visible ? 100 : 0;
        TerminalRow.Height = new GridLength(visible ? Math.Max(100, double.IsFinite(state.TerminalHeight) ? state.TerminalHeight : 240) : 0);
        TerminalSplitterRow.Height = new GridLength(visible ? 4 : 0);
        TerminalToggleButton.Background = visible ? Ui.Brush("#343044") : System.Windows.Media.Brushes.Transparent;
        state.TerminalVisible = visible;
    }
    public void ToggleTerminal() { SetTerminalVisible(!TerminalVisible); if (TerminalVisible) terminal.FocusTerminal(); SaveWorkspace(); }
    private void Terminal_Click(object sender, RoutedEventArgs e) => ToggleTerminal();
    private void HideTerminal_Click(object sender, RoutedEventArgs e) { SetTerminalVisible(false); SaveWorkspace(); }
    private void Stop_Click(object sender, RoutedEventArgs e) => terminal.Stop();
    private void ClearTerminal_Click(object sender, RoutedEventArgs e) { if (BottomTabs.SelectedIndex == 0) terminal.Clear(); else { OperationOutput.Clear(); outputContexts.Clear(); } }
    private void ProjectFilter_Changed(object sender, TextChangedEventArgs e) { if (loaded) Redraw(); }
    private void ProjectList_Changed(object sender, SelectionChangedEventArgs e) { if (!listing && ProjectList.SelectedItem is ListBoxItem { Tag: ProjectState p }) OpenProject(p); }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
