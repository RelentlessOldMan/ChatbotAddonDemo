using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Demo.Domain;

namespace Demo.Desktop.ViewModels;

/// <summary>The "legacy" main window. The AI pieces are additive: Chat is null until opted in.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;

    public MainViewModel(AppServices services)
    {
        _services = services;
        // Agents and the assistant change data from background threads; marshal refreshes to the UI.
        services.Workshop.Changed += (_, _) => Application.Current.Dispatcher.BeginInvoke(Refresh);
        Refresh();
        ApplySettings();
    }

    public McpController Mcp => _services.Mcp;
    public string DataPath => _services.DataPath;
    public string Title => _services.IsDemo ? "Workshop Manager 7.2 — DEMO MODE" : "Workshop Manager 7.2";

    /// <summary>Raised when the view should show the Settings dialog.</summary>
    public event Action? SettingsRequested;

    public ObservableCollection<WorkOrder> Orders { get; } = [];
    public ObservableCollection<Part> Parts { get; } = [];
    public IReadOnlyList<Priority> Priorities { get; } = Enum.GetValues<Priority>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetPriorityCommand), nameof(CompleteOrderCommand))]
    private WorkOrder? _selectedOrder;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AdjustStockCommand))]
    private Part? _selectedPart;

    [ObservableProperty] private Priority _editPriority;
    [ObservableProperty] private string _completionNote = "";
    [ObservableProperty] private int _stockDelta = 1;
    [ObservableProperty] private string _stockReason = "";
    [ObservableProperty] private int _selectedTabIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChatVisible), nameof(IsChatAvailable))]
    private ChatViewModel? _chat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChatVisible))]
    private bool _isChatOpen = true;

    public bool IsChatAvailable => Chat is not null;
    public bool IsChatVisible => Chat is not null && IsChatOpen;

    partial void OnSelectedOrderChanged(WorkOrder? value)
    {
        _services.Selection.SelectedOrder = value;
        if (value is not null) EditPriority = value.Priority;
    }

    partial void OnSelectedTabIndexChanged(int value) =>
        _services.Selection.ActiveTab = value == 0 ? "Work orders" : "Parts";

    [RelayCommand]
    private void ToggleChat() => IsChatOpen = !IsChatOpen;

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke();

    /// <summary>Re-reads settings after the Settings dialog closes: shows/hides chat and restarts agent access.</summary>
    public void ApplySettings()
    {
        var settings = _services.Settings.Load();
        Chat = settings.ChatEnabled ? new ChatViewModel(_services, settings, OpenSettings) : null;
        IsChatOpen = true;
        _ = _services.Mcp.ApplyAsync();
    }

    private bool HasOpenSelection() => SelectedOrder is { Status: not OrderStatus.Completed };

    [RelayCommand(CanExecute = nameof(HasOpenSelection))]
    private void SetPriority() => Try(() => _services.Workshop.SetPriority(SelectedOrder!.Id, EditPriority));

    [RelayCommand(CanExecute = nameof(HasOpenSelection))]
    private void CompleteOrder()
    {
        Try(() => _services.Workshop.CompleteOrder(SelectedOrder!.Id, CompletionNote));
        CompletionNote = "";
    }

    private bool HasPart() => SelectedPart is not null;

    [RelayCommand(CanExecute = nameof(HasPart))]
    private void AdjustStock()
    {
        Try(() => _services.Workshop.AdjustStock(SelectedPart!.Sku, StockDelta, StockReason));
        StockReason = "";
    }

    private void Refresh()
    {
        var selectedId = SelectedOrder?.Id;
        var selectedSku = SelectedPart?.Sku;
        var data = _services.Workshop.Snapshot;
        Replace(Orders, data.Orders);
        Replace(Parts, data.Parts);
        SelectedOrder = Orders.FirstOrDefault(o => o.Id == selectedId);
        SelectedPart = Parts.FirstOrDefault(p => p.Sku == selectedSku);
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    private static void Try(Action action)
    {
        try { action(); }
        catch (DomainException ex) { MessageBox.Show(ex.Message, "Workshop Manager", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
