using System.IO;
using System.Windows;
using System.Windows.Controls;
using AvalonDock.Layout;
using AvalonDock.Serializer.Xml;
using Vibedev;
using Vibedev.Views;

internal static partial class Program
{
    private static async Task ExecuteDashboardAsync(MainWindow window, string root)
    {
        foreach (var name in new[] { "top-left", "top-right", "bottom-project" })
        {
            var path = Path.Combine(root, name); Directory.CreateDirectory(path); window.AddPath(path);
        }
        window.OpenProject(window.Projects[0]);
        var first = window.Documents().Single();
        window.SplitWithProject(first, Orientation.Horizontal, window.Projects[1]);
        var second = window.Documents().Single(d => d != first);
        window.SplitWithProject(first, Orientation.Vertical, window.Projects[2]);
        window.SplitWithProject(second, Orientation.Vertical, window.Projects[0]);
        await Task.Delay(200); window.UpdateLayout();
        CheckDashboardAboveProjects(window);
        Check(window.Documents().Count() == 4 && window.Documents().All(d => ((ProjectView)d.Content).IsVisible),
            "Four project quadrants stay visible beneath the dashboard");
        var documents = window.Documents().ToArray();
        var parents = documents.Select(d => d.Parent).ToArray();
        var selected = documents.Select(d => d.IsSelected).ToArray();
        var dockHeight = window.Dock.ActualHeight;
        window.ToggleDashboard(); window.UpdateLayout();
        Check(!window.DashboardVisible && window.DashboardHost.ActualHeight == 0 && window.Dock.ActualHeight > dockHeight,
            "Hiding the dashboard returns its row height to the project workspace");
        window.ToggleDashboard(); window.UpdateLayout();
        Check(documents.SequenceEqual(window.Documents()) && parents.SequenceEqual(documents.Select(d => d.Parent)) && selected.SequenceEqual(documents.Select(d => d.IsSelected)),
            "Dashboard toggles preserve project positions and selections");
        var tiles = ((DockPanel)window.Dashboard.Content).Children.OfType<ListBox>().Single();
        tiles.SelectedItem = tiles.Items[0]; window.UpdateLayout();
        CheckDashboardAboveProjects(window);
        Check(await window.ReplaceProjectAsync(first, window.Projects[1]), "Replacing a project remains available below the dashboard");
        CheckDashboardAboveProjects(window);
        var floatingProject = window.OpenProjectWindow(window.Projects[0]); await Task.Delay(200);
        CheckDashboardAboveProjects(window);
        floatingProject.Close(); await Task.Delay(200);
        CheckDashboardAboveProjects(window);
        await CaptureAsync(window, "dashboard-four-quadrants.png");

        // Old versions saved the dashboard as the selected tab in a project pane.
        window.SaveWorkspace();
        var legacy = new LayoutDocument { ContentId = "dashboard", Title = "Dashboard", Content = new TextBlock { Text = "Legacy dashboard" } };
        var pane = (LayoutDocumentPane)first.Parent; pane.Children.Add(legacy); legacy.IsSelected = true; legacy.IsActive = true;
        var layoutPath = Path.Combine(root, "state", "layout.xml");
        new XmlLayoutSerializer(window.Dock).Serialize(layoutPath);
        var restored = new MainWindow(Path.Combine(root, "state")) { ShowActivated = false, WindowState = WindowState.Normal }; restored.Show(); await Task.Delay(600);
        CheckDashboardAboveProjects(restored);
        Check(restored.Documents().Count() == 4 && restored.Documents().All(d => ((ProjectView)d.Content).IsVisible),
            "Restoring an old dashboard tab reveals every project quadrant");
        restored.Close();

        // Also discard an old dashboard-only split instead of keeping an empty quarter.
        var group = new LayoutDocumentPaneGroup(new LayoutDocumentPane());
        ((LayoutDocumentPane)group.Children[0]).Children.Add(legacy);
        window.Dock.Layout.RootPanel.Children.Add(group);
        new XmlLayoutSerializer(window.Dock).Serialize(layoutPath);
        var restoredSplit = new MainWindow(Path.Combine(root, "state")) { ShowActivated = false, WindowState = WindowState.Normal }; restoredSplit.Show(); await Task.Delay(600);
        CheckDashboardAboveProjects(restoredSplit);
        Check(restoredSplit.Dock.Layout.Descendents().OfType<LayoutDocumentPane>().Count() == 4,
            "Restoring an old dashboard-only split removes its empty pane");
        restoredSplit.Close();
        window.Dock.Layout.RootPanel.Children.Remove(group); window.Dock.Layout.CollectGarbage();
        File.WriteAllLines(Path.Combine(OutputDirectory, "dashboard-results.txt"), Results);
    }

    private static void CheckDashboardAboveProjects(MainWindow window)
    {
        window.UpdateLayout();
        var bottom = window.DashboardHost.TranslatePoint(new Point(0, window.DashboardHost.ActualHeight), window).Y;
        var top = window.Dock.TranslatePoint(new Point(), window).Y;
        Check(window.DashboardVisible && ReferenceEquals(window.Dashboard.Parent, window.DashboardHost) &&
              !window.Documents().Any(d => d.ContentId == "dashboard" || d.Content is DashboardView) &&
              window.DashboardHost.ActualHeight > 0 && Math.Abs(window.DashboardHost.ActualWidth - window.Dock.ActualWidth) < 1 && bottom <= top,
            "Dashboard occupies a separate full-width row above all project docking space");
    }
}
