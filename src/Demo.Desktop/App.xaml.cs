using System.Windows;
using Demo.Desktop.ViewModels;
using Demo.Mcp;

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
        if (e.Args is ["mcp", ..])
        {
            // A coding agent started this copy of the exe: no window, just pass its MCP messages to the copy the
            // user has open (path D1). Exits when either side hangs up.
            var agentPipe = LaunchOptions.Parse(e.Args).AgentPipe ?? AgentDoor.DefaultPipeName;
            Shutdown(Task.Run(() => AgentRelay.RunAsync(agentPipe, Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error, TimeSpan.FromSeconds(3)))
                .GetAwaiter().GetResult());
            return;
        }
        // The AI features are an add-on to a tool people depend on: whatever goes wrong in them (or anywhere in the
        // UI, e.g. a key that can't be saved) is reported, and the tool keeps running.
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            System.Diagnostics.Trace.TraceError(args.Exception.ToString());
            if (!Unobtrusive) // automated runs never put a window in front of the person at the machine
                MessageBox.Show(MainWindow, $"Something went wrong: {args.Exception.Message}", "TestBench", MessageBoxButton.OK, MessageBoxImage.Error);
        };
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
