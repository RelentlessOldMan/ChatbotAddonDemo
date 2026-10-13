using System.ClientModel;
using Anthropic;
using Demo.Ai.Credentials;
using Microsoft.Extensions.AI;
using OpenAI;

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

    private static IChatClient CreateOpenAI(string model, ResolvedCredential credential)
    {
        var options = new OpenAIClientOptions();
        if (credential.BaseUrl is not null) options.Endpoint = credential.BaseUrl;
        // A gateway may authenticate by network identity and need no token; the SDK still requires a value.
        var client = new OpenAIClient(new ApiKeyCredential(credential.ApiKey ?? "gateway"), options);
        return client.GetChatClient(model).AsIChatClient();
    }
}
