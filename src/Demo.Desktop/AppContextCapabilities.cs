using System.ComponentModel;
using Demo.Capabilities;

namespace Demo.Desktop;

/// <summary>What the user is looking at right now. Updated by the UI, read from any thread.</summary>
public sealed class SelectionContext
{
    private volatile string _activeTab = "Test plans";
    private volatile string? _selectedPlanId;
    private volatile string? _selectedRunId;
    private volatile string? _shownPlot;

    public string ActiveTab { get => _activeTab; set => _activeTab = value; }
    public string? SelectedPlanId { get => _selectedPlanId; set => _selectedPlanId = value; }
    public string? SelectedRunId { get => _selectedRunId; set => _selectedRunId = value; }
    public string? ShownPlot { get => _shownPlot; set => _shownPlot = value; }
}

/// <summary>
/// A capability only the running desktop app can offer: the user's on-screen context, so
/// "analyze this run" or "run the selected plan" work in the chat panel.
/// </summary>
public sealed class AppContextCapabilities(SelectionContext selection)
{
    public const string Instructions =
        "- When the user says 'this plan', 'this run', 'the selected one' or 'this plot', call get_current_view to see what they have selected.";

    [Capability("get_current_view", SafetyClass.Read, Title = "See what the user is looking at")]
    [Description("Returns which tab the user has open in the desktop app, the selected test plan and run ids, and which plot is shown.")]
    public object GetCurrentView() => new
    {
        activeTab = selection.ActiveTab,
        selectedPlanId = selection.SelectedPlanId,
        selectedRunId = selection.SelectedRunId,
        shownPlot = selection.ShownPlot,
    };
}
