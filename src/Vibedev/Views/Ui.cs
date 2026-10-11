using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Vibedev.Views;

public static class Ui
{
    public static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    public static TextBlock Text(string text, double size = 13, string color = "#E4E4E7") => new() { Text = text, FontSize = size, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap };
    public static Button Button(string title, Action action, bool accent = false)
    {
        var b = new Button { Content = title, ToolTip = title switch { "×" => "Close", "Split →" => "Split the project panel horizontally", "Split ↓" => "Split the project panel vertically", "↗ Float" => "Detach this project into a floating window", "▶ Run" => "Save files and run the selected launch configuration", _ => title } };
        if (accent) { b.Background = Brush("#6D4BC3"); b.BorderBrush = Brush("#8B6DD0"); b.Foreground = Brushes.White; }
        b.Click += (_, _) => action(); return b;
    }
    public static Border Card(UIElement content, Thickness? padding = null) => new() { Background = Brush("#202024"), BorderBrush = Brush("#34343A"), BorderThickness = new(1), CornerRadius = new(8), Padding = padding ?? new Thickness(20), Child = content };
    public static StackPanel Row(params UIElement[] items)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal }; foreach (var item in items) panel.Children.Add(item); return panel;
    }
    public static void Error(string message, Window? owner = null) => MessageBox.Show(owner ?? Application.Current.MainWindow, message, "Vibedev", MessageBoxButton.OK, MessageBoxImage.Error);
    public static string? Prompt(Window owner, string title, string description, string initial = "")
    {
        var dialog = new Window { Title = title, Owner = owner, Width = 520, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var body = new StackPanel { Margin = new(24) }; body.Children.Add(Text(description));
        var input = new TextBox { Text = initial, Margin = new(0, 16, 0, 20), MinWidth = 440 }; body.Children.Add(input);
        var ok = Button(title, () => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; }, true); ok.IsDefault = true;
        var cancel = Button("Cancel", () => dialog.DialogResult = false); cancel.IsCancel = true;
        var row = Row(cancel, ok); row.HorizontalAlignment = HorizontalAlignment.Right; body.Children.Add(row);
        dialog.Content = body; dialog.Loaded += (_, _) => input.Focus(); return dialog.ShowDialog() == true ? input.Text : null;
    }
}
