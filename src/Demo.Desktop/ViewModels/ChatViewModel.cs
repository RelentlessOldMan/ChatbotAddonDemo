using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Demo.Ai;
using Demo.Mcp;
using Microsoft.Extensions.AI;

namespace Demo.Desktop.ViewModels;

/// <summary>The opt-in assistant panel. Created only when the user has enabled chat in Settings.</summary>
public sealed partial class ChatViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly AiSettings _settings;
    private readonly Dictionary<string, ChatItem> _itemsByCallId = [];
    private ChatSession? _session;
    private HubTools? _tools;
    private Action? _detachSession;
    private CancellationTokenSource? _cts;
    private int _generation;

    public ChatViewModel(AppServices services, AiSettings settings, Action openSettings)
    {
        _services = services;
        _settings = settings;
        OpenSettingsCommand = new RelayCommand(openSettings);

        var info = ProviderInfo.For(settings.Provider);
        Model = settings.ModelFor(settings.Provider);
        var credential = services.ResolveCredential(settings.Provider);
        NeedsSetup = !services.IsDemo && !credential.IsAvailable;
        Header = services.IsDemo ? "Assistant · demo mode" : $"Assistant · {info.DisplayName}";
        ConnectionDescription = services.IsDemo
            ? "Scripted replies; no key needed and nothing leaves this PC."
            : $"{Model} · {credential.Describe()}";
        UsageSummary = "No usage yet";
    }

    public string Header { get; }
    public string Model { get; }
    public string ConnectionDescription { get; }
    public bool NeedsSetup { get; }
    public IRelayCommand OpenSettingsCommand { get; }
    public ObservableCollection<ChatItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _input = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _usageSummary;

    /// <summary>What the panel is waiting on while busy: the model, or the user's decision.</summary>
    [ObservableProperty] private string _activity = "Thinking…";

    private bool CanSend() => !IsBusy && !NeedsSetup && !string.IsNullOrWhiteSpace(Input);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Input.Trim();
        Input = "";
        IsBusy = true;
        // New chat or closing the panel bumps the generation; a request from an older one then finishes silently.
        var generation = _generation;
        using var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            _session ??= await CreateSessionAsync(generation, cts.Token);
            await _session.SendAsync(text, (request, ct) => ApproveAsync(request, generation, ct), cts.Token);
        }
        catch (Exception) when (generation != _generation)
        {
            // This chat was cleared or closed while the request was in flight: nothing left to report to.
        }
        catch (OperationCanceledException)
        {
            Items.Add(new NoticeItem("Stopped."));
        }
        catch (Exception ex) when (ex is SpendCapExceededException or TurnRequestLimitException)
        {
            Items.Add(new NoticeItem(ex.Message, isError: true));
        }
        catch (Exception ex)
        {
            // Provider errors (bad key, rate limit, outage) must never take down the legacy app.
            Items.Add(new NoticeItem($"The assistant couldn't respond: {ex.GetBaseException().Message}", isError: true));
        }
        finally
        {
            if (generation == _generation)
            {
                CancelPendingApprovals();
                IsBusy = false;
                _cts = null;
            }
        }
    }

    private bool CanStop() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void NewChat()
    {
        EndSession();
        _itemsByCallId.Clear();
        Items.Clear();
        UsageSummary = "No usage yet";
    }

    /// <summary>
    /// Called when this panel is replaced (Settings changed) or closed: cancels any request in flight and
    /// pending approvals, stops listening to the old session so late events can't touch anything, and closes
    /// the hub connection (which ends any outside MCP servers it started).
    /// </summary>
    public void Shutdown()
    {
        EndSession();
        if (_tools is { } tools) _ = tools.DisposeAsync().AsTask();
        _tools = null;
    }

    private void EndSession()
    {
        _generation++;
        _cts?.Cancel();
        _cts = null;
        CancelPendingApprovals();
        _detachSession?.Invoke();
        _detachSession = null;
        _session = null;
        IsBusy = false;
    }

    private void CancelPendingApprovals()
    {
        foreach (var pending in Items.OfType<ApprovalItem>().Where(a => a.IsPending).ToList()) pending.Cancel();
    }

    /// <summary>
    /// Connects to the hub on the first message: the app's tools, plus those of any outside MCP servers (which may take
    /// a few seconds to start). Kept for New chat; closed by <see cref="Shutdown"/>.
    /// </summary>
    private async Task<HubTools> ConnectToolsAsync(int generation, CancellationToken cancellationToken)
    {
        if (_tools is not null) return _tools;
        var outside = _services.OutsideServers(_settings);
        if (outside.Count > 0) Activity = "Connecting to " + string.Join(", ", outside.Select(s => s.Name)) + "…";
        HubTools tools;
        try
        {
            tools = await _services.Hub.ConnectChatAsync(outside, cancellationToken);
        }
        finally
        {
            Activity = "Thinking…";
        }
        if (generation != _generation)
        {
            await tools.DisposeAsync(); // closed while connecting
            throw new OperationCanceledException();
        }
        foreach (var problem in tools.Problems) Items.Add(new NoticeItem(problem, isError: true));
        return _tools = tools;
    }

    private async Task<ChatSession> CreateSessionAsync(int generation, CancellationToken cancellationToken)
    {
        var tools = await ConnectToolsAsync(generation, cancellationToken);
        var meter = new UsageMeter(Model, _settings.SessionSpendCapUsd);
        // The meter reports from whichever thread the request finished on (often not the UI's), so the summary is
        // updated on the UI thread, and not at all once this chat has been cleared or closed.
        // Token counts come from the provider; only the dollar figure is an estimate.
        EventHandler onUsage = (_, _) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (generation == _generation) UsageSummary = meter.Summary + (meter.EstimatedCostUsd is null ? "" : " (estimate)");
        });
        meter.Changed += onUsage;
        var session = new ChatSession(_services.CreateChatClient(_settings), tools.Catalog, meter, _services.ChatOptions);
        session.MessageAdded += Render;
        _detachSession = () =>
        {
            meter.Changed -= onUsage;
            session.MessageAdded -= Render;
        };
        return session;
    }

    private async Task<bool> ApproveAsync(ApprovalRequest request, int generation, CancellationToken cancellationToken)
    {
        // A request from a chat that has since been cleared or closed must never show a card in the new one.
        if (generation != _generation) throw new OperationCanceledException(cancellationToken);
        var item = new ApprovalItem(ApprovalText.Heading("The assistant", request.Capability), request.Capability.Title, ApprovalText.Arguments(request.Arguments));
        _itemsByCallId[request.CallId] = item;
        Items.Add(item);
        Activity = "Waiting for your approval…";
        await using var _ = cancellationToken.Register(item.Cancel);
        try
        {
            return await item.Decision;
        }
        finally
        {
            Activity = "Thinking…";
        }
    }

    private void Render(ChatMessage message)
    {
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent { Text.Length: > 0 } text when message.Role == ChatRole.User:
                    Items.Add(new UserItem(text.Text));
                    break;
                case TextContent { Text.Length: > 0 } text when message.Role == ChatRole.Assistant:
                    Items.Add(new AssistantItem(text.Text.Trim()));
                    break;
                case ToolApprovalRequestContent:
                    break; // Rendered by ApproveAsync as an approval card.
                case FunctionCallContent call when !_itemsByCallId.ContainsKey(call.CallId):
                    var title = _tools?.Catalog.TryGet(call.Name, out var capability) == true ? capability.Title : call.Name;
                    var tool = new ToolItem(title, call.Name);
                    _itemsByCallId[call.CallId] = tool;
                    Items.Add(tool);
                    break;
                case FunctionResultContent result:
                    switch (_itemsByCallId.GetValueOrDefault(result.CallId))
                    {
                        case ToolItem done: done.IsDone = true; break;
                        case ApprovalItem approval: approval.IsDone = true; break;
                    }
                    break;
            }
        }
    }
}
