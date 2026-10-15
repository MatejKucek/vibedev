using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace Vibedev.Views;

/// <summary>Line numbers with breathing room, a faint divider and a bright current-line number.</summary>
internal static class EditorGutter
{
    public static void Apply(TextEditor editor)
    {
        editor.ShowLineNumbers = false;
        var numbers = new CurrentLineNumberMargin(editor) { Margin = new(4, 0, 0, 0) }; numbers.SetValue(Control.ForegroundProperty, Ui.Brush("#5A5A66"));
        var divider = new Line { X1 = 0, Y1 = 0, X2 = 0, Y2 = 1, Stretch = Stretch.Fill, Stroke = Ui.Brush("#2A2A30"), StrokeThickness = 1, Margin = new(12, 0, 10, 0) };
        editor.TextArea.LeftMargins.Insert(0, numbers); editor.TextArea.LeftMargins.Insert(1, divider);
        editor.TextArea.Caret.PositionChanged += (_, _) => numbers.InvalidateVisual();
    }

    private sealed class CurrentLineNumberMargin(TextEditor editor) : LineNumberMargin
    {
        private static readonly Brush Current = Ui.Brush("#D4D4DC");
        protected override void OnRender(DrawingContext context)
        {
            var view = TextView;
            if (view is null || !view.VisualLinesValid) return;
            var caret = editor.TextArea.Caret.Line; var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip; var muted = (Brush)GetValue(Control.ForegroundProperty);
            foreach (var line in view.VisualLines)
            {
                var number = line.FirstDocumentLine.LineNumber;
                var text = new FormattedText(number.ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, emSize, number == caret ? Current : muted, dpi);
                var y = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.TextTop);
                context.DrawText(text, new Point(RenderSize.Width - text.Width, y - view.VerticalOffset));
            }
        }
    }
}
