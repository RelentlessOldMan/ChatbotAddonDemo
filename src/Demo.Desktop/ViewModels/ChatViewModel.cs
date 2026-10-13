using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Demo.Ai;
using Demo.Capabilities;
using Microsoft.Extensions.AI;

namespace Demo.Desktop.ViewModels;

/// <summary>The opt-in assistant panel. Created only when the user has enabled chat in Settings.</summary>
public sealed partial class ChatViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly AiSettings _settings;
    private readonly Dictionary<string, ChatItem> _itemsByCallId = [];
    private ChatSession? _session;
    private CancellationTokenSource? _cts;

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
        _cts = new CancellationTokenSource();
        try
        {
            _session ??= CreateSession();
            await _session.SendAsync(text, ApproveAsync, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Items.Add(new NoticeItem("Stopped."));
        }
        catch (SpendCapExceededException ex)
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
            foreach (var pending in Items.OfType<ApprovalItem>().Where(a => a.IsPending)) pending.Cancel();
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private bool CanStop() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void NewChat()
    {
        _cts?.Cancel();
        _session = null;
        _itemsByCallId.Clear();
        Items.Clear();
        UsageSummary = "No usage yet";
    }

    private ChatSession CreateSession()
    {
        var meter = new UsageMeter(Model, _settings.SessionSpendCapUsd);
        meter.Changed += (_, _) => UsageSummary = meter.Summary + " (estimate)";
        var session = new ChatSession(_services.CreateChatClient(_settings), _services.Catalog, meter);
        session.MessageAdded += Render;
        return session;
    }

    private async Task<bool> ApproveAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        var item = new ApprovalItem(request.Capability.Title, FormatArguments(request.Arguments));
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
                    var title = _services.Catalog.Capabilities.FirstOrDefault(c => c.Name == call.Name)?.Title ?? call.Name;
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

    private static string FormatArguments(IReadOnlyDictionary<string, object?> arguments) =>
        string.Join("\n", arguments.Select(a => $"{a.Key}: {(a.Value is JsonElement e && e.ValueKind == JsonValueKind.String ? e.GetString() : a.Value)}"));
}
