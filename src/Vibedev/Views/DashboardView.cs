using System.Windows;
using System.Windows.Controls;
using Vibedev.Core;

namespace Vibedev.Views;

public sealed class ProjectState(ProjectEntry entry)
{
    public ProjectEntry Entry { get; } = entry;
    public GitStatus? Git { get; set; }
    public string Activity { get; set; } = "";
    public bool Failed { get; set; }
    public bool Busy { get; set; }
}

public sealed class DashboardView : UserControl
{
    private readonly MainWindow main;
    internal IReadOnlyList<ProjectState> DisplayedProjects { get; private set; } = [];
    public DashboardView(MainWindow main) { this.main = main; Refresh(); }
    public void Refresh()
    {
        var body = new DockPanel { Margin = new(12), VerticalAlignment = VerticalAlignment.Top };
        var pullAll = Ui.Button("↓ Pull all", () => main.PullAll(), true); pullAll.IsEnabled = !main.IsPulling; pullAll.ToolTip = "Pull every registered project, including closed projects";
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
            var row = new Grid(); for (var column = 0; column < 3; column++) row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var name = Ui.Text(project.Entry.Name, 12); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; name.MaxWidth = 180; name.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(name);
            var branch = Ui.Text(project.Git?.Branch ?? "Local project", 11, "#777784"); branch.TextWrapping = TextWrapping.NoWrap; branch.TextTrimming = TextTrimming.CharacterEllipsis; branch.MaxWidth = 140; branch.VerticalAlignment = VerticalAlignment.Center; branch.Margin = new(8, 0, 8, 0); Grid.SetColumn(branch, 1); row.Children.Add(branch);
            var state = project.Busy ? project.Activity : project.Failed ? "Failed" : project.Git?.Changes > 0 ? $"● {project.Git.Changes} changes" : project.Git?.IsRepository == true ? "Clean" : "Local";
            var status = Ui.Text(state, 11, project.Failed ? "#F87171" : project.Git?.Changes > 0 ? "#FBBF24" : "#9898A6"); status.TextWrapping = TextWrapping.NoWrap; status.TextTrimming = TextTrimming.CharacterEllipsis; status.MaxWidth = 140; status.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(status, 2); row.Children.Add(status);
            var frame = Ui.Card(row, new Thickness(10, 8, 10, 8)); frame.CornerRadius = new(4); frame.BorderBrush = Ui.Brush("#605272");
            list.Items.Add(new ListBoxItem { Content = frame, Tag = project, Margin = new(0, 0, 8, 0), Padding = new(2), ToolTip = project.Entry.Path + (project.Activity.Length > 0 ? "\n" + project.Activity : "") });
        }
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is ListBoxItem { Tag: ProjectState project }) main.OpenProject(project); };
        if (DisplayedProjects.Count == 0) { var empty = Ui.Text("Open a project from the sidebar or add a project.", 12, "#777784"); empty.VerticalAlignment = VerticalAlignment.Center; body.Children.Add(empty); } else body.Children.Add(list);
        Content = body;
    }
}
