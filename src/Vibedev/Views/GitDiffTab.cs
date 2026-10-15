using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;
using Vibedev.Core;

namespace Vibedev.Views;

internal sealed class GitDiffTab : TabItem
{
    public string Key { get; }
    public GitFileChange? Change { get; }
    public bool Staged { get; }
    internal TextEditor Editor { get; }
    public GitDiffTab(string key, string name, string? detail, string diff, GitFileChange? change, bool staged, Action<GitDiffTab> close)
    {
        Key = key; Change = change; Staged = staged; Style = (Style)Application.Current.FindResource(typeof(TabItem));
        var icon = Ui.Text("±", 12, "#A78BFA"); icon.Margin = new(0, 0, 6, 0); icon.ToolTip = "Diff";
        // The title dims with the tab's selected state like file tabs.
        var title = new TextBlock { Text = name, FontSize = 12 };
        title.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = this });
        var row = Ui.Row(icon, title);
        if (detail is not null) { var suffix = Ui.Text(detail, 11, "#9898A6"); suffix.Margin = new(6, 0, 0, 0); suffix.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(suffix); }
        var button = Ui.Button("×", () => close(this)); button.Padding = new(5, 0, 5, 0); button.Margin = new(10, 0, 0, 0); button.BorderThickness = new(0); button.Background = Brushes.Transparent; button.ToolTip = "Close diff";
        row.Children.Add(button); Header = row; ToolTip = key;
        Editor = new() { Text = diff, IsReadOnly = true, Background = Ui.Brush("#18181B"), Foreground = Ui.Brush("#D4D4DC"), FontFamily = new("Cascadia Code, Consolas"), FontSize = 12, Padding = new(8) };
        EditorGutter.Apply(Editor); Editor.TextArea.TextView.LineTransformers.Add(new DiffColors()); Content = Editor;
    }
    private sealed class DiffColors : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(ICSharpCode.AvalonEdit.Document.DocumentLine line)
        {
            if (line.Length == 0) return;
            var text = CurrentContext.Document.GetText(line);
            var color = text.StartsWith('+') ? "#4ADE80" : text.StartsWith('-') ? "#F87171" : text.StartsWith("@@", StringComparison.Ordinal) ? "#A78BFA" : null;
            if (color is not null) ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(Ui.Brush(color)));
        }
    }
}
