using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Shapes;
using Vibedev.Core;

namespace Vibedev.Views;

public sealed class ProjectState(ProjectEntry entry)
{
    public ProjectEntry Entry { get; } = entry;
    public GitStatus? Git { get; set; }
    public string Activity { get; set; } = "";
    public bool Failed { get; set; }
    public bool Busy { get; set; }
    public int OpenTaskCount { get; set; }
    public PullState Pull { get; set; }
    public ProjectClaudeActivity Claude { get; } = new();
}

/// <summary>Result of the latest "pull all" run, shown next to each project in the sidebar.</summary>
public enum PullState { None, Queued, Pulling, Succeeded, Failed }

public sealed class DashboardView : UserControl
{
    private readonly MainWindow main;
    internal IReadOnlyList<ProjectState> DisplayedProjects { get; private set; } = [];
    public DashboardView(MainWindow main)
    {
        this.main = main;
        ContextMenu = new ContextMenu { Style = (Style)main.FindResource("ProjectContextMenu") };
        ContextMenu.Opened += (_, _) =>
        {
            ContextMenu.Items.Clear();
            foreach (var (label, action) in new (string, Action)[] {
                ("Add project…", main.AddProject), ("Refresh statuses", main.RefreshStatuses),
                ("Pull all projects", () => main.PullAll()), ("Hide dashboard", main.ToggleDashboard) })
            {
                var item = new MenuItem { Header = label, Style = (Style)main.FindResource("ProjectMenuItem"), IsEnabled = label != "Pull all projects" || !main.IsPulling };
                item.Click += (_, _) => action(); ContextMenu.Items.Add(item);
            }
        };
        Refresh();
    }
    public void Refresh()
    {
        var body = new DockPanel { Margin = new(12), VerticalAlignment = VerticalAlignment.Top };
        var pullAll = Ui.Button("↓ Pull all", () => main.PullAll(), true); pullAll.IsEnabled = !main.IsPulling && main.Projects.Count > 0; pullAll.ToolTip = "Pull every registered project, including closed projects";
        var actions = Ui.Row(Ui.Button("+ Add project", main.AddProject), Ui.Button("↻", main.RefreshStatuses), pullAll);
        ((Button)actions.Children[1]).ToolTip = "Refresh all project statuses"; actions.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(actions, Dock.Right); body.Children.Add(actions);
        DisplayedProjects = main.OpenProjects;
        var list = new ListBox { VerticalAlignment = VerticalAlignment.Center };
        var projectsPanel = new FrameworkElementFactory(typeof(StackPanel));
        projectsPanel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        list.ItemsPanel = new ItemsPanelTemplate(projectsPanel);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        for (var index = 0; index < DisplayedProjects.Count; index++)
        {
            var project = DisplayedProjects[index];
            var row = new Grid(); for (var column = 0; column < 4; column++) row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var light = project.Claude.Light(project.OpenTaskCount);
            var description = project.Claude.Description(project.OpenTaskCount);
            var indicator = new Ellipse { Width = 10, Height = 10, Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                Tag = light, ToolTip = description, Fill = Ui.Brush(light switch {
                    ClaudeActivityLight.Red => "#F87171", ClaudeActivityLight.Blue => "#60A5FA",
                    ClaudeActivityLight.Orange => "#FB923C", ClaudeActivityLight.Green => "#4ADE80", _ => "#777784" }) };
            AutomationProperties.SetName(indicator, project.Entry.Name + ": " + description);
            row.Children.Add(indicator);
            var name = Ui.Text(project.Entry.Name, 12); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; name.MaxWidth = 180; name.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(name, 1); row.Children.Add(name);
            var branch = Ui.Text(project.Git?.Summary ?? "Local project", 11, "#A1A1AA"); branch.TextWrapping = TextWrapping.NoWrap; branch.TextTrimming = TextTrimming.CharacterEllipsis; branch.MaxWidth = 140; branch.VerticalAlignment = VerticalAlignment.Center; branch.Margin = new(8, 0, 8, 0); branch.ToolTip = project.Git?.Branch; Grid.SetColumn(branch, 2); row.Children.Add(branch);
            var state = project.Busy ? project.Activity : project.Failed ? "Failed" : project.Git?.Changes > 0 ? $"● {project.Git.Changes} changes" : project.Git?.IsRepository == true ? "Clean" : "Local";
            var status = Ui.Text(state, 11, project.Failed ? "#F87171" : project.Git?.Changes > 0 ? "#FBBF24" : "#9898A6"); status.TextWrapping = TextWrapping.NoWrap; status.TextTrimming = TextTrimming.CharacterEllipsis; status.MaxWidth = 140; status.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(status, 3); row.Children.Add(status);
            var frame = Ui.Card(row, new Thickness(10, 8, 10, 8)); frame.CornerRadius = new(4); frame.BorderBrush = Ui.Brush("#605272");
            var item = new ListBoxItem { Content = frame, Tag = project, Margin = new(0, 0, 8, 0), Padding = new(2), ContextMenu = main.CreateProjectContextMenu(project), ToolTip = project.Entry.Path + (project.Activity.Length > 0 ? "\n" + project.Activity : "") };
            item.PreviewMouseRightButtonDown += (_, e) => e.Handled = true;
            list.Items.Add(item);
        }
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is ListBoxItem { Tag: ProjectState project }) main.OpenProject(project); };
        if (DisplayedProjects.Count == 0) { var empty = Ui.Text("Open a project from the sidebar or add a project.", 12, "#777784"); empty.VerticalAlignment = VerticalAlignment.Center; body.Children.Add(empty); } else body.Children.Add(list);
        Content = body;
    }
}
