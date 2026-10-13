using System.Windows;
using Demo.Desktop.ViewModels;

namespace Demo.Desktop;

public partial class App : Application
{
    private AppServices? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _services = new AppServices(LaunchOptions.Parse(e.Args));
        if (_services.IsDemo && _services.Options.SnapshotPipe is { } pipe)
            SnapshotServer.Start(pipe, Dispatcher);
        var window = new MainWindow(_services, new MainViewModel(_services));
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Mcp.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        base.OnExit(e);
    }
}
