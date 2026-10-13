using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Demo.Desktop;

/// <summary>
/// Documentation tooling only: renders a window to PNG on request, so docs screenshots are
/// produced in-process (works with a locked screen or on a build agent, and is pixel-identical
/// everywhere). Started only when launched with <c>--demo --snapshot-pipe &lt;name&gt;</c>.
/// Protocol: client sends "&lt;window AutomationId&gt;|&lt;png path&gt;\n", server replies "ok" or "error: …".
/// </summary>
public static class SnapshotServer
{
    private const double Scale = 1.5;

    public static void Start(string pipeName, Dispatcher dispatcher) => _ = Task.Run(async () =>
    {
        while (true)
        {
            await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync();
            using var reader = new StreamReader(pipe);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            var request = (await reader.ReadLineAsync())?.Split('|', 2);
            var reply = request is [var windowId, var path]
                ? await dispatcher.InvokeAsync(() => Render(windowId, path), DispatcherPriority.ContextIdle)
                : "error: bad request";
            await writer.WriteLineAsync(reply);
        }
    });

    private static string Render(string windowId, string path)
    {
        var window = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => AutomationProperties.GetAutomationId(w) == windowId);
        if (window?.Content is not FrameworkElement content) return $"error: no window '{windowId}'";

        content.UpdateLayout();
        var size = new Size(content.ActualWidth, content.ActualHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(size));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(size));
        }

        var bitmap = new RenderTargetBitmap((int)(size.Width * Scale), (int)(size.Height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return "ok";
    }
}
