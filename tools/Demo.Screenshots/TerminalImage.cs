using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Demo.Screenshots;

/// <summary>
/// Renders real console output as a terminal-style PNG. Deterministic, unlike capturing
/// whichever terminal app the machine happens to use. Must be called on an STA thread.
/// </summary>
public static class TerminalImage
{
    public static void Save(string title, string text, string path, double width = 980)
    {
        var titleBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2F, 0x3A)),
            Padding = new Thickness(14, 8, 14, 8),
            Child = new TextBlock { Text = title, Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xD1, 0xD9)), FontFamily = new FontFamily("Segoe UI"), FontSize = 13 },
        };
        var body = new TextBlock
        {
            Text = text.TrimEnd(),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3)),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 12, 16, 16),
        };
        var root = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1B, 0x22)),
            CornerRadius = new CornerRadius(8),
            Width = width,
            Child = new StackPanel { Children = { titleBar, body } },
        };

        root.Measure(new Size(width, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();

        const double dpi = 144; // crisp on high-DPI displays and in docs
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi / 96), (int)Math.Ceiling(root.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(root);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
