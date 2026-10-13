using Demo.Ai;
using Demo.Ai.Credentials;
using Demo.Capabilities;
using Demo.Capabilities.TestBench;
using Demo.Domain;

namespace Demo.Ai.Tests;

/// <summary>
/// Real API calls, which cost money. Skipped unless DEMO_LIVE_TESTS=1 *and* the provider's key is set
/// (many developers have keys in their environment; that alone must not spend money).
/// Run: $env:DEMO_LIVE_TESTS='1'; dotnet test --filter Category=Live
/// </summary>
[Trait("Category", "Live")]
public class LiveProviderTests
{
    [Theory]
    [InlineData(AiProvider.Anthropic)]
    [InlineData(AiProvider.OpenAI)]
    public async Task Model_uses_a_read_tool_and_answers_from_its_result(AiProvider provider)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DEMO_LIVE_TESTS") == "1", "set DEMO_LIVE_TESTS=1 to run paid API tests");
        var info = ProviderInfo.For(provider);
        var key = Environment.GetEnvironmentVariable(info.ApiKeyEnvironmentVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(key), $"{info.ApiKeyEnvironmentVariable} not set");

        var bench = new TestBenchService(Path.Combine(Path.GetTempPath(), "live-tests", Guid.NewGuid().ToString("N")), BenchOptions.Instant);
        using var client = ChatClientFactory.Create(provider, info.DefaultModel, new ResolvedCredential(CredentialSource.EnvironmentVariable, key));
        var session = new ChatSession(client, CapabilityCatalog.FromObject(new TestBenchCapabilities(bench)), new UsageMeter(info.DefaultModel, 0.50m),
            ChatSessionOptions.For(TestBenchCapabilities.Instructions));

        var turn = await session.SendAsync("Which spec sheets do we have? Reply with just the file names.", (_, _) => Task.FromResult(false));

        Assert.Contains("LDO-3300_datasheet.csv", turn.Text);
        Assert.Contains("LDO-3300_rev-B_targets.json", turn.Text);
        Assert.True(session.Meter.InputTokens > 0);
    }
}
