using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace Demo.Screenshots;

/// <summary>
/// Drives Demo.Desktop through UI Automation, by AutomationId. Shared by the screenshot
/// tool and the UI tests so both exercise the app the same way a user would.
/// </summary>
public sealed class DesktopDriver : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private readonly Application _app;
    private readonly UIA3Automation _automation;

    private readonly string _snapshotPipe;

    private DesktopDriver(Application app, UIA3Automation automation, string snapshotPipe, Window main)
    {
        _app = app;
        _automation = automation;
        _snapshotPipe = snapshotPipe;
        Main = main;
    }

    public Window Main { get; }

    /// <summary>Launches the app; a snapshot pipe is added so <see cref="Capture"/> works without a visible screen.</summary>
    public static DesktopDriver Launch(string exePath, params string[] args)
    {
        var pipe = $"ChatbotAddonDemo-snapshot-{Guid.NewGuid():N}";
        var app = Application.Launch(new ProcessStartInfo(exePath, [.. args, "--snapshot-pipe", pipe]) { UseShellExecute = false });
        var automation = new UIA3Automation();
        try
        {
            // Not app.GetMainWindow: it waits forever for Process.MainWindowHandle, which stays empty because the
            // unobtrusive window has no taskbar button (WPF gives it a hidden owner). Find it through UIA instead.
            var main = Retry.WhileNull(() => automation.GetDesktop().FindFirstChild(cf => cf.ByProcessId(app.ProcessId).And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window)))?.AsWindow(),
                DefaultTimeout, throwOnTimeout: true, timeoutMessage: "The main window did not appear").Result!;
            return new DesktopDriver(app, automation, pipe, main);
        }
        catch
        {
            // No driver to dispose yet, so don't leave the app running (or the automation object alive) behind.
            try { app.Kill(); } catch { /* already gone */ }
            app.Dispose();
            automation.Dispose();
            throw;
        }
    }

    public AutomationElement Find(string automationId, AutomationElement? root = null, TimeSpan? timeout = null) =>
        Retry.WhileNull(() => (root ?? Main).FindFirstDescendant(cf => cf.ByAutomationId(automationId)), timeout ?? DefaultTimeout, throwOnTimeout: true,
            timeoutMessage: $"Element '{automationId}' not found").Result!;

    public AutomationElement[] FindAll(string automationId, AutomationElement? root = null) =>
        (root ?? Main).FindAllDescendants(cf => cf.ByAutomationId(automationId));

    public void Click(string automationId, AutomationElement? root = null)
    {
        var element = Find(automationId, root);
        Retry.WhileFalse(() => element.IsEnabled, DefaultTimeout, throwOnTimeout: true, timeoutMessage: $"'{automationId}' never became enabled");
        element.AsButton().Invoke();
    }

    public void SetChecked(string automationId, bool value, AutomationElement? root = null)
    {
        var box = Find(automationId, root).AsCheckBox();
        if (box.IsChecked != value) box.Toggle();
    }

    public void SelectTab(string automationId, AutomationElement? root = null) =>
        Find(automationId, root).AsTabItem().Select();

    public void Type(string automationId, string text, AutomationElement? root = null)
    {
        var box = Find(automationId, root).AsTextBox();
        box.Text = text;
    }

    /// <summary>Selects the grid row whose first cell equals <paramref name="key"/>.</summary>
    public void SelectRow(string gridId, string key)
    {
        var grid = Find(gridId).AsDataGridView();
        var row = Retry.WhileNull(() => grid.Rows.FirstOrDefault(r => r.Cells[0].Value == key), DefaultTimeout, throwOnTimeout: true).Result!;
        row.Patterns.SelectionItem.Pattern.Select();
    }

    public string GridCell(string gridId, string key, int column) =>
        Find(gridId).AsDataGridView().Rows.First(r => r.Cells[0].Value == key).Cells[column].Value;

    public Window WaitForModal(string automationId) =>
        Retry.WhileNull(() => Main.ModalWindows.FirstOrDefault(w => w.AutomationId == automationId), DefaultTimeout, throwOnTimeout: true,
            timeoutMessage: $"Modal '{automationId}' did not open").Result!;

    public void WaitUntil(Func<bool> condition, string what, TimeSpan? timeout = null) =>
        Retry.WhileFalse(() => { try { return condition(); } catch { return false; } }, timeout ?? DefaultTimeout, throwOnTimeout: true, timeoutMessage: $"Timed out waiting for {what}");

    /// <summary>Asks the app to render <paramref name="window"/> to PNG in-process (requires --demo; see SnapshotServer).</summary>
    public void Capture(Window window, string path)
    {
        using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", _snapshotPipe, System.IO.Pipes.PipeDirection.InOut);
        pipe.Connect((int)DefaultTimeout.TotalMilliseconds);
        using var reader = new StreamReader(pipe);
        using var writer = new StreamWriter(pipe) { AutoFlush = true };
        writer.WriteLine($"{window.AutomationId}|{Path.GetFullPath(path)}");
        var reply = reader.ReadLine();
        if (reply != "ok") throw new InvalidOperationException($"Snapshot of '{window.AutomationId}' failed: {reply}");
    }

    public void Dispose()
    {
        try { Main.Close(); } catch { /* already gone */ }
        try
        {
            using var process = Process.GetProcessById(_app.ProcessId);
            if (!process.WaitForExit(3000)) _app.Kill();
        }
        catch { /* already gone */ }
        _app.Dispose();
        _automation.Dispose();
    }
}
