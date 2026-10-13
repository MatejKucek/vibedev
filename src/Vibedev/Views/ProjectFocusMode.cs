using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AvalonDock.Controls;

namespace Vibedev.Views;

// Animate layout values so AvalonDock's bindings and saved split sizes stay intact.
internal sealed class ProjectFocusMode : IDisposable
{
    private readonly List<ColumnDefinition> columns = [];
    private readonly List<RowDefinition> rows = [];
    private readonly List<(UIElement Element, Visibility Visibility)> hidden = [];

    public ProjectFocusMode(ProjectView view)
    {
        DependencyObject child = view;
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null; child = parent, parent = VisualTreeHelper.GetParent(child))
        {
            if (parent is not Grid grid || parent is not (LayoutDocumentPaneGroupControl or LayoutPanelControl)) continue;
            if (child is not UIElement selected) continue;
            foreach (UIElement sibling in grid.Children)
            {
                if (sibling == selected) continue;
                hidden.Add((sibling, sibling.Visibility));
                sibling.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            }
            if (grid.ColumnDefinitions.Count > 1)
                for (var i = 0; i < grid.ColumnDefinitions.Count; i++)
                {
                    var column = grid.ColumnDefinitions[i]; columns.Add(column);
                    column.BeginAnimation(ColumnDefinition.MinWidthProperty, new DoubleAnimation(0, TimeSpan.Zero));
                    column.BeginAnimation(ColumnDefinition.WidthProperty, new FocusLengthAnimation(i == Grid.GetColumn(selected) ? new(1, GridUnitType.Star) : new(0)));
                }
            if (grid.RowDefinitions.Count > 1)
                for (var i = 0; i < grid.RowDefinitions.Count; i++)
                {
                    var row = grid.RowDefinitions[i]; rows.Add(row);
                    row.BeginAnimation(RowDefinition.MinHeightProperty, new DoubleAnimation(0, TimeSpan.Zero));
                    row.BeginAnimation(RowDefinition.HeightProperty, new FocusLengthAnimation(i == Grid.GetRow(selected) ? new(1, GridUnitType.Star) : new(0)));
                }
        }
    }

    public void Dispose()
    {
        foreach (var column in columns)
        {
            column.BeginAnimation(ColumnDefinition.WidthProperty, null);
            column.BeginAnimation(ColumnDefinition.MinWidthProperty, null);
        }
        foreach (var row in rows)
        {
            row.BeginAnimation(RowDefinition.HeightProperty, null);
            row.BeginAnimation(RowDefinition.MinHeightProperty, null);
        }
        foreach (var (element, visibility) in hidden) element.SetCurrentValue(UIElement.VisibilityProperty, visibility);
    }

    private sealed class FocusLengthAnimation(GridLength value) : AnimationTimeline
    {
        public override Type TargetPropertyType => typeof(GridLength);
        protected override Freezable CreateInstanceCore() => new FocusLengthAnimation(value);
        public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock) => value;
    }
}
