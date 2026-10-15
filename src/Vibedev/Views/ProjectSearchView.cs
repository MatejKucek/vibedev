using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Vibedev.Core;

namespace Vibedev.Views;

public sealed class ProjectSearchView : UserControl
{
    private readonly ProjectView project;
    private readonly TextBox query = new() { ToolTip = "Search only this project", Tag = "Search files and contents…", Margin = new(0, 0, 0, 8) };
    private readonly ComboBox mode = new() { ItemsSource = new[] { "Files & contents", "Contents", "Filenames" }, SelectedIndex = 0, Margin = new(0, 0, 0, 10), ToolTip = "Choose whether to search filenames, file contents, or both" };
    private readonly TextBlock summary = Ui.Text("Search this project", 11, "#9898A6");
    private readonly ListBox results = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly DispatcherTimer debounce;
    private CancellationTokenSource? currentSearch;
    private int revision;
    internal IReadOnlyList<ProjectSearchHit> DisplayedHits => results.Items.OfType<ProjectSearchHit>().ToArray();
    public ProjectSearchView(ProjectView project)
    {
        this.project = project;
        var root = new DockPanel { Background = Ui.Brush("#1D1D21") };
        var controls = new StackPanel { Margin = new(10, 12, 10, 8) };
        var heading = Ui.Text("FIND IN PROJECT", 10, "#9898A6"); heading.Margin = new(0, 0, 0, 10); controls.Children.Add(heading);
        controls.Children.Add(query); controls.Children.Add(mode); controls.Children.Add(summary);
        DockPanel.SetDock(controls, Dock.Top); root.Children.Add(controls); root.Children.Add(results); Content = root;
        var template = new DataTemplate(typeof(ProjectSearchHit));
        var row = new FrameworkElementFactory(typeof(StackPanel));
        var location = new FrameworkElementFactory(typeof(TextBlock)); location.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ProjectSearchHit.Location)));
        location.SetValue(TextBlock.FontSizeProperty, 11d); location.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); location.SetValue(TextBlock.ForegroundProperty, Ui.Brush("#DDD6EE")); row.AppendChild(location);
        var preview = new FrameworkElementFactory(typeof(TextBlock)); preview.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ProjectSearchHit.Preview)));
        preview.SetValue(TextBlock.FontSizeProperty, 11d); preview.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); preview.SetValue(TextBlock.MarginProperty, new Thickness(0, 5, 0, 0)); preview.SetValue(TextBlock.ForegroundProperty, Ui.Brush("#9898A6")); row.AppendChild(preview);
        template.VisualTree = row; results.ItemTemplate = template;
        debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) }; debounce.Tick += async (_, _) => { debounce.Stop(); await SearchAsync(); };
        query.TextChanged += (_, _) => ScheduleSearch(); mode.SelectionChanged += (_, _) => ScheduleSearch();
        query.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; debounce.Stop(); await SearchAsync(); } if (e.Key == Key.Escape) project.ShowExplorer(); };
        results.MouseDoubleClick += async (_, _) => { if (results.SelectedItem is ProjectSearchHit hit) await project.OpenSearchHitAsync(hit); };
        results.KeyDown += async (_, e) => { if (e.Key == Key.Enter && results.SelectedItem is ProjectSearchHit hit) { e.Handled = true; await project.OpenSearchHitAsync(hit); } };
        Unloaded += (_, _) => { debounce.Stop(); currentSearch?.Cancel(); revision++; };
        Loaded += (_, _) => { if (query.Text.Length > 0 && currentSearch is null && !debounce.IsEnabled) ScheduleSearch(); };
    }
    private void ScheduleSearch()
    {
        revision++; currentSearch?.Cancel(); debounce.Stop();
        results.ItemsSource = null; summary.ToolTip = null;
        if (string.IsNullOrWhiteSpace(query.Text)) { results.ItemsSource = null; summary.Text = "Search this project"; return; }
        summary.Text = "Searching…"; debounce.Start();
    }
    public async Task SearchAsync()
    {
        currentSearch?.Cancel();
        using var search = new CancellationTokenSource(); currentSearch = search;
        var version = ++revision;
        var buffers = project.Editors.ToDictionary(e => e.FilePath, e => e.Editor.Text, StringComparer.OrdinalIgnoreCase);
        try
        {
            var result = await ProjectSearch.FindAsync(project.Project.Entry.Path, query.Text, (ProjectSearchMode)mode.SelectedIndex, buffers, search.Token);
            if (version != revision) return;
            results.ItemsSource = result.Hits;
            summary.Text = result.Truncated ? $"First {result.Hits.Count} matches · refine your search" : $"{result.Hits.Count} matches · {result.FilesSearched} files";
            if (result.Warnings.Count > 0) summary.Text += $" · {result.Warnings.Count} unreadable";
            summary.ToolTip = string.Join(Environment.NewLine, result.Warnings);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == revision) { results.ItemsSource = null; summary.Text = "Search failed: " + ex.Message; } }
        finally { if (ReferenceEquals(currentSearch, search)) currentSearch = null; }
    }
    public void FocusSearch(bool filenames = false)
    {
        if (filenames) mode.SelectedIndex = (int)ProjectSearchMode.FileNames;
        _ = Dispatcher.BeginInvoke(() => { query.Focus(); query.SelectAll(); }, DispatcherPriority.Input);
    }
    internal async Task<IReadOnlyList<ProjectSearchHit>> FindForTestAsync(string text, ProjectSearchMode searchMode)
    {
        query.Text = text; mode.SelectedIndex = (int)searchMode; debounce.Stop(); await SearchAsync();
        return DisplayedHits;
    }
}
