using Demo.Ai;
using Demo.Ai.Credentials;
using Demo.Capabilities;

namespace Demo.Paths.Tests;

/// <summary>Tests that change process-wide environment variables, so nothing else may run alongside them.</summary>
[CollectionDefinition(nameof(EnvironmentVariables), DisableParallelization = true)]
public sealed class EnvironmentVariables;

/// <summary>
/// Vendor SDKs look for credentials of their own (environment variables, saved sign-in profiles) and add them to
/// requests. When IT routes the chat through a gateway, the user's personal credentials must never go with it.
/// </summary>
[Collection(nameof(EnvironmentVariables))]
public sealed class EnvironmentCredentialTests
{
    private const string Personal = "personal-credential-must-not-be-sent";

    private static readonly string[] Variables =
        ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_PROFILE", "OPENAI_API_KEY", "OPENAI_ORG_ID", "OPENAI_PROJECT_ID"];

    private sealed class GatewayPolicy(Uri url, string? token) : IMachinePolicy
    {
        public string? GatewayUrl(AiProvider provider) => url.ToString();
        public string? GatewayToken(AiProvider provider) => token;
        public bool DisableUserKeys => true;
    }

    [Theory]
    [InlineData(AiProvider.Anthropic, "gateway-token")]
    [InlineData(AiProvider.Anthropic, null)]
    [InlineData(AiProvider.OpenAI, "gateway-token")]
    [InlineData(AiProvider.OpenAI, null)]
    public async Task Personal_credentials_in_the_environment_never_reach_the_gateway(AiProvider provider, string? gatewayToken)
    {
        var saved = Variables.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var v in Variables) Environment.SetEnvironmentVariable(v, Personal);
            await using var gateway = await FakeAiGateway.StartAsync(_ => new ModelReply.Text("Hello."));
            var credential = new CredentialResolver(new InMemoryCredentialStore(), new GatewayPolicy(gateway.BaseUrl, gatewayToken))
                .Resolve(provider);
            var model = ProviderInfo.For(provider).DefaultModel;
            using var client = ChatClientFactory.Create(provider, model, credential);
            var session = new ChatSession(client, CapabilityCatalog.FromObject(new object()), new UsageMeter(model, null));

            var turn = await session.SendAsync("Hi", (_, _) => Task.FromResult(false), TestContext.Current.CancellationToken);

            Assert.Equal("Hello.", turn.Text);
            var request = Assert.Single(gateway.Received);
            Assert.DoesNotContain(request.Headers, h => h.Value.Contains(Personal, StringComparison.Ordinal));
            if (gatewayToken is not null) Assert.Contains(request.Headers, h => h.Value.Contains(gatewayToken, StringComparison.Ordinal));
        }
        finally
        {
            foreach (var (v, value) in saved) Environment.SetEnvironmentVariable(v, value);
        }
    }
}
