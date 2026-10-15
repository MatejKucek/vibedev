using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AvalonDock.Layout;
using Vibedev;
using Vibedev.Core;
using Vibedev.Views;

internal static partial class Program
{
    private static async Task ExecuteProjectContextMenusAsync(MainWindow window, string root)
    {
        var repository = Path.Combine(root, "menu-repository"); Directory.CreateDirectory(repository);
        var local = Path.Combine(root, "local-project"); Directory.CreateDirectory(local);
        var remote = Path.Combine(root, "remote.git"); Directory.CreateDirectory(remote);
        async Task Git(string path, params string[] args)
        {
            var result = await ProcessRunner.RunAsync(new("git", args, path));
            Check(result.Success, "Menu fixture: git " + string.Join(" ", args));
        }
        await Git(remote, "init", "--bare", "--initial-branch=main");
        await Git(repository, "init", "--initial-branch=main");
        await Git(repository, "config", "user.name", "Menu test");
        await Git(repository, "config", "user.email", "menu@example.invalid");
        var file = Path.Combine(repository, "notes.txt"); await File.WriteAllTextAsync(file, "Keep this editor open.");
        await Git(repository, "add", "."); await Git(repository, "commit", "-m", "Initial");
        await Git(repository, "remote", "add", "origin", remote); await Git(repository, "push", "-u", "origin", "main");
        window.AddPath(repository); window.AddPath(local);
        var project = window.Projects[0]; project.Git = await window.Git.StatusAsync(repository);
        window.OpenProject(project);
        var document = window.Documents().Single(); var view = (ProjectView)document.Content;
        await view.OpenFileAsync(file); var editor = view.Editors.Single();
        window.OpenProject(window.Projects[1]); window.UpdateLayout();
        var localDocument = window.Documents().Single(d => d != document);
        Check(window.Icon is not null, "Main window loads the purple V app icon");

        // Right-clicking another tile must not invoke the left-click open/activate path.
        var tiles = ((DockPanel)window.Dashboard.Content).Children.OfType<ListBox>().Single();
        var tile = tiles.Items.OfType<ListBoxItem>().Single(i => i.Tag == project);
        var previousSelection = tiles.SelectedItem;
        var rightClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
        { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent };
        tile.RaiseEvent(rightClick);
        Check(rightClick.Handled && tiles.SelectedItem == previousSelection && localDocument.IsActive,
            "Right-clicking a dashboard project leaves selection and the active project unchanged");
        var menu = await OpenMenuAsync(tile);
        Check(MenuItemNamed(menu, "Close project").IsEnabled && MenuItemNamed(menu, "Float project").IsEnabled && MenuItemNamed(menu, "Git").IsEnabled,
            "Dashboard project menu exposes close, float and Git actions");
        CheckMenuText(menu);
        menu.IsOpen = false;
        Check(ReferenceEquals(view.ContextMenu, view.ProjectTitle.ContextMenu), "Project panel and project title share their action menu");
        window.OpenProject(project); window.UpdateLayout();
        menu = await OpenMenuAsync(view);
        ClickMenu(menu, "Float project"); await Task.Delay(250);
        Check(document.IsFloating && ReferenceEquals(document.Content, view) && view.Editors.Single() == editor,
            "Float from the panel menu preserves its project and open editor");
        menu = await OpenMenuAsync(view);
        Check(MenuItemNamed(menu, "Dock project").IsEnabled && !menu.Items.OfType<MenuItem>().Any(i => i.Header.ToString() == "Float project"),
            "Floating project menus replace Float with Dock");
        ClickMenu(menu, "Dock project"); await Task.Delay(250);
        Check(!document.IsFloating && view.Editors.Single() == editor, "Dock returns the same panel and editor to the main workspace");

        menu = await OpenMenuAsync(view);
        var gitMenu = MenuItemNamed(menu, "Git"); gitMenu.IsSubmenuOpen = true; await Task.Delay(100);
        foreach (var item in gitMenu.Items.OfType<MenuItem>()) CheckMenuItemText(item);
        gitMenu.IsSubmenuOpen = false;
        ClickMenu(menu, "Review changes / commit staged", gitMenu);
        await Task.Delay(150);
        Check(view.GitView.IsVisible && document.IsActive, "Git review menu opens the targeted project's Git tab");
        foreach (var operation in new[] { "Pull", "Push" })
        {
            menu = await OpenMenuAsync(view); gitMenu = MenuItemNamed(menu, "Git");
            ClickMenu(menu, operation, gitMenu);
            await WaitUntilAsync(() => !project.Busy, operation + " from context menu finishes");
            Check(!project.Failed && project.Activity == operation.ToLowerInvariant() + " completed",
                operation + " from context menu uses the existing Git operation");
        }
        project.Busy = true;
        menu = await OpenMenuAsync(view);
        Check(!MenuItemNamed(menu, "Git").IsEnabled, "Git commands are disabled while the project is busy");
        menu.IsOpen = false; project.Busy = false;
        window.OpenProject(window.Projects[1]); window.UpdateLayout();
        menu = await OpenMenuAsync((ProjectView)localDocument.Content);
        Check(!MenuItemNamed(menu, "Git").IsEnabled && MenuItemNamed(menu, "Close project").IsEnabled,
            "Local folders retain project actions with Git commands disabled");
        menu.IsOpen = false;

        // A dashboard menu resolves its target again when opened after a layout change.
        var extra = window.OpenProjectWindow(project); await Task.Delay(200);
        tiles = ((DockPanel)window.Dashboard.Content).Children.OfType<ListBox>().Single();
        tile = tiles.Items.OfType<ListBoxItem>().Single(i => i.Tag == project);
        menu = await OpenMenuAsync(tile); ClickMenu(menu, "Dock project"); await Task.Delay(200);
        Check(!extra.IsFloating && window.Documents().Contains(document), "Dashboard Dock targets the active panel when a project has multiple panels");
        tiles = ((DockPanel)window.Dashboard.Content).Children.OfType<ListBox>().Single();
        tile = tiles.Items.OfType<ListBoxItem>().Single(i => i.Tag == project);
        menu = await OpenMenuAsync(tile); ClickMenu(menu, "Close project"); await Task.Delay(200);
        Check(!window.Documents().Contains(extra) && window.Documents().Contains(document) && window.Dashboard.DisplayedProjects.Contains(project),
            "Dashboard Close removes only the targeted panel and keeps another panel for that project");
        tiles = ((DockPanel)window.Dashboard.Content).Children.OfType<ListBox>().Single();
        tile = tiles.Items.OfType<ListBoxItem>().Single(i => i.Tag == project);
        menu = await OpenMenuAsync(tile); ClickMenu(menu, "Close project"); await Task.Delay(200);
        Check(!window.Documents().Contains(document) && window.Documents().Contains(localDocument) && window.Projects.Contains(project) && !window.Dashboard.DisplayedProjects.Contains(project),
            "Closing the last project panel removes its dashboard card and preserves its registration and other projects");
        menu = await OpenMenuAsync(window.Dashboard);
        Check(MenuItemNamed(menu, "Add project…").IsEnabled && MenuItemNamed(menu, "Pull all projects").IsEnabled,
            "Dashboard background exposes workspace actions");
        Check(ReferenceEquals(window.DashboardHost.ContextMenu, window.Dashboard.ContextMenu), "Dashboard margins and empty background share workspace actions");
        ClickMenu(menu, "Hide dashboard"); window.UpdateLayout();
        Check(!window.DashboardVisible && window.Documents().Contains(localDocument), "Dashboard background Hide preserves project panels");
        window.ToggleDashboard();

        var sidebarItem = window.ProjectList.Items.OfType<ListBoxItem>().Single(i => i.Tag == project);
        menu = await OpenMenuAsync(sidebarItem);
        Check(MenuItemNamed(menu, "Open project").IsEnabled && MenuItemNamed(menu, "Remove from workspace").IsEnabled,
            "Projects sidebar menu exposes open and remove actions");
        CheckMenuText(menu);
        Check(MenuItemNamed(menu, "Open project").Foreground is SolidColorBrush { Color: var light } && light.R > 0xC0,
            "Sidebar menu text uses the light-on-dark theme");
        ClickMenu(menu, "Remove from workspace"); await Task.Delay(200);
        Check(!window.Projects.Contains(project) && window.Documents().Contains(localDocument) && !window.ProjectList.Items.OfType<ListBoxItem>().Any(i => i.Tag == project),
            "Sidebar Remove unregisters the project and keeps other panels");
        menu = await OpenMenuAsync((ProjectView)localDocument.Content);
        ClickMenu(menu, "Remove from workspace"); await Task.Delay(200);
        Check(window.Projects.Count == 0 && !window.Documents().Any(), "Panel Remove closes the panel and unregisters its project");
        File.WriteAllLines(Path.Combine(OutputDirectory, "context-menu-results.txt"), Results);
    }

    private static async Task<ContextMenu> OpenMenuAsync(FrameworkElement target)
    {
        var menu = target.ContextMenu ?? throw new InvalidOperationException("Missing context menu");
        menu.PlacementTarget = target; menu.IsOpen = true; await Task.Delay(100); return menu;
    }
    private static MenuItem MenuItemNamed(ItemsControl menu, string header) => menu.Items.OfType<MenuItem>().Single(i => i.Header.ToString() == header);
    private static void ClickMenu(ContextMenu menu, string header, ItemsControl? parent = null)
    {
        var item = MenuItemNamed(parent ?? menu, header);
        Check(item.IsEnabled, "Menu action is enabled: " + header);
        menu.IsOpen = false; item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }
}
