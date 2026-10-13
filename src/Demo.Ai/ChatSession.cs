using System.Text.Json;
using Demo.Capabilities;
using Microsoft.Extensions.AI;

namespace Demo.Ai;

/// <summary>A change or bench action the model wants to make, waiting for the user's decision.</summary>
public sealed record ApprovalRequest(string CallId, Capability Capability, IReadOnlyDictionary<string, object?> Arguments)
{
    public string ArgumentsJson => JsonSerializer.Serialize(Arguments, CapabilityCatalog.SerializerOptions);
}

public delegate Task<bool> ApprovalHandler(ApprovalRequest request, CancellationToken cancellationToken);

public sealed record ChatSessionOptions
{
    /// <summary>Generic rules for any tool the assistant is embedded in; the host app adds its domain instructions.</summary>
    public const string BasePrompt =
        """
        You are the assistant built into a desktop application. Answer using the tools provided.
        Tools that change data or start equipment ask the user for approval before they run. If the user declines, accept it and suggest alternatives.
        Keep answers short and practical. Refer to things by their ids.
        The chat panel displays plain text, so do not use Markdown formatting.
        """;

    /// <summary>Base rules plus the host application's domain instructions.</summary>
    public static ChatSessionOptions For(string domainInstructions) => new() { SystemPrompt = BasePrompt + "\n" + domainInstructions };

    public string SystemPrompt { get; init; } = BasePrompt;

    /// <summary>Upper bound on model↔tool round-trips per user message, so a confused model can't loop on the customer's bill.</summary>
    public int MaxToolIterations { get; init; } = 8;
}

/// <summary>One user message and everything that happened in response to it.</summary>
public sealed record ChatTurn(IReadOnlyList<ChatMessage> Messages)
{
    public string Text => string.Join("\n", Messages.Where(m => m.Role == ChatRole.Assistant).Select(m => m.Text).Where(t => t.Length > 0));
}

/// <summary>
/// Provider-neutral conversation with tool calling and human approval for every capability
/// that is not <see cref="SafetyClass.Read"/>. Not thread-safe: one message at a time.
/// </summary>
public sealed class ChatSession
{
    private readonly IChatClient _client;
    private readonly CapabilityCatalog _catalog;
    private readonly ChatOptions _chatOptions;
    private readonly List<ChatMessage> _history;

    public ChatSession(IChatClient innerClient, CapabilityCatalog catalog, UsageMeter meter, ChatSessionOptions? options = null)
    {
        options ??= new ChatSessionOptions();
        _catalog = catalog;
        Meter = meter;
        _client = new ChatClientBuilder(innerClient)
            .UseFunctionInvocation(configure: f =>
            {
                f.MaximumIterationsPerRequest = options.MaxToolIterations;
                f.IncludeDetailedErrors = false; // Unexpected exception text can contain internals; domain errors are already results.
            })
            .Build();

        _chatOptions = new ChatOptions
        {
            Tools = catalog.Capabilities
                .Select(c => c.Safety == SafetyClass.Read ? c.Function : (AITool)new ApprovalRequiredAIFunction(c.Function))
                .ToList(),
        };
        _history = [new ChatMessage(ChatRole.System, options.SystemPrompt)];
    }

    public UsageMeter Meter { get; }

    public IReadOnlyList<ChatMessage> History => _history;

    public async Task<ChatTurn> SendAsync(string text, ApprovalHandler approve, CancellationToken cancellationToken = default)
    {
        Meter.EnsureUnderCap();
        var turn = new List<ChatMessage>();
        Append(new ChatMessage(ChatRole.User, text), turn);

        while (true)
        {
            var response = await _client.GetResponseAsync(_history, _chatOptions, cancellationToken);
            Meter.Add(response.Usage);
            foreach (var message in response.Messages) Append(message, turn);

            var pending = response.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().ToList();
            if (pending.Count == 0) return new ChatTurn(turn);

            var decisions = new List<AIContent>();
            foreach (var request in pending)
            {
                var call = (FunctionCallContent)request.ToolCall;
                var approved = await approve(new ApprovalRequest(call.CallId, _catalog[call.Name], call.Arguments?.AsReadOnly() ?? EmptyArguments), cancellationToken);
                decisions.Add(request.CreateResponse(approved, approved ? null : "The user declined this action."));
            }
            Append(new ChatMessage(ChatRole.User, decisions), turn);
            Meter.EnsureUnderCap();
        }
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyArguments = new Dictionary<string, object?>();

    /// <summary>Raised for each message as it joins the conversation, so a UI can render progressively.</summary>
    public event Action<ChatMessage>? MessageAdded;

    private void Append(ChatMessage message, List<ChatMessage> turn)
    {
        _history.Add(message);
        turn.Add(message);
        MessageAdded?.Invoke(message);
    }
}
