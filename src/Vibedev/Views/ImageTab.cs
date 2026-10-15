using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Vibedev.Views;

internal sealed class ImageTab : TabItem
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".tif", ".tiff" };
    public static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path));
    public string FilePath { get; }

    public ImageTab(string path, Action<ImageTab> close)
    {
        FilePath = path; Style = (Style)Application.Current.FindResource(typeof(TabItem));
        var icon = Ui.Text("▣", 12, "#A78BFA"); icon.Margin = new(0, 0, 6, 0); icon.ToolTip = "Image";
        var title = new TextBlock { Text = Path.GetFileName(path), FontSize = 12 };
        title.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = this });
        var row = Ui.Row(icon, title);
        var button = Ui.Button("×", () => close(this)); button.Padding = new(5, 0, 5, 0); button.Margin = new(10, 0, 0, 0); button.BorderThickness = new(0); button.Background = Brushes.Transparent; button.ToolTip = "Close image";
        row.Children.Add(button); Header = row; ToolTip = path;

        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze();
        var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new(16), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var info = Ui.Text($"{bitmap.PixelWidth} × {bitmap.PixelHeight} · {new FileInfo(path).Length / 1024.0:0.#} KB", 11, "#9898A6"); info.Margin = new(12, 6, 12, 6);
        var grid = new Grid { Background = Ui.Brush("#18181B") };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new());
        grid.Children.Add(info); Grid.SetRow(image, 1); grid.Children.Add(image);
        Content = grid;
    }
}
