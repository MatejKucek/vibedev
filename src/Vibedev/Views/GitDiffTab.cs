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
    public GitDiffTab(string key, string name, string diff, GitFileChange? change, bool staged, Action<GitDiffTab> close)
    {
        Key = key; Change = change; Staged = staged; Style = (Style)Application.Current.FindResource(typeof(TabItem));
        var button = Ui.Button("×", () => close(this)); button.Padding = new(4, 0, 4, 0); button.ToolTip = "Close diff";
        Header = Ui.Row(Ui.Text(name, 11), button); ToolTip = key;
        Editor = new() { Text = diff, IsReadOnly = true, ShowLineNumbers = true, Background = Ui.Brush("#18181B"), Foreground = Ui.Brush("#D4D4DC"), FontFamily = new("Cascadia Code, Consolas"), FontSize = 12, Padding = new(8) };
        Editor.TextArea.TextView.LineTransformers.Add(new DiffColors()); Content = Editor;
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
