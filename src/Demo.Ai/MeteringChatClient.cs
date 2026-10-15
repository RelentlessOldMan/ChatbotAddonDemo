using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Demo.Ai;

/// <summary>
/// Meters every individual model request and enforces the chat's limit before each one. It sits *below*
/// the function-invocation layer, so it also sees the automatic tool round trips inside a single user message,
/// and records each request's usage as soon as it completes (a later failure or Stop doesn't lose it).
/// <see cref="UsageMeter.Changed"/> is therefore raised on whatever thread the request finished on, which is
/// often not the UI thread.
/// </summary>
public sealed class MeteringChatClient(IChatClient innerClient, UsageMeter meter) : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IReadOnlyCollection<ChatMessage> ?? messages.ToList();
        meter.EnsureCanAfford(list);
        var response = await base.GetResponseAsync(list, options, cancellationToken);
        meter.Add(response.Usage);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages as IReadOnlyCollection<ChatMessage> ?? messages.ToList();
        meter.EnsureCanAfford(list);
        var usage = new UsageDetails();
        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(list, options, cancellationToken))
            {
                foreach (var content in update.Contents.OfType<UsageContent>()) usage.Add(content.Details);
                yield return update;
            }
        }
        finally
        {
            meter.Add(usage); // whatever was reported, even if the stream was cut short
        }
    }
}
