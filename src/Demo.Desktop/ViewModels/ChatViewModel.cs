using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
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
            _session ??= CreateSession();
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
    /// pending approvals, and stops listening to the old session so late events can't touch anything.
    /// </summary>
    public void Shutdown() => EndSession();

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

    private ChatSession CreateSession()
    {
        var meter = new UsageMeter(Model, _settings.SessionSpendCapUsd);
        // Token counts come from the provider; only the dollar figure is an estimate.
        EventHandler onUsage = (_, _) => UsageSummary = meter.Summary + (meter.EstimatedCostUsd is null ? "" : " (estimate)");
        meter.Changed += onUsage;
        var session = new ChatSession(_services.CreateChatClient(_settings), _services.Catalog, meter, _services.ChatOptions);
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
        var heading = request.Capability.Safety switch
        {
            SafetyClass.Execute => "The assistant wants to use the test bench",
            SafetyClass.Write => "The assistant wants to create a file",
            // A read normally runs without asking, but is asked about when it comes in the same reply as a change.
            _ => "The assistant wants to look something up",
        };
        var item = new ApprovalItem(heading, request.Capability.Title, FormatArguments(request.Arguments));
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

    private const int MaxArgumentLength = 200;

    /// <summary>
    /// One line per argument; strings bare, everything else (numbers, lists) as compact JSON. The values come from
    /// the model, so each is forced onto one line (a value with line breaks can't pass for extra arguments) and cut short.
    /// </summary>
    private static string FormatArguments(IReadOnlyDictionary<string, object?> arguments) =>
        string.Join("\n", arguments.Select(a => $"{OneLine(a.Key)}: {OneLine(a.Value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? "",
            string s => s,
            var v => JsonSerializer.Serialize(v, CapabilityCatalog.SerializerOptions),
        })}"));

    private static string OneLine(string value)
    {
        var text = new StringBuilder(Math.Min(value.Length, MaxArgumentLength + 10));
        foreach (var c in value)
        {
            if (text.Length >= MaxArgumentLength) return text.Append('…').ToString();
            text.Append(c switch
            {
                '\r' => @"\r",
                '\n' => @"\n",
                '\t' => @"\t",
                _ when char.IsControl(c) || char.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator => $@"\u{(int)c:x4}",
                _ => c.ToString(),
            });
        }
        return text.ToString();
    }
}
