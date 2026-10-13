using System.Windows;
using Demo.Desktop.ViewModels;

namespace Demo.Desktop;

public partial class App : Application
{
    private AppServices? _services;

    /// <summary>
    /// True when driven by the UI tests or the screenshot tool (<c>--demo --snapshot-pipe</c>). Windows then open
    /// off-screen, unactivated and without a taskbar button, so automated runs never steal focus from whatever the
    /// person at the machine is doing. Screenshots still work: they are rendered in-process, not captured from the screen.
    /// </summary>
    public static bool Unobtrusive { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _services = new AppServices(LaunchOptions.Parse(e.Args));
        if (_services.IsDemo && _services.Options.SnapshotPipe is { } pipe)
        {
            Unobtrusive = true;
            SnapshotServer.Start(pipe, Dispatcher);
        }
        var window = new MainWindow(_services, new MainViewModel(_services));
        MainWindow = window;
        MakeUnobtrusive(window);
        window.Show();
    }

    public static void MakeUnobtrusive(Window window)
    {
        if (!Unobtrusive) return;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
    }
}
