using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Demo.Mcp;

namespace Demo.Desktop.ViewModels;

/// <summary>
/// What coding agents connected to this app are doing (path D1). Reads are listed as they happen; a change or bench
/// action shows the same approval card the chat panel uses, and waits for the user. Shown while agents are allowed,
/// whether or not the chat panel is turned on.
/// </summary>
public sealed partial class AgentPanelViewModel(Dispatcher dispatcher) : ObservableObject, IAgentSupervisor
{
    private readonly Dictionary<string, ChatItem> _itemsByCallId = [];

    public ObservableCollection<ChatItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private int _connected;

    /// <summary>Why agents can't connect (e.g. another TestBench window already lets them); null when they can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private string? _problem;

    public string Status => Problem ?? Connected switch
    {
        0 => "Waiting for a coding agent",
        1 => "1 coding agent connected",
        var n => $"{n} coding agents connected",
    };

    [RelayCommand]
    private void Clear()
    {
        foreach (var item in Items.ToList())
        {
            if (item is ApprovalItem { IsPending: true }) continue; // still waiting for the user
            Items.Remove(item);
        }
        foreach (var id in _itemsByCallId.Where(e => !Items.Contains(e.Value)).Select(e => e.Key).ToList()) _itemsByCallId.Remove(id);
    }

    /// <summary>Agents were turned off: cancels anything still waiting for the user, and empties the list.</summary>
    public void Reset()
    {
        foreach (var pending in Items.OfType<ApprovalItem>().Where(a => a.IsPending).ToList()) pending.Cancel();
        Items.Clear();
        _itemsByCallId.Clear();
        Connected = 0;
        Problem = null;
    }

    // The door calls these from its own threads; everything the view binds to changes on the UI thread.

    void IAgentSupervisor.AgentConnected() => dispatcher.BeginInvoke(() => Connected++);

    void IAgentSupervisor.AgentDisconnected() => dispatcher.BeginInvoke(() => Connected = Math.Max(0, Connected - 1));

    async Task<bool> IAgentSupervisor.ApproveAsync(AgentCall call, CancellationToken cancellationToken)
    {
        var item = await dispatcher.InvokeAsync(() =>
        {
            var card = new ApprovalItem(ApprovalText.Heading(call.Agent, call.Capability), call.Capability.Title, ApprovalText.Arguments(call.Arguments));
            _itemsByCallId[call.Id] = card;
            Items.Add(card);
            return card;
        });
        // The agent gave up (or the door is closing): the card says so, and nothing runs.
        await using var _ = cancellationToken.Register(() => dispatcher.BeginInvoke(item.Cancel));
        return await item.Decision;
    }

    void IAgentSupervisor.Ran(AgentCall call) => dispatcher.BeginInvoke(() =>
    {
        switch (_itemsByCallId.GetValueOrDefault(call.Id))
        {
            case ApprovalItem approval:
                approval.IsDone = true;
                break;
            default:
                var tool = new ToolItem($"{call.Agent}: {call.Capability.Title}", call.Capability.Name) { IsDone = true };
                _itemsByCallId[call.Id] = tool;
                Items.Add(tool);
                break;
        }
    });
}
