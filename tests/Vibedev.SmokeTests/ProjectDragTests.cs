using System.IO;
using System.Windows;
using System.Windows.Input;
using AvalonDock.Controls;
using AvalonDock.Layout;
using AvalonDock.Serializer.Xml;
using Vibedev;
using Vibedev.Views;

internal static partial class Program
{
    private static async Task ExecuteProjectDragAsync(MainWindow window, string root)
    {
        foreach (var name in new[] { "drag-source", "sidebar-project" })
        {
            var path = Path.Combine(root, name); Directory.CreateDirectory(path);
            await File.WriteAllTextAsync(Path.Combine(path, "notes.txt"), "Editor content survives moving its project.");
            window.AddPath(path);
        }
        window.RefreshStatuses(); await Task.Delay(300);
        window.OpenProject(window.Projects[0]); window.UpdateLayout();
        var original = window.Documents().Single(d => d.Content is ProjectView);
        var view = (ProjectView)original.Content;
        await view.OpenFileAsync(Path.Combine(window.Projects[0].Entry.Path, "notes.txt"));
        var editor = view.Editors.Single();
        Check(view.ProjectTitle.Cursor == Cursors.SizeAll && view.DragRequested is not null, "Project names expose a wired drag handle");
        Check(window.ProjectList.Items.Cast<System.Windows.Controls.ListBoxItem>().All(i => i.Cursor == Cursors.SizeAll), "Sidebar rows expose drag handles");
        Check(!ProjectDragHandle.PassedThreshold(new Point(10, 10), new Point(11, 11)), "Small pointer movement remains a click");
        Check(ProjectDragHandle.PassedThreshold(new Point(), new Point(SystemParameters.MinimumHorizontalDragDistance, 0)) &&
              ProjectDragHandle.PassedThreshold(new Point(), new Point(0, -SystemParameters.MinimumVerticalDragDistance)), "Dragging starts at the Windows threshold in either direction");
        view.DragRequested!(view.ProjectTitle.PointToScreen(new Point(20, 10)));
        await Task.Delay(300);
        Check(original.IsFloating && ReferenceEquals(original.Content, view) && view.Editors.Single() == editor, "Dragging a project title floats the same panel and preserves its editor");
        var originalWindow = Window.GetWindow(view);
        view.DragRequested!(view.ProjectTitle.PointToScreen(new Point(20, 10)));
        await Task.Delay(300);
        Check(ReferenceEquals(Window.GetWindow(view), originalWindow) && window.Dock.Layout.FloatingWindows.Count == 1, "Dragging a floating project reuses its window");
        original.Dock(); await Task.Delay(200);
        var extra = window.OpenProjectWindow(window.Projects[0]); await Task.Delay(300);
        Check(extra != original && extra.IsFloating && window.Documents().Count(d => d.Content is ProjectView v && v.Project == view.Project) == 2,
            "Sidebar drag creates another window for an open project");
        Check(Window.GetWindow((ProjectView)extra.Content) is { ActualWidth: >= 640, ActualHeight: >= 400 }, "New sidebar windows have usable dimensions");
        Check(ReferenceEquals(original.Content, view) && view.Editors.Single() == editor && !original.IsFloating, "Creating a sidebar window preserves the existing panel and editor");
        extra.Close(); await Task.Delay(100);
        var fromSidebar = window.OpenProjectWindow(window.Projects[1]); await Task.Delay(300);
        Check(fromSidebar.IsFloating && window.OpenProjects.Count == 2 && window.Dashboard.DisplayedProjects.Count == 2, "A closed sidebar project opens directly in a floating panel and appears on the dashboard");
        window.SaveWorkspace();
        new XmlLayoutSerializer(window.Dock).Serialize(Path.Combine(root, "state", "layout.xml"));
        var restored = new MainWindow(Path.Combine(root, "state")) { ShowActivated = false, WindowState = WindowState.Normal }; restored.Show();
        await Task.Delay(600);
        Check(restored.Documents().Count(d => d.Content is ProjectView) == 2 && restored.Dock.Layout.FloatingWindows.Count == 1,
            "Dragged project panels restore with their floating layout");
        Check(restored.Documents().Select(d => d.Content).OfType<ProjectView>().Any(v => v.Editors.Any(e => e.FilePath == editor.FilePath)), "Moving projects retains persisted open files");
        restored.Close();
        fromSidebar.Close(); await Task.Delay(200);
        original.Float(); await Task.Delay(200);
        var withoutDockedPanels = window.OpenProjectWindow(window.Projects[1]); await Task.Delay(200);
        Check(Window.GetWindow((ProjectView)withoutDockedPanels.Content) is { ActualWidth: >= 640, ActualHeight: >= 400 },
            "Sidebar windows have usable dimensions even when all existing projects are floating");
        ((LayoutDocumentPane)original.Parent).Children.Add(withoutDockedPanels);
        window.Dock.Layout.CollectGarbage(); await Task.Delay(200);
        view.DragRequested!(view.ProjectTitle.PointToScreen(new Point(20, 10))); await Task.Delay(300);
        Check(original.IsFloating && withoutDockedPanels.IsFloating && !ReferenceEquals(original.Parent, withoutDockedPanels.Parent) &&
              window.Dock.Layout.FloatingWindows.Count == 2 && view.Editors.Single() == editor,
            "Dragging a name from a shared floating window detaches only that project");
        withoutDockedPanels.Close(); original.Dock(); await Task.Delay(200);
        original.IsSelected = true; original.IsActive = true; window.UpdateLayout();
        File.WriteAllLines(Path.Combine(OutputDirectory, "project-drag-results.txt"), Results);
    }
}
