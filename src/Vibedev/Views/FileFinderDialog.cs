using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Vibedev.Core;

namespace Vibedev.Views;

/// <summary>Quick-open popup: fuzzy filename search with Up/Down navigation, Enter to open and a *.cs filter.</summary>
public sealed class FileFinderDialog : Window
{
    private const int MaxResults = 100;
    private static readonly (string Label, string? Extension)[] Filters = [("All files", null), ("*.cs", ".cs")];
    private readonly TextBox query = new() { Tag = "Find file…  (e.g. fipl → FilePlanner)", Margin = new(0, 0, 8, 0) };
    private readonly ComboBox filter = new() { ItemsSource = Filters.Select(f => f.Label).ToArray(), SelectedIndex = 0, MinWidth = 100, ToolTip = "Limit results to a file type" };
    private readonly ListBox results = new() { MaxHeight = 360, Margin = new(0, 8, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock summary = Ui.Text("Loading files…", 11, "#9898A6");
    private IReadOnlyList<string> files = [];

    public string? SelectedPath { get; private set; }
    internal IReadOnlyList<string> DisplayedPaths => results.Items.OfType<string>().ToArray();

    public FileFinderDialog(Window owner, string root)
    {
        Owner = owner; Width = 640; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        // The app's implicit Window style does not apply to subclasses, so a Window subclass is white unless colored explicitly.
        Background = Ui.Brush("#24242A"); Foreground = Ui.Brush("#E4E4E7"); FontFamily = new("Segoe UI"); FontSize = 13;
        var top = new Grid(); top.ColumnDefinitions.Add(new()); top.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        top.Children.Add(query); Grid.SetColumn(filter, 1); top.Children.Add(filter);
        var body = new StackPanel { Margin = new(14) };
        body.Children.Add(top); body.Children.Add(results); summary.Margin = new(0, 8, 0, 0); body.Children.Add(summary);
        Content = new Border { BorderBrush = Ui.Brush("#605272"), BorderThickness = new(1), Child = body };
        results.ItemTemplate = Template();
        results.MouseDoubleClick += (_, _) => Confirm();
        query.TextChanged += (_, _) => Refresh(); filter.SelectionChanged += (_, _) => Refresh();
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => { if (DialogResult is null && IsVisible && !filter.IsDropDownOpen) Close(); };
        Loaded += async (_, _) =>
        {
            query.Focus();
            try { files = await ProjectSearch.ListFilesAsync(root); } catch (Exception ex) { summary.Text = "Cannot list files: " + ex.Message; return; }
            Refresh();
        };
    }

    private static DataTemplate Template()
    {
        var template = new DataTemplate(typeof(string));
        var row = new FrameworkElementFactory(typeof(TextBlock));
        row.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("."));
        row.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        row.SetValue(TextBlock.ForegroundProperty, Ui.Brush("#DDD6EE"));
        template.VisualTree = row; return template;
    }

    internal void SetFilter(int index) => filter.SelectedIndex = index;
    internal void SetQuery(string text) => query.Text = text;
    internal void ConfirmForTest() => Confirm();

    private void Refresh()
    {
        if (files.Count == 0) return;
        var extension = Filters[Math.Max(0, filter.SelectedIndex)].Extension;
        var candidates = extension is null ? files : files.Where(f => f.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
        var ranked = query.Text.Trim().Length == 0 ? candidates.Take(MaxResults).ToArray() : FuzzyMatch.Rank(candidates, query.Text, MaxResults);
        results.ItemsSource = ranked;
        if (ranked.Count > 0) results.SelectedIndex = 0;
        summary.Text = ranked.Count == 0 ? "No matching files" : "↑↓ navigate · Enter open · Esc close";
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: e.Handled = true; Close(); break;
            case Key.Enter when !filter.IsDropDownOpen: e.Handled = true; Confirm(); break;
            case Key.Down when !filter.IsFocused: e.Handled = true; Move(1); break;
            case Key.Up when !filter.IsFocused: e.Handled = true; Move(-1); break;
        }
    }

    private void Move(int delta)
    {
        if (results.Items.Count == 0) return;
        results.SelectedIndex = Math.Clamp(results.SelectedIndex + delta, 0, results.Items.Count - 1);
        results.ScrollIntoView(results.SelectedItem);
    }

    private void Confirm()
    {
        if (results.SelectedItem is not string path) return;
        SelectedPath = path; DialogResult = true;
    }
}
