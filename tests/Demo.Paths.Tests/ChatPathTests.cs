using Demo.Ai;
using Demo.Ai.Credentials;
using Demo.Capabilities;
using Demo.Capabilities.TestBench;
using Demo.Domain;

namespace Demo.Paths.Tests;

/// <summary>
/// Path C, the chat panel, end to end except for the window: IT's gateway policy → credential resolver → the real
/// Claude or OpenAI SDK → HTTP → a fake gateway with a scripted model → tool calls → approvals → core logic → files.
/// Free: the gateway runs on 127.0.0.1.
/// </summary>
public sealed class ChatPathTests : IDisposable
{
    private const string GatewayToken = "test-gateway-token";
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "paths-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _approvalsAsked = [];

    private sealed class GatewayPolicy(Uri url) : IMachinePolicy
    {
        public string? GatewayUrl(AiProvider provider) => url.ToString();
        public string? GatewayToken(AiProvider provider) => ChatPathTests.GatewayToken;
        public bool DisableUserKeys => true;
    }

    private (ChatSession Session, IDisposable Client) Connect(AiProvider provider, FakeAiGateway gateway, decimal? limitUsd = 1.00m, long unpricedTokenLimit = 500_000)
    {
        // Exactly what the desktop app does, with the gateway set by policy (so no personal key can be used).
        var credential = new CredentialResolver(new InMemoryCredentialStore(), new GatewayPolicy(gateway.BaseUrl), _ => "must-not-be-used")
            .Resolve(provider);
        Assert.Equal(CredentialSource.Gateway, credential.Source);

        var model = ProviderInfo.For(provider).DefaultModel;
        var client = ChatClientFactory.Create(provider, model, credential);
        var bench = new TestBenchService(_workspace, BenchOptions.Instant);
        var session = new ChatSession(client, CapabilityCatalog.FromObject(new TestBenchCapabilities(bench)), new UsageMeter(model, limitUsd, tokenLimitWhenUnpriced: unpricedTokenLimit),
            ChatSessionOptions.For(TestBenchCapabilities.Instructions));
        return (session, client);
    }

    private ApprovalHandler Answer(bool approve) => (request, _) =>
    {
        _approvalsAsked.Add(request.Capability.Name);
        return Task.FromResult(approve);
    };

    [Theory]
    [InlineData(AiProvider.Anthropic)]
    [InlineData(AiProvider.OpenAI)]
    public async Task The_whole_job_runs_through_the_real_SDK_and_the_gateway(AiProvider provider)
    {
        await using var gateway = await FakeAiGateway.StartAsync(Scenario.ScriptedModel);
        var (session, client) = Connect(provider, gateway);
        using var _ = client;

        var turn = await session.SendAsync(Scenario.Question, Answer(true), TestContext.Current.CancellationToken);

        // The user was asked about the two actions that change something, and nothing else.
        Assert.Equal(["create_test_plan_from_spec", "start_test_run"], _approvalsAsked);

        // The work really happened, in the workspace every path shares.
        var bench = new TestBenchService(_workspace, BenchOptions.Instant);
        var plan = Assert.Single(bench.ListPlans(), p => p.Id == "TP-1002");
        Assert.Equal(Scenario.Measurements, plan.MeasurementCount);
        var run = Assert.Single(bench.ListRuns("TP-1002"));
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Contains("R-1002 is done", turn.Text);

        // Six billed requests: one per tool round trip, plus the final answer.
        Assert.Equal(6, gateway.Received.Count);
        Assert.Equal(6, session.Meter.Requests);
        Assert.Equal(6 * FakeAiGateway.InputTokensPerRequest, session.Meter.InputTokens);

        for (var i = 0; i < gateway.Received.Count; i++)
        {
            var request = gateway.Received[i];
            // Every request carries the 13 tools for AI and the whole conversation so far (i tool results).
            Assert.Equal(13, request.Parsed.ToolNames.Count);
            Assert.Equal(i, request.Parsed.ToolResults.Count);
            Assert.Contains("LDO-3300", request.Parsed.LastUserText);
            // Approvals are our bookkeeping: they must not reach the provider as empty messages.
            Assert.Equal(0, request.Parsed.EmptyMessages);
            // The gateway token goes with every request, and the personal key never does.
            Assert.Contains(request.Headers, h => h.Value.Contains(GatewayToken));
            Assert.DoesNotContain(request.Headers, h => h.Value.Contains("must-not-be-used"));
        }

        if (provider == AiProvider.OpenAI)
        {
            // Customer data isn't stored at OpenAI.
            Assert.All(gateway.Received, r => Assert.False(r.Body["store"]?.GetValue<bool>()));
        }
    }

    [Theory]
    [InlineData(AiProvider.Anthropic)]
    [InlineData(AiProvider.OpenAI)]
    public async Task Deny_means_nothing_is_created_and_the_model_is_told(AiProvider provider)
    {
        await using var gateway = await FakeAiGateway.StartAsync(Scenario.ScriptedModel);
        var (session, client) = Connect(provider, gateway);
        using var _ = client;

        var turn = await session.SendAsync(Scenario.Question, Answer(false), TestContext.Current.CancellationToken);

        Assert.Equal(["create_test_plan_from_spec"], _approvalsAsked);
        Assert.Equal(["TP-1001"], new TestBenchService(_workspace, BenchOptions.Instant).ListPlans().Select(p => p.Id));
        Assert.True(Scenario.Declined(gateway.Received[^1].Parsed.ToolResults[^1]));
        Assert.Contains("didn't do that", turn.Text);
        Assert.Equal(3, gateway.Received.Count);
    }

    [Theory]
    [InlineData(AiProvider.Anthropic)]
    [InlineData(AiProvider.OpenAI)]
    public async Task A_spending_limit_too_small_for_one_request_sends_nothing(AiProvider provider)
    {
        await using var gateway = await FakeAiGateway.StartAsync(Scenario.ScriptedModel);
        // Models without a price in the table (the OpenAI ones, for now) are limited by tokens instead of dollars.
        var (session, client) = Connect(provider, gateway, limitUsd: 0.0001m, unpricedTokenLimit: 100);
        using var _ = client;

        await Assert.ThrowsAsync<SpendCapExceededException>(() =>
            session.SendAsync(Scenario.Question, Answer(true), TestContext.Current.CancellationToken));

        Assert.Empty(gateway.Received);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
