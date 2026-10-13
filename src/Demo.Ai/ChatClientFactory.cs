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
            throw new InvalidOperationException($"No credentials are configured for {ProviderInfo.For(provider).DisplayName}.");

        return provider switch
        {
            AiProvider.Anthropic => CreateAnthropic(model, credential),
            AiProvider.OpenAI => CreateOpenAI(model, credential),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };
    }

    private static IChatClient CreateAnthropic(string model, ResolvedCredential credential)
    {
        var client = credential.BaseUrl is null
            ? new AnthropicClient { ApiKey = credential.ApiKey }
            : new AnthropicClient { ApiKey = credential.ApiKey, BaseUrl = credential.BaseUrl.ToString() };
        return client.AsIChatClient(model, MaxOutputTokens);
    }

#pragma warning disable OPENAI001 // The SDK still marks its Responses API as experimental.
    private static IChatClient CreateOpenAI(string model, ResolvedCredential credential)
    {
        var options = new OpenAIClientOptions();
        if (credential.BaseUrl is not null) options.Endpoint = credential.BaseUrl;
        // A gateway may authenticate by network identity and need no token; the SDK still requires a value.
        var client = new OpenAIClient(new ApiKeyCredential(credential.ApiKey ?? "gateway"), options);
        // The Responses API, not Chat Completions: current models reject function tools plus reasoning on
        // /v1/chat/completions (HTTP 400). Responses are stored server-side by default; customer data shouldn't be,
        // so storage is off and the full conversation is sent with each request (as with Anthropic).
        return client.GetResponsesClient().AsIChatClient(model)
            .AsBuilder()
            .ConfigureOptions(o => o.RawRepresentationFactory ??= _ => new CreateResponseOptions { StoredOutputEnabled = false })
            .Build();
    }
#pragma warning restore OPENAI001
}
