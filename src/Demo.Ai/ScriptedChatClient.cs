using Microsoft.Extensions.AI;

namespace Demo.Ai;

/// <summary>
/// A fake model that replays a script. Used by tests and by the desktop app's
/// <c>--demo</c> mode so screenshots and walkthroughs need no key and are identical every run.
/// Tools still really execute: the function-invocation layer runs them against the domain.
/// </summary>
public sealed class ScriptedChatClient(IEnumerable<ScriptedChatClient.Step> steps) : IChatClient
{
    /// <summary>Produces the next response given the conversation so far.</summary>
    public delegate ChatResponse Step(IReadOnlyList<ChatMessage> history);

    private readonly Queue<Step> _steps = new(steps);

    public string ModelId { get; init; } = "scripted";

    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public int RemainingSteps => _steps.Count;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var history = messages.ToList();
        Requests.Add(history);
        if (!_steps.TryDequeue(out var step))
            throw new InvalidOperationException("The script has no more responses.");
        var response = step(history);
        response.ModelId ??= ModelId;
        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    public static Step Reply(string text, int inputTokens = 900, int outputTokens = 60) =>
        _ => WithUsage(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)), inputTokens, outputTokens);

    public static Step Call(string toolName, IDictionary<string, object?>? arguments = null, string? preamble = null, int inputTokens = 800, int outputTokens = 40) =>
        _ =>
        {
            var contents = new List<AIContent>();
            if (preamble is not null) contents.Add(new TextContent(preamble));
            contents.Add(new FunctionCallContent($"call_{Guid.NewGuid():N}"[..13], toolName, arguments));
            return WithUsage(new ChatResponse(new ChatMessage(ChatRole.Assistant, contents)), inputTokens, outputTokens);
        };

    private static ChatResponse WithUsage(ChatResponse response, int input, int output)
    {
        response.Usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = output, TotalTokenCount = input + output };
        response.FinishReason = response.Messages[^1].Contents.OfType<FunctionCallContent>().Any() ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop;
        return response;
    }
}
