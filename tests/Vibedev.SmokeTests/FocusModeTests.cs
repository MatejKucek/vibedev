using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AvalonDock.Layout;
using AvalonDock.Serializer.Xml;
using Vibedev;
using Vibedev.Services;
using Vibedev.Views;

internal static partial class Program
{
    private static async Task ExecuteFocusModeAsync(MainWindow window, string root)
    {
        Check(!window.FocusModeActive, "Focus mode starts disabled with no projects");
        window.ToggleFocusMode();
        Check(!window.FocusModeActive, "Focus toggle without an active project is harmless");
        foreach (var name in new[] { "first", "second", "third" })
        {
            var path = Path.Combine(root, name); Directory.CreateDirectory(path); window.AddPath(path);
        }
        window.OpenProject(window.Projects[0]);
        var first = window.Documents().Single();
        window.SplitWithProject(first, Orientation.Horizontal, window.Projects[1]);
        window.SplitWithProject(first, Orientation.Vertical, window.Projects[2]);
        await Task.Delay(200); window.UpdateLayout();
        var documents = window.Documents().ToArray();
        var view = (ProjectView)first.Content;
        view.ProjectTitle.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        Check(first.IsActive && documents.Where(d => d != first).All(d => !d.IsActive), "Clicking a project header activates only its panel");
        Check(((SolidColorBrush)view.ProjectFrame.BorderBrush).Color == Color.FromRgb(0x3B, 0x9E, 0xFF) && view.ProjectFrame.Effect is not null &&
              documents.Where(d => d != first).All(d => ((ProjectView)d.Content).ProjectFrame.Effect is null), "Only the active project has a blue border and subtle glow");
        var sizes = documents.Select(d => (((ProjectView)d.Content).ActualWidth, ((ProjectView)d.Content).ActualHeight)).ToArray();
        var parents = documents.Select(d => d.Parent).ToArray();
        var before = Path.Combine(root, "before.xml"); new XmlLayoutSerializer(window.Dock).Serialize(before);

        window.Modifiers = () => ModifierKeys.Shift;
        KeyEventArgs PressF(UIElement source)
        {
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(source)!, 0, Key.F) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            source.RaiseEvent(key); window.UpdateLayout(); return key;
        }
        Check(PressF(view.ProjectTitle).Handled && window.FocusModeActive, "Shift+F on the project header enters focus mode");
        await Task.Delay(50); window.UpdateLayout();
        Check(view.ActualWidth >= window.Dock.ActualWidth - 16 && view.ActualHeight >= window.Dock.ActualHeight - 16 &&
              documents.Where(d => d != first).All(d => !((ProjectView)d.Content).IsVisible), "Focused project fills the docking workspace across nested horizontal and vertical splits");
        Check(window.DashboardVisible && window.Dashboard.DisplayedProjects.Count == 3 && window.ProjectSidebarVisible && window.TerminalVisible,
            "Focus mode preserves the dashboard, sidebar, terminal and open-project list");
        var during = Path.Combine(root, "during.xml"); new XmlLayoutSerializer(window.Dock).Serialize(during);
        Check(File.ReadAllText(before) == File.ReadAllText(during), "Focus mode leaves the serialized docking layout unchanged");
        await CaptureAsync(window, "focus-mode.png");
        Check(PressF(view.ProjectTitle).Handled && !window.FocusModeActive, "Pressing Shift+F again restores the split layout");
        Check(documents.SequenceEqual(window.Documents()) && parents.SequenceEqual(documents.Select(d => d.Parent)) &&
              documents.Select((d, i) => Math.Abs(((ProjectView)d.Content).ActualWidth - sizes[i].Item1) < 1 && Math.Abs(((ProjectView)d.Content).ActualHeight - sizes[i].Item2) < 1 && ((ProjectView)d.Content).IsVisible).All(x => x),
            "Leaving focus mode restores every panel's size, position and content");

        var file = Path.Combine(view.Project.Entry.Path, "notes.txt"); await File.WriteAllTextAsync(file, "Focus typing");
        await view.OpenFileAsync(file); window.UpdateLayout();
        var editor = view.Editors.Single();
        Check(!PressF(editor.Editor.TextArea).Handled && !window.FocusModeActive, "F remains available for typing in the file editor");
        window.Activate(); editor.Editor.Focus();
        view.ProjectTitle.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        Check(ReferenceEquals(Keyboard.FocusedElement, view), "Clicking the panel header leaves the editor ready for the F shortcut");
        view.ShowSearch(); window.UpdateLayout();
        var search = Descendants<TextBox>(view.Search).First();
        Check(!PressF(search).Handled && !window.FocusModeActive, "F remains available in the project search field");
        window.BottomTabs.SelectedIndex = 1; window.UpdateLayout();
        Check(!PressF(window.OperationOutput).Handled, "F does not toggle focus mode from task output");
        window.BottomTabs.SelectedIndex = 0; window.UpdateLayout();
        Check(!PressF(window.TerminalHost.Children.OfType<TerminalControl>().Single()).Handled, "F does not toggle focus mode from terminal input");
        view.ShowExplorer();
        PressF(view.ProjectTitle);
        Check(!PressF(editor.Editor.TextArea).Handled && window.FocusModeActive, "Typing F while focused keeps focus mode enabled");
        var second = documents.First(d => d != first); second.IsSelected = true; second.IsActive = true; window.UpdateLayout();
        Check(!window.FocusModeActive && documents.All(d => ((ProjectView)d.Content).IsVisible) && view.ProjectFrame.Effect is null,
            "Switching projects exits focus mode and moves the active border");
        PressF(((ProjectView)second.Content).ProjectTitle);
        window.SplitWithProject(second, Orientation.Horizontal, window.Projects[0]); await Task.Delay(50); window.UpdateLayout();
        Check(!window.FocusModeActive && window.Documents().All(d => ((ProjectView)d.Content).IsVisible), "Splitting during focus mode restores the normal workspace");

        var floating = window.OpenProjectWindow(window.Projects[0]); await Task.Delay(200); window.UpdateLayout();
        var floatingView = (ProjectView)floating.Content;
        var floatingWindow = Application.Current.Windows.OfType<AvalonDock.Controls.LayoutFloatingWindowControl>().Single(w => w.Model.Descendents().Contains(floating));
        var floatingState = floatingWindow.WindowState;
        floatingView.ProjectTitle.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        var floatingKey = PressF(floatingView.ProjectTitle);
        Check(floatingKey.Handled && window.FocusModeActive && floatingWindow.WindowState == WindowState.Maximized,
            "F works in floating project windows and maximizes the active window");
        PressF(floatingView.ProjectTitle);
        await Task.Delay(50); window.UpdateLayout();
        Check(!window.FocusModeActive && floatingWindow.WindowState == floatingState, "F restores the floating window's previous state");
        PressF(floatingView.ProjectTitle); floating.Close(); await Task.Delay(200); window.UpdateLayout();
        Check(!window.FocusModeActive && window.Documents().All(d => ((ProjectView)d.Content).IsVisible), "Closing the focused floating project restores focus state safely");
        File.WriteAllLines(Path.Combine(OutputDirectory, "focus-mode-results.txt"), Results);
    }
}
