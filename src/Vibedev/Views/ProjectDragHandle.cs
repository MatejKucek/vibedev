using System.Windows;
using System.Windows.Input;

namespace Vibedev.Views;

internal static class ProjectDragHandle
{
    internal static bool PassedThreshold(Point start, Point current) =>
        Math.Abs(current.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance ||
        Math.Abs(current.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;

    public static void Attach(FrameworkElement handle, Action<Point> drag, Action? click = null)
    {
        Point? origin = null;
        handle.Cursor = Cursors.SizeAll;
        handle.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!handle.CaptureMouse()) return;
            origin = e.GetPosition(handle);
            e.Handled = true;
        };
        handle.PreviewMouseMove += (_, e) =>
        {
            if (origin is not { } start) return;
            if (e.LeftButton != MouseButtonState.Pressed) { Reset(); return; }
            var position = e.GetPosition(handle);
            if (!PassedThreshold(start, position)) return;
            var screenPosition = handle.PointToScreen(position);
            Reset();
            e.Handled = true;
            drag(screenPosition);
        };
        handle.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (origin is null) return;
            var inside = new Rect(handle.RenderSize).Contains(e.GetPosition(handle));
            Reset();
            e.Handled = true;
            if (inside) click?.Invoke();
        };
        handle.LostMouseCapture += (_, _) => origin = null;
        handle.Unloaded += (_, _) => Reset();

        void Reset()
        {
            origin = null;
            if (handle.IsMouseCaptured) handle.ReleaseMouseCapture();
        }
    }
}
