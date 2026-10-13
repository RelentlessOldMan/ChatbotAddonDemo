using System.ComponentModel;
using Demo.Capabilities;
using Demo.Domain;

namespace Demo.Desktop;

/// <summary>What the user is looking at right now. Updated by the UI, read from any thread.</summary>
public sealed class SelectionContext
{
    private volatile WorkOrder? _selectedOrder;
    private volatile string _activeTab = "Work orders";

    public WorkOrder? SelectedOrder { get => _selectedOrder; set => _selectedOrder = value; }
    public string ActiveTab { get => _activeTab; set => _activeTab = value; }
}

/// <summary>
/// Capabilities only the running desktop app can offer: live UI context. This is
/// why agents attach to the WPF app over HTTP rather than spawning a fresh process.
/// </summary>
public sealed class AppContextCapabilities(SelectionContext selection)
{
    [Capability("get_current_view", SafetyClass.Read, Title = "See what the user is looking at")]
    [Description("Returns which tab the user has open in the desktop app and the work order they have selected, if any. Use this when the user says 'this order', 'the selected one', etc.")]
    public object GetCurrentView() => new { activeTab = selection.ActiveTab, selectedWorkOrder = selection.SelectedOrder };
}
