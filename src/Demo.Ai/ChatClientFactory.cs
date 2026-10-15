using System.ClientModel;
using Anthropic;
using Demo.Ai.Credentials;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;

namespace Demo.Ai;

/// <summary>
/// The only place that knows about vendor SDKs. Everything else in the app talks to
/// <see cref="IChatClient"/>. Adding a provider = one more case here. See docs/adr/002-provider-abstraction.md.
/// </summary>
public static class ChatClientFactory
{
    public const int MaxOutputTokens = 16_000;

    public static IChatClient Create(AiProvider provider, string model, ResolvedCredential credential)
    {
        if (!credential.IsAvailable)
            throw new InvalidOperationException(credential.Problem ?? $"No credentials are configured for {ProviderInfo.For(provider).DisplayName}.");

        return provider switch
        {
            AiProvider.Anthropic => CreateAnthropic(model, credential),
            AiProvider.OpenAI => CreateOpenAI(model, credential),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };
    }

    /// <summary>The official OpenAI endpoint, used whenever IT hasn't set a gateway.</summary>
    internal static readonly Uri OpenAIApi = new("https://api.openai.com/v1");

    private static IChatClient CreateAnthropic(string model, ResolvedCredential credential) =>
        CreateAnthropicClient(credential).AsIChatClient(model, MaxOutputTokens);

    /// <summary>
    /// The address is always set explicitly: left unset, the SDK reads ANTHROPIC_BASE_URL from the environment,
    /// so anything that can set an environment variable could send the user's key and data elsewhere.
    /// </summary>
    internal static AnthropicClient CreateAnthropicClient(ResolvedCredential credential) =>
        new() { ApiKey = credential.ApiKey, BaseUrl = credential.BaseUrl?.ToString() ?? Anthropic.Core.EnvironmentUrl.Production };

#pragma warning disable OPENAI001 // The SDK still marks its Responses API as experimental.
    private static IChatClient CreateOpenAI(string model, ResolvedCredential credential)
    {
        // The OpenAI SDK reads no address from the environment, but it's pinned as for Anthropic so it never can.
        var options = new OpenAIClientOptions { Endpoint = credential.BaseUrl ?? OpenAIApi };
        // A gateway may authenticate by network identity and need no token; the SDK still requires a value.
        var client = new OpenAIClient(new ApiKeyCredential(credential.ApiKey ?? "gateway"), options);
        // The Responses API, not Chat Completions: current models reject function tools plus reasoning on
        // /v1/chat/completions (HTTP 400). Responses are stored server-side by default; customer data shouldn't be,
        // so storage is off and the full conversation is sent with each request (as with Anthropic).
        return client.GetResponsesClient().AsIChatClient(model)
            .AsBuilder()
            .ConfigureOptions(o => o.RawRepresentationFactory = WithStorageOff(o.RawRepresentationFactory))
            .Build();
    }

    /// <summary>
    /// Wraps a caller's raw-options factory (if any) so storage is off whatever it returns. With storage off,
    /// reasoning items can't be referenced by id on the next request, so their encrypted content is requested
    /// and sent back instead.
    /// </summary>
    internal static Func<IChatClient, object?> WithStorageOff(Func<IChatClient, object?>? callerFactory) => chatClient =>
    {
        var raw = callerFactory?.Invoke(chatClient) as CreateResponseOptions ?? new CreateResponseOptions();
        raw.StoredOutputEnabled = false;
        if (!raw.IncludedProperties.Contains(IncludedResponseProperty.ReasoningEncryptedContent))
            raw.IncludedProperties.Add(IncludedResponseProperty.ReasoningEncryptedContent);
        return raw;
    };
#pragma warning restore OPENAI001
}
