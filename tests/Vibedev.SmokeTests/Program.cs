using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvalonDock.Layout;
using Vibedev;
using Vibedev.Core;
using Vibedev.Services;
using Vibedev.Views;

internal static partial class Program
{
    private static readonly string OutputDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts"));
    private static readonly List<string> Results = [];
    [STAThread]
    private static int Main(string[] args)
    {
        Directory.CreateDirectory(OutputDirectory);
        var app = new App { SuppressStartupWindow = true }; app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (_, e) => { Results.Add("FAIL: " + e.Exception); Finish(1); e.Handled = true; };
        var root = Path.Combine(Path.GetTempPath(), "Vibedev-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var window = new MainWindow(Path.Combine(root, "state")) { ShowActivated = false };
        var started = false;
        window.ContentRendered += async (_, _) =>
        {
            if (started) return; started = true;
            try
            {
                if (args.Contains("--project-drag")) await ExecuteProjectDragAsync(window, root);
                else if (args.Contains("--dashboard")) await ExecuteDashboardAsync(window, root);
                else if (args.Contains("--context-menu")) await ExecuteProjectContextMenusAsync(window, root);
                else await ExecuteAsync(window, root);
                Finish(0);
            }
            catch (Exception ex) { Results.Add("FAIL: " + ex); Finish(1); }
        };
        app.Run(window); return Environment.ExitCode;
    }
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description); Results.Add("PASS: " + description);
    }
    private static void Finish(int code)
    {
        File.WriteAllLines(Path.Combine(OutputDirectory, "smoke-results.txt"), Results); Environment.ExitCode = code; Application.Current.Shutdown(code);
    }
    private static async Task ExecuteAsync(MainWindow window, string root)
    {
        var terminal = window.TerminalHost.Children.OfType<TerminalControl>().Single();
        for (var i = 0; i < 200 && !terminal.IsReady; i++) await Task.Delay(100);
        Check(terminal.IsReady, "WebView2 and bundled xterm terminal initialize");
        Check(window.DashboardVisible && ((SolidColorBrush)window.DashboardToggleButton.Background).Color == Color.FromRgb(0x34, 0x30, 0x44), "Visible dashboard highlights its activity-rail button");
        window.DashboardToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
        Check(!window.DashboardVisible && !window.Documents().Any(d => d.ContentId == "dashboard") && window.DashboardToggleButton.Background == Brushes.Transparent, "Dashboard button hides the empty dashboard and clears its highlight");
        window.DashboardToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
        Check(window.DashboardVisible, "Dashboard button shows the dashboard again with no open projects");
        await Task.Delay(500); await CaptureAsync(window, "dashboard-empty.png");
        var repo = Path.Combine(root, "sample-api"); Directory.CreateDirectory(repo);
        var gitInit = await ProcessRunner.RunAsync(new("git", ["init", "--initial-branch=main"], repo)); Check(gitInit.Success, "Disposable repository created");
        await ProcessRunner.RunAsync(new("git", ["config", "user.name", "Smoke test"], repo));
        await ProcessRunner.RunAsync(new("git", ["config", "user.email", "smoke@example.invalid"], repo));
        await ProcessRunner.RunAsync(new("git", ["config", "commit.gpgsign", "false"], repo));
        await ProcessRunner.RunAsync(new("git", ["config", "core.hooksPath", Path.Combine(root, "no-hooks")], repo));
        var file = Path.Combine(repo, "Program.cs"); await File.WriteAllTextAsync(file, "using System;\r\n\r\n// A project in your workspace\r\nConsole.WriteLine(\"Hello, Vibedev!\");\r\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "Sample.Api.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup></Project>");
        Directory.CreateDirectory(Path.Combine(repo, ".vscode"));
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        var nestedFile = Path.Combine(repo, "src", "Nested.cs"); await File.WriteAllTextAsync(nestedFile, "// nested file\r\n");
        var helperFile = Path.Combine(repo, "src", "LanguageHelper.cs"); await File.WriteAllTextAsync(helperFile, "public static class LanguageHelper { public static string Message => \"ready\"; }\r\n");
        await File.WriteAllTextAsync(Path.Combine(repo, ".vscode", "launch.json"), """{"configurations":[{"name":"Sample API","type":"coreclr","request":"launch","program":"${workspaceFolder}/bin/Debug/net10.0/Sample.Api.dll","cwd":"${workspaceFolder}","preLaunchTask":"build"}]}""");
        await File.WriteAllTextAsync(Path.Combine(repo, ".vscode", "tasks.json"), """{"tasks":[{"label":"build","type":"process","command":"dotnet","args":["build","--nologo"]}]}""");
        window.AddPath(repo);
        var second = Path.Combine(root, "desktop-client"); Directory.CreateDirectory(second); window.AddPath(second);
        var third = Path.Combine(root, "shared-library"); Directory.CreateDirectory(third); window.AddPath(third);
        window.RefreshStatuses(); await Task.Delay(300);
        Check(window.Projects.Count == 3 && window.Dashboard.DisplayedProjects.Count == 0, "Registered projects stay off the dashboard until their panels open");
        window.OpenProject(window.Projects[0]);
        Check(window.DashboardVisible && ((SolidColorBrush)window.DashboardToggleButton.Background).Color == Color.FromRgb(0x34, 0x30, 0x44), "Opening a project preserves the separate dashboard and its highlight");
        Check(window.Dashboard.DisplayedProjects.Count == 1 && window.Dashboard.DisplayedProjects[0] == window.Projects[0], "Dashboard immediately shows the opened project only");
        var firstDoc = window.Documents().First(d => d.Content is ProjectView);
        var firstView = (ProjectView)firstDoc.Content;
        window.UpdateLayout();
        Check(firstView.ProjectTitle.IsVisible && firstView.ProjectTitle.Text == window.Projects[0].Entry.Name, "Project name appears in the panel header");
        Check(!Descendants<AvalonDock.Controls.DocumentPaneTabPanel>(window.Dock).Any(p => p.IsVisible), "Project tab strips are hidden without leaving a header row");
        window.OpenProject(window.Projects[1]);
        Check(window.Dashboard.DisplayedProjects.Count == 2, "Background project tabs remain on the dashboard");
        ((ProjectView)window.Documents().Single(d => d.Content is ProjectView v && v.Project == window.Projects[1]).Content).CloseProjectButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(window.Dashboard.DisplayedProjects.Count == 1, "Closing a project panel immediately removes its dashboard card");
        firstDoc.IsActive = true;
        var fileRoot = firstView.FileTree.Tree.Items.OfType<TreeViewItem>().Single();
        var sourceFolder = fileRoot.Items.OfType<TreeViewItem>().Single(n => n.Tag as string == Path.Combine(repo, "src"));
        sourceFolder.IsExpanded = true;
        Check(sourceFolder.Items.OfType<TreeViewItem>().Any(n => n.Tag as string == nestedFile), "File tree expands folders to show nested files");
        Check(!fileRoot.Items.OfType<TreeViewItem>().Any(n => Path.GetFileName(n.Tag as string) == ".git"), "File tree keeps Git metadata out of project navigation");
        firstView.FileTree.Reveal(nestedFile); firstView.RefreshTree();
        fileRoot = firstView.FileTree.Tree.Items.OfType<TreeViewItem>().Single();
        sourceFolder = fileRoot.Items.OfType<TreeViewItem>().Single(n => n.Tag as string == Path.Combine(repo, "src"));
        Check(sourceFolder.IsExpanded && (firstView.FileTree.Tree.SelectedItem as TreeViewItem)?.Tag as string == nestedFile, "Refreshing the file tree preserves folder expansion and selected file");
        await firstView.OpenFileAsync(file); await firstView.OpenFileAsync(file);
        Check(firstView.Editors.Count() == 1, "Opening a file twice activates its existing tab");
        var editor = firstView.Editors.Single(); editor.Editor.AppendText("// saved from the editor\r\n");
        Check(editor.IsDirty && firstDoc.Title.EndsWith(" ●"), "Editing marks project and file tabs dirty");
        var searchHits = await firstView.Search.FindForTestAsync("saved from the editor", ProjectSearchMode.Contents);
        Check(searchHits.Count == 1 && searchHits[0].FilePath == file, "Project-tab search includes unsaved content from its editor");
        await firstView.OpenSearchHitAsync(searchHits[0]);
        Check(editor.Editor.SelectedText == "saved from the editor", "Selecting a search result opens and selects its matching text");
        var names = await firstView.Search.FindForTestAsync("Nested.cs", ProjectSearchMode.FileNames);
        Check(names.Count == 1 && names[0].FilePath == nestedFile, "Filename search finds a nested project file");
        Check(await editor.SaveAsync(), "Editor saves successfully");
        Check((await File.ReadAllTextAsync(file)).Contains("saved from the editor"), "Editor changes reach disk");
        editor.Editor.AppendText("// autosaved from the editor\r\n");
        await WaitUntilAsync(() => !editor.IsDirty && File.ReadAllText(file).Contains("autosaved from the editor"), "Edits autosave after typing pauses and clear the dirty indicator");
        await File.AppendAllTextAsync(file, "// external edit\r\n");
        await WaitUntilAsync(() => editor.Editor.Text.Contains("external edit"), "File watcher updates clean editors automatically");
        Check(editor.Editor.Text.Contains("external edit") && !editor.IsDirty, "Clean editors refresh external changes without becoming dirty");
        editor.Editor.AppendText("// unsaved\r\n"); await File.AppendAllTextAsync(file, "// another external edit\r\n"); await Task.Delay(900);
        Check(editor.Editor.Text.Contains("unsaved") && !editor.Editor.Text.Contains("another external edit"), "External refresh preserves unsaved editor buffers");
        await WaitUntilAsync(() => Descendants<TextBlock>((Grid)editor.Content).Any(t => t.Text.Contains("Autosave paused")), "Autosave reports disk conflicts in the editor footer without a dialog");
        Check(!(await File.ReadAllTextAsync(file)).Contains("// unsaved"), "Autosave preserves external disk changes");
        editor.Editor.Undo(); await editor.RefreshFromDiskAsync();
        var watchedFile = Path.Combine(repo, "src", "watched.txt"); await File.WriteAllTextAsync(watchedFile, "created");
        await WaitUntilAsync(() => TreeHas(firstView.FileTree.Tree.Items, watchedFile), "File watcher reveals new files while preserving expanded folders");
        var renamedFile = Path.Combine(repo, "src", "renamed.txt"); File.Move(watchedFile, renamedFile);
        await WaitUntilAsync(() => TreeHas(firstView.FileTree.Tree.Items, renamedFile) && !TreeHas(firstView.FileTree.Tree.Items, watchedFile), "File watcher reflects renamed files");
        File.Delete(renamedFile); await WaitUntilAsync(() => !TreeHas(firstView.FileTree.Tree.Items, renamedFile), "File watcher reflects deleted files");
        Check(await window.GitActionAsync(window.Projects[0], "commit", "Smoke test commit"), "Commit-all action stages and commits edited file");
        Check(!await window.GitActionAsync(window.Projects[0], "pull"), "Failed pull is reported without a remote");
        Check(window.Projects[0].Failed, "Failed pull appears on project state");
        window.RunProject(firstView);
        for (var i = 0; i < 300 && window.RunningProject is not null; i++) await Task.Delay(100);
        Check(window.RunningProject is null && window.OperationOutput.Text.Contains("Sample API exited with code 0"), "Run dropdown builds and launches the configured .NET application end to end");
        var originalText = editor.Editor.Text;
        firstDoc.IsActive = true; window.Activate(); editor.Editor.Focus();
        editor.Editor.AppendText("\r\nstring languageProbe = 123;\r\nConsole.WriteLine(LanguageHelper.Message);\r\n");
        await WaitUntilAsync(() => firstView.LanguageFeatures(editor)?.Diagnostics.Any(d => d.GetProperty("message").GetString()!.Contains("int", StringComparison.OrdinalIgnoreCase)) == true, "C# server reports diagnostics for unsaved code", 60000);
        var features = firstView.LanguageFeatures(editor)!;
        editor.Editor.CaretOffset = editor.Editor.Text.LastIndexOf("Message", StringComparison.Ordinal) + 3;
        var hover = await features.RequestAtCaretAsync("textDocument/hover");
        Check(hover.ToString().Contains("Message"), "C# hover returns symbol information");
        await features.ShowCompletionAsync();
        Check(features.Completion?.CompletionList.CompletionData.Any(c => c.Text == "Message") == true, "C# completion popup offers members of the correct type");
        features.Completion!.CompletionList.SelectedItem = features.Completion.CompletionList.CompletionData.Single(c => c.Text == "Message");
        features.Completion.CompletionList.RequestInsertion(EventArgs.Empty);
        await features.CompletionApplied;
        Check(editor.Editor.Text.Contains("LanguageHelper.Message);"), "Accepting C# completion applies the server edit without duplicating the existing suffix");
        editor.Editor.CaretOffset = editor.Editor.Text.LastIndexOf("Message", StringComparison.Ordinal) + 3;
        await features.GoToDefinitionAsync();
        Check(firstView.Editors.Any(e => e.FilePath == helperFile && e.Editor.TextArea.Caret.Line == 1), "F12 definition opens the target source file and position");
        await firstView.OpenFileAsync(file); editor.Editor.Text = originalText;
        await Task.Delay(1200);
        await WaitUntilAsync(() => features.Diagnostics.All(d => !d.GetProperty("message").GetString()!.Contains("Cannot implicitly convert type", StringComparison.OrdinalIgnoreCase)), "C# diagnostics clear after fixing the unsaved error");
        var gitFile = Path.Combine(repo, "selected.txt"); await File.WriteAllTextAsync(gitFile, "selected change\n");
        var otherGitFile = Path.Combine(repo, "not-selected.txt"); await File.WriteAllTextAsync(otherGitFile, "keep unstaged\n");
        firstView.ShowGit(); await firstView.GitView.RefreshAsync();
        var selectedItem = firstView.GitView.ChangesList.Items.OfType<ListBoxItem>().Single(i => ((GitFileChange)i.Tag).Path == "selected.txt"); selectedItem.IsSelected = true;
        await WaitUntilAsync(() => Descendants<GitDiffTab>(firstView).Any(t => t.Editor.Text.Contains("+selected change")), "Selecting a Git change opens its colored diff");
        await firstView.GitView.ChangeStageAsync(false); await firstView.GitView.RefreshAsync();
        Check(firstView.GitView.Files.Single(f => f.Path == "selected.txt").Staged && !firstView.GitView.Files.Single(f => f.Path == "not-selected.txt").Staged, "Git tab stages only selected files");
        firstView.GitView.StagedList.Items.OfType<ListBoxItem>().Single(i => ((GitFileChange)i.Tag).Path == "selected.txt").IsSelected = true;
        await firstView.GitView.ChangeStageAsync(true); await firstView.GitView.RefreshAsync();
        Check(!firstView.GitView.Files.Single(f => f.Path == "selected.txt").Staged && File.Exists(gitFile), "Git tab unstages selected files while preserving working copies");
        await CaptureAsync(window, "git-tab.png");
        firstView.ShowExplorer();
        await window.OpenLocationAsync(new(file, 4, 2)); Check(editor.Editor.TextArea.Caret.Line == 4 && editor.Editor.TextArea.Caret.Column == 2, "Build-error navigation opens the requested editor position");
        terminal.Write($"\r\n{file}(3,2): error CS1002: smoke clickable error\r\n"); await Task.Delay(500);
        var activated = new TaskCompletionSource<BuildLocation>(TaskCreationOptions.RunContinuationsAsynchronously);
        terminal.LocationActivated += location => activated.TrySetResult(location);
        var linkResult = await terminal.EvaluateAsync("(()=>{for(let y=1;y<=terminal.buffer.active.length;y++){let found=false;buildLinkProvider.provideLinks(y,links=>{const link=links.find(l=>l.text.includes('Program.cs(3,2)'));if(link){link.activate();found=true;}});if(found)return true;}return false;})()");
        Check(linkResult == "true" && (await activated.Task.WaitAsync(TimeSpan.FromSeconds(5))).Line == 3, "xterm build-error links activate native file navigation");
        window.SetTerminalVisible(false); window.UpdateLayout(); Check(!window.TerminalVisible && window.TerminalRow.ActualHeight == 0, "Hiding terminal returns its height to the editor");
        window.SetTerminalVisible(true); window.UpdateLayout(); Check(window.TerminalVisible && window.TerminalRow.ActualHeight >= 100, "Showing terminal restores its saved height");
        window.OpenProject(window.Projects[1]);
        var projectDocuments = window.Documents().Where(d => d.Content is ProjectView).ToArray();
        window.SetDashboardVisible(false);
        window.DashboardToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
        Check(window.DashboardVisible && projectDocuments.Any(d => ((ProjectView)d.Content).IsVisible), "Dashboard button shows the separate row without covering a project");
        window.DashboardToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
        Check(!window.DashboardVisible && projectDocuments.Any(d => ((ProjectView)d.Content).IsVisible) && projectDocuments.SequenceEqual(window.Documents().Where(d => d.Content is ProjectView)), "Hiding the dashboard preserves visible editors and open project panels");
        window.DashboardToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
        Check(window.DashboardVisible && ((SolidColorBrush)window.DashboardToggleButton.Background).Color == Color.FromRgb(0x34, 0x30, 0x44), "Showing the dashboard restores its highlight");
        await Task.Delay(250); await CaptureAsync(window, "dashboard-projects.png");
        var dashboardList = ((DockPanel)window.Dashboard.Content).Children.OfType<ListBox>().Single();
        var projectTiles = dashboardList.Items.OfType<ListBoxItem>().ToArray();
        var projectLabels = Descendants<TextBlock>((Border)projectTiles[0].Content).ToArray();
        var pullAllButton = Descendants<Button>(window.Dashboard).Single(b => b.Content?.ToString()?.Contains("Pull all") == true);
        var pullAllCenter = pullAllButton.TranslatePoint(new Point(0, pullAllButton.ActualHeight / 2), window.Dashboard).Y;
        Check(projectLabels.All(label => Math.Abs(label.TranslatePoint(new Point(0, label.ActualHeight / 2), window.Dashboard).Y - pullAllCenter) < 2),
            "Dashboard project names and Git info share one line with Pull all");
        Check(!Descendants<TextBlock>(window.Dashboard).Any(label => label.Text.StartsWith("PROJECTS")), "Dashboard omits the projects heading and open count");
        var firstTile = projectTiles[0].TranslatePoint(new Point(), dashboardList);
        var secondTile = projectTiles[1].TranslatePoint(new Point(), dashboardList);
        Check(secondTile.X > firstTile.X && Math.Abs(secondTile.Y - firstTile.Y) < 1, "Dashboard projects appear horizontally next to each other");
        dashboardList.Width = 300; window.UpdateLayout();
        firstTile = projectTiles[0].TranslatePoint(new Point(), dashboardList);
        secondTile = projectTiles[1].TranslatePoint(new Point(), dashboardList);
        Check(secondTile.X > firstTile.X && Math.Abs(secondTile.Y - firstTile.Y) < 1 && Descendants<ScrollViewer>(dashboardList).First().ScrollableWidth > 0,
            "Dashboard projects stay in one scrollable row when the available width is narrow");
        dashboardList.Width = double.NaN; window.UpdateLayout();
        window.Documents().Single(d => d.Content is ProjectView v && v.Project == window.Projects[1]).Close();
        firstDoc.IsActive = true; await Task.Delay(250); await CaptureAsync(window, "editor.png");
        firstView.ShowSearch();
        var consoleHits = await firstView.Search.FindForTestAsync("Console", ProjectSearchMode.Both);
        await Task.Delay(500);
        Check(consoleHits.Count > 0 && firstView.Search.DisplayedHits.SequenceEqual(consoleHits) && consoleHits.All(h => h.Preview.Contains("Console", StringComparison.OrdinalIgnoreCase)), "Opening Search and changing its query keeps only the latest matching results");
        await CaptureAsync(window, "project-search.png");
        firstView.ShowExplorer();
        window.ProjectsColumn.Width = new GridLength(280); window.UpdateLayout();
        window.ToggleProjectSidebar(); window.UpdateLayout();
        Check(!window.ProjectSidebarVisible && window.ProjectsColumn.ActualWidth == 0 && window.ProjectsSplitterColumn.ActualWidth == 0, "Hiding Projects removes the sidebar and its splitter completely");
        await CaptureAsync(window, "sidebar-hidden.png");
        window.ToggleProjectSidebar(); window.UpdateLayout();
        Check(window.ProjectSidebarVisible && Math.Abs(window.ProjectsColumn.ActualWidth - 280) < 1, "Showing Projects restores its previous width");
        window.SplitWithProject(firstDoc, Orientation.Horizontal, window.Projects[1]);
        var secondDoc = window.Documents().Single(d => d.Content is ProjectView v && v.Project == window.Projects[1]);
        window.SplitWithProject(secondDoc, Orientation.Vertical, window.Projects[2]);
        Check(window.Dock.Layout.Descendents().OfType<LayoutDocumentPaneGroup>().Count() >= 2, "Recursive horizontal and vertical splits are created");
        var thirdDoc = window.Documents().Single(d => d.Content is ProjectView v && v.Project == window.Projects[2]);
        var firstParent = firstDoc.Parent; var thirdParent = thirdDoc.Parent;
        window.SwapProjectDocuments(firstDoc, thirdDoc);
        Check(ReferenceEquals(firstDoc.Parent, thirdParent) && ReferenceEquals(thirdDoc.Parent, firstParent), "Project-tab swap exchanges split positions without reopening editors");
        Check(window.Dashboard.DisplayedProjects.SequenceEqual(new[] { window.Projects[2], window.Projects[1], window.Projects[0] }), "Dashboard follows project panel order after a swap");
        var dashboardRows = ((DockPanel)window.Dashboard.Content).Children.OfType<ListBox>().Single().Items.OfType<ListBoxItem>().ToArray();
        Check(dashboardRows.Select((row, index) => row.Content is Border frame && frame.BorderThickness.Left > 0 && Descendants<TextBlock>(frame).First().Text == window.Dashboard.DisplayedProjects[index].Entry.Name).All(matches => matches), "Dashboard projects have individual frames and names without numbers");
        window.SwapProjectDocuments(firstDoc, thirdDoc);
        var samePane = (LayoutDocumentPane)firstDoc.Parent;
        // Split swap already covered above; exercise same-pane ordering before restoring it.
        var temporaryDoc = new LayoutDocument { Content = new ProjectView(window, window.Projects[1]), ContentId = "swap-test", Title = "swap-test" };
        samePane.Children.Add(temporaryDoc); var firstIndex = samePane.Children.IndexOf(firstDoc); var peerIndex = samePane.Children.IndexOf(temporaryDoc);
        window.SwapProjectDocuments(firstDoc, temporaryDoc);
        Check(samePane.Children.IndexOf(firstDoc) == peerIndex && samePane.Children.IndexOf(temporaryDoc) == firstIndex, "Project-tab swap exchanges exact positions within one tab group");
        window.SwapProjectDocuments(firstDoc, temporaryDoc); samePane.Children.Remove(temporaryDoc); ((ProjectView)temporaryDoc.Content).Dispose();
        var projectMenu = new ContextMenu(); window.PopulateProjectMenu(projectMenu, firstDoc);
        Check(projectMenu.Items.OfType<MenuItem>().Any(i => i.Header.ToString() == "Swap with project" && i.Items.Count == 2), "Project context menu lists other projects as swap targets");
        Check(projectMenu.Items.OfType<MenuItem>().Single(i => i.Header.ToString() == "Replace project").Items.OfType<MenuItem>().Select(i => i.Header.ToString()).SequenceEqual(window.Projects.Skip(1).Select(p => p.Entry.Name)), "Project context menu lists other registered projects as replacement targets");
        var closedPath = Path.Combine(root, "replacement-project"); Directory.CreateDirectory(closedPath); window.AddPath(closedPath);
        var closedProject = window.Projects.Last(); var secondParent = secondDoc.Parent; var secondId = secondDoc.ContentId;
        Check(await window.ReplaceProjectAsync(secondDoc, closedProject), "A panel can be replaced with a registered project that is not open");
        Check(ReferenceEquals(secondDoc.Parent, secondParent) && secondDoc.ContentId == secondId && ((ProjectView)secondDoc.Content).Project == closedProject && firstView.Editors.Contains(editor), "Replacement preserves panel position and ID without disturbing other editors");
        Check(window.Dashboard.DisplayedProjects.Contains(closedProject) && !window.Dashboard.DisplayedProjects.Contains(window.Projects[1]), "Dashboard updates when a project is replaced");
        Check(new WorkspaceStore(Path.Combine(root, "state")).Load().PanelProjects[secondId] == closedPath, "Replacement persists the new project under the existing panel ID");
        Check(!await window.ReplaceProjectAsync(secondDoc, closedProject), "Replacing a panel with its current project leaves it unchanged");
        Check(await window.ReplaceProjectAsync(secondDoc, window.Projects[1]), "A replacement panel can be switched back");
        var actualMenu = window.Dock.DocumentContextMenu; actualMenu.DataContext = window.Dock.GetLayoutItemFromModel(firstDoc); actualMenu.PlacementTarget = window.Dock; actualMenu.IsOpen = true;
        Check(actualMenu.Items.OfType<MenuItem>().Any(i => i.Header.ToString() == "Swap with project"), "Native project-tab context menu populates swap commands for the clicked document");
        await Task.Delay(150);
        CheckMenuText(actualMenu);
        var swapMenu = actualMenu.Items.OfType<MenuItem>().Single(i => i.Header.ToString() == "Swap with project"); swapMenu.IsSubmenuOpen = true;
        await Task.Delay(150);
        foreach (var item in swapMenu.Items.OfType<MenuItem>()) CheckMenuItemText(item);
        swapMenu.IsSubmenuOpen = false;
        actualMenu.IsOpen = false;
        firstDoc.IsSelected = true; firstDoc.IsActive = true; window.UpdateLayout();
        var titleMenu = firstView.ProjectTitle.ContextMenu; titleMenu.PlacementTarget = firstView.ProjectTitle; titleMenu.IsOpen = true;
        await Task.Delay(150);
        Check(titleMenu.Items.OfType<MenuItem>().Any(i => i.Header.ToString() == "Swap with project"), "Project name context menu retains project swap commands");
        CheckMenuText(titleMenu);
        titleMenu.IsOpen = false;
        var tooltipText = new TextBlock { Text = "Readable project tooltip" };
        var tooltip = new ToolTip { Content = tooltipText, PlacementTarget = firstView.ProjectTitle, IsOpen = true };
        await Task.Delay(150);
        var tooltipBackground = ((SolidColorBrush)tooltip.Background).Color; var tooltipForeground = ((SolidColorBrush)tooltipText.Foreground).Color;
        Check(tooltipBackground.R < 80 && tooltipBackground.G < 80 && tooltipBackground.B < 80 && tooltipForeground.R > 200 && tooltipForeground.G > 200 && tooltipForeground.B > 200, "Tooltips render light content on a dark background");
        tooltip.IsOpen = false;
        var autosavePath = Path.Combine(repo, "autosave-encoding.txt");
        await File.WriteAllTextAsync(autosavePath, "original\r\n", new UnicodeEncoding(false, true));
        using (var autosaveEditor = new EditorTab(await TextFile.OpenAsync(autosavePath), _ => { }, () => { }))
        {
            autosaveEditor.Editor.AppendText("background edit\r\n");
            await WaitUntilAsync(() => !autosaveEditor.IsDirty, "Background editor changes autosave without activating the tab");
            var autosaveBytes = await File.ReadAllBytesAsync(autosavePath);
            Check(autosaveBytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }) && (await File.ReadAllTextAsync(autosavePath)) == "original\r\nbackground edit\r\n", "Autosave preserves UTF-16 encoding, BOM and CRLF line endings");
            autosaveEditor.Editor.AppendText("discarded edit\r\n"); autosaveEditor.Dispose();
            await Task.Delay(1000);
            Check(!(await File.ReadAllTextAsync(autosavePath)).Contains("discarded edit"), "Closing an editor cancels its pending autosave");
        }
        await Task.Delay(250); await CaptureAsync(window, "nested-panels.png");
        secondDoc.Float(); await Task.Delay(200);
        Check(window.Dock.Layout.FloatingWindows.Count > 0, "Project panel floats into a separate WPF window");
        Check(window.Dashboard.DisplayedProjects.Count == 3, "Dashboard includes projects in floating and split panels");
        var floatingParent = secondDoc.Parent; var floatingWindow = window.Dock.Layout.FloatingWindows.Single();
        Check(await window.ReplaceProjectAsync(secondDoc, closedProject), "A floating project panel can be replaced");
        Check(ReferenceEquals(secondDoc.Parent, floatingParent) && window.Dock.Layout.FloatingWindows.Contains(floatingWindow), "Replacement preserves the floating window");
        Check(await window.ReplaceProjectAsync(secondDoc, window.Projects[1]), "Floating replacement can be switched back");
        window.SetProjectSidebarVisible(false);
        window.SetTerminalVisible(false);
        window.SetDashboardVisible(false);
        window.SaveWorkspace();
        var layoutPath = Path.Combine(root, "state", "layout.xml"); new AvalonDock.Serializer.Xml.XmlLayoutSerializer(window.Dock).Serialize(layoutPath);
        var saved = new WorkspaceStore(Path.Combine(root, "state")).Load(); Check(saved.PanelProjects.Count == 3, "Project panels persist with stable IDs");
        var restored = new MainWindow(Path.Combine(root, "state")) { ShowActivated = false }; restored.Show();
        await Task.Delay(800);
        Check(restored.Documents().Count(d => d.Content is ProjectView) == 3, "All project panels restore after reopening");
        Check(restored.Dock.Layout.FloatingWindows.Count > 0, "Floating window layout restores");
        Check(!restored.ProjectSidebarVisible, "Hidden Projects sidebar persists after reopening");
        Check(!restored.TerminalVisible, "Hidden terminal state persists after reopening");
        Check(!restored.DashboardVisible && !restored.Documents().Any(d => d.ContentId == "dashboard") && restored.DashboardToggleButton.Background == Brushes.Transparent, "Hidden dashboard and unlit button persist after reopening");
        restored.DashboardToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); restored.UpdateLayout();
        Check(restored.DashboardVisible, "Hidden dashboard can be shown after reopening");
        Check(restored.Dashboard.DisplayedProjects.Count == 3, "Restored open projects appear on the dashboard");
        Check(!Descendants<AvalonDock.Controls.DocumentPaneTabPanel>(restored.Dock).Any(p => p.IsVisible), "Restored project panes keep their top tabs hidden");
        Check(restored.Documents().OfType<LayoutDocument>().Select(d => d.Content).OfType<ProjectView>().Any(v => v.Editors.Any(e => e.FilePath == file)), "Open editor files restore after reopening");
        restored.Close();
        window.SetTerminalVisible(true);
        // Exercise the actual Windows pseudoconsole with unicode output, input and resize.
        var output = new StringBuilder(); var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var console = new PseudoConsole())
        {
            console.Output += s => { lock (output) output.Append(s); };
            console.Exited += code => exited.TrySetResult(code);
            console.Start(new("powershell.exe", ["-NoProfile", "-Command", "[Console]::OutputEncoding=[Text.Encoding]::UTF8; [Console]::WriteLine('žluťoučký'); $x=[Console]::ReadLine(); [Console]::WriteLine('INPUT:'+$x)"], repo));
            console.Resize(100, 20); await Task.Delay(250); console.Write("from-terminal\r");
            Check(await exited.Task.WaitAsync(TimeSpan.FromSeconds(10)) == 0, "ConPTY program accepts input and exits successfully");
            await Task.Delay(250);
            Check(output.ToString().Contains("žluťoučký") && output.ToString().Contains("INPUT:from-terminal"), "ConPTY preserves Unicode and forwards keyboard input");
        }
        var plan = new LaunchPlan(new("powershell.exe", ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"], repo), []);
        var running = terminal.RunAsync("smoke-run", plan); await Task.Delay(300);
        Check(terminal.IsRunning, "Shared terminal marks a run active");
        await AssertThrowsAsync(() => terminal.RunAsync("second-run", plan));
        terminal.Stop(); await running.WaitAsync(TimeSpan.FromSeconds(10));
        Check(!terminal.IsRunning, "Stop terminates the run and releases the shared terminal");
        window.Close();
    }
    private static async Task AssertThrowsAsync(Func<Task> action)
    {
        try { await action(); throw new Exception("Concurrent Run was unexpectedly accepted."); }
        catch (InvalidOperationException) { Results.Add("PASS: Second Run is refused while the shared terminal is busy"); }
    }
    private static void CheckMenuText(ContextMenu menu)
    {
        foreach (var item in menu.Items.OfType<MenuItem>()) CheckMenuItemText(item);
    }
    private static void CheckMenuItemText(MenuItem item)
    {
        var label = Descendants<TextBlock>(item).First(t => t.Text == item.Header.ToString());
        Check(label.Foreground is SolidColorBrush text && item.Foreground is SolidColorBrush foreground && text.Color == foreground.Color,
            $"Menu text follows its native foreground, including disabled items: {item.Header} (text: {label.Foreground}, item: {item.Foreground})");
    }
    private static async Task WaitUntilAsync(Func<bool> condition, string description, int timeout = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(100);
        Check(condition(), description);
    }
    private static bool TreeHas(ItemCollection items, string path) => items.OfType<TreeViewItem>().Any(n => n.Tag as string == path || TreeHas(n.Items, path));
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static async Task CaptureAsync(MainWindow window, string name)
    {
        // Capture the WebView surface separately; native composition surfaces are not in RenderTargetBitmap.
        var terminal = window.TerminalHost.Children.OfType<TerminalControl>().FirstOrDefault();
        if (terminal is not null && window.BottomTabs.SelectedIndex == 0)
        {
            using var memory = new MemoryStream(); await terminal.CaptureAsync(memory); memory.Position = 0;
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = memory; bitmap.EndInit();
            window.TerminalHost.Children.Clear(); window.TerminalHost.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Fill });
        }
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var render = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); render.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(render));
        using (var file = File.Create(Path.Combine(OutputDirectory, name))) encoder.Save(file);
        if (terminal is not null) { window.TerminalHost.Children.Clear(); window.TerminalHost.Children.Add(terminal); }
    }
}
