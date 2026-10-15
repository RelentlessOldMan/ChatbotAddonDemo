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

    /// <summary>
    /// Upper bound on automatic tool round trips within one call to the function-invocation layer, i.e. between
    /// two approval questions. When it's reached, that layer makes one more request without tools to get a
    /// final answer. It starts again after each approval, so it doesn't bound a whole message on its own:
    /// <see cref="MaxModelRequestsPerMessage"/> does.
    /// </summary>
    public int MaxToolIterations { get; init; } = 8;

    /// <summary>
    /// Upper bound on model requests for one user message, across all tool round trips and approvals, so a
    /// confused model can't loop on the customer's bill. Reaching it ends the message with
    /// <see cref="TurnRequestLimitException"/>.
    /// </summary>
    public int MaxModelRequestsPerMessage { get; init; } = 20;
}

/// <summary>One user message needed more model requests than <see cref="ChatSessionOptions.MaxModelRequestsPerMessage"/> allows.</summary>
public sealed class TurnRequestLimitException(int limit)
    : Exception($"The assistant stopped after {limit} requests to the AI provider for this one message without finishing. Try asking for less at once, or start a new chat.");

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
    private readonly int _maxRequestsPerMessage;
    private int _requestsThisMessage;

    /// <summary>
    /// The conversation as last sent (or about to be sent) to the model during the current
    /// <see cref="IChatClient.GetResponseAsync"/> call; null between calls. See <see cref="KeepWhatAlreadyHappened"/>.
    /// </summary>
    private List<ChatMessage>? _lastModelRequest;

    public ChatSession(IChatClient innerClient, CapabilityCatalog catalog, UsageMeter meter, ChatSessionOptions? options = null)
    {
        options ??= new ChatSessionOptions();
        _catalog = catalog;
        _maxRequestsPerMessage = options.MaxModelRequestsPerMessage;
        Meter = meter;
        // The builder lists layers outermost first, so everything after UseFunctionInvocation sits inside the tool
        // loop and sees each individual model request, tool round trips included.
        _client = new ChatClientBuilder(innerClient)
            .UseFunctionInvocation(configure: f =>
            {
                f.MaximumIterationsPerRequest = options.MaxToolIterations;
                f.IncludeDetailedErrors = false; // Unexpected exception text can contain internals; domain errors are already results.
            })
            .Use(async (messages, chatOptions, next, cancellationToken) =>
            {
                // Recorded before the limits are checked, so a refused request still leaves its record.
                _lastModelRequest = messages.ToList();
                if (++_requestsThisMessage > _maxRequestsPerMessage) throw new TurnRequestLimitException(_maxRequestsPerMessage);
                await next(_lastModelRequest, chatOptions, cancellationToken);
            })
            .Use(inner => new MeteringChatClient(inner, meter)) // enforces the spending limit before every request
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

    /// <summary>
    /// Sends one user message and runs it to completion, asking <paramref name="approve"/> about each change.
    /// The spending limit is enforced before every model request by <see cref="MeteringChatClient"/>.
    /// If anything fails or is cancelled, tools that already ran stay in the conversation (so the model still
    /// knows about a plan it created, say), and only what can't be sent again is dropped; see
    /// <see cref="KeepWhatAlreadyHappened"/>.
    /// </summary>
    public async Task<ChatTurn> SendAsync(string text, ApprovalHandler approve, CancellationToken cancellationToken = default)
    {
        var turnStart = _history.Count;
        _requestsThisMessage = 0;
        _lastModelRequest = null;
        try
        {
            return await RunTurnAsync(text, approve, cancellationToken);
        }
        catch
        {
            KeepWhatAlreadyHappened(turnStart);
            throw;
        }
        finally
        {
            _lastModelRequest = null;
        }
    }

    private async Task<ChatTurn> RunTurnAsync(string text, ApprovalHandler approve, CancellationToken cancellationToken)
    {
        var turn = new List<ChatMessage>();
        Append(new ChatMessage(ChatRole.User, text), turn);

        // Why a loop on top of the function-invocation layer: that layer runs Read tools itself, but stops at the
        // first reply that calls a tool needing approval and returns approval requests instead. We ask the user,
        // append the decisions, and call again; the layer then runs (or declines) those tools and carries on.
        while (true)
        {
            var response = await _client.GetResponseAsync(_history, _chatOptions, cancellationToken);
            _lastModelRequest = null; // that call's results are in the response now
            foreach (var message in response.Messages) Append(message, turn);

            var pending = response.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().ToList();
            if (pending.Count == 0) return new ChatTurn(turn);

            var decisions = new List<AIContent>();
            foreach (var request in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var call = (FunctionCallContent)request.ToolCall;
                // The model can name a tool that doesn't exist: refuse it instead of asking the user about nothing.
                if (!_catalog.TryGet(call.Name, out var capability))
                {
                    decisions.Add(request.CreateResponse(false, $"There is no tool named '{call.Name}'."));
                    continue;
                }
                var approved = await approve(new ApprovalRequest(call.CallId, capability, call.Arguments?.AsReadOnly() ?? EmptyArguments), cancellationToken);
                decisions.Add(request.CreateResponse(approved, approved ? null : "The user declined this action."));
            }
            Append(new ChatMessage(ChatRole.User, decisions), turn);
        }
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyArguments = new Dictionary<string, object?>();

    /// <summary>
    /// After a failure or Stop, keeps the part of this message's exchange that really happened and can be sent
    /// again: every tool call with its result, up to the last tool that ran. What comes after (an approval request
    /// nobody answered, a reply that never arrived) is dropped. If no tool ran, the message is removed altogether,
    /// as if it had never been sent.
    /// </summary>
    private void KeepWhatAlreadyHappened(int turnStart)
    {
        var known = new HashSet<ChatMessage>(_history, ReferenceEqualityComparer.Instance);

        // A failed model request: the function-invocation layer had already run the tools it was answering
        // (approved changes included) and built the conversation it was sending. Those results are nowhere
        // else, and the conversation is complete by construction, so it replaces ours.
        if (_lastModelRequest is { } sent && sent.Count > turnStart)
        {
            _history.Clear();
            _history.AddRange(sent);
        }

        var lastResult = _history.FindLastIndex(m => m.Contents.Any(c => c is FunctionResultContent));
        var keep = Math.Max(turnStart, lastResult + 1);
        _history.RemoveRange(keep, _history.Count - keep);
        if (!IsComplete(_history.Skip(turnStart).ToList())) _history.RemoveRange(turnStart, _history.Count - turnStart);

        // Tool results the UI hasn't seen yet (from the failed request) are announced, so it can show them as done.
        foreach (var message in _history.Skip(turnStart).Where(m => !known.Contains(m))) MessageAdded?.Invoke(message);
    }

    /// <summary>
    /// True when every tool call has its result and every approval request its answer (and that answer's result),
    /// which both providers require of a conversation, and which stops an approved change being run twice.
    /// </summary>
    private static bool IsComplete(IReadOnlyList<ChatMessage> messages)
    {
        var contents = messages.SelectMany(m => m.Contents).ToList();
        var results = contents.OfType<FunctionResultContent>().Select(r => r.CallId).ToHashSet();
        var answers = contents.OfType<ToolApprovalResponseContent>().Select(r => r.RequestId).ToHashSet();
        return contents.All(c => c switch
        {
            FunctionCallContent call => results.Contains(call.CallId),
            ToolApprovalRequestContent request => answers.Contains(request.RequestId),
            ToolApprovalResponseContent answer => answer.ToolCall is not FunctionCallContent call || results.Contains(call.CallId),
            _ => true,
        });
    }

    /// <summary>Raised for each message as it joins the conversation, so a UI can render progressively.</summary>
    public event Action<ChatMessage>? MessageAdded;

    private void Append(ChatMessage message, List<ChatMessage> turn)
    {
        _history.Add(message);
        turn.Add(message);
        MessageAdded?.Invoke(message);
    }
}
