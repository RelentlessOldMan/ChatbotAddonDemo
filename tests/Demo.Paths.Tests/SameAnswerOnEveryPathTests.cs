using System.Globalization;
using System.Text.Json;
using Demo.Ai;
using Demo.Ai.Credentials;
using Demo.Capabilities;
using Demo.Capabilities.TestBench;
using Demo.Domain;

namespace Demo.Paths.Tests;

/// <summary>
/// The paths share one workspace and one core logic, so they must agree: the same job gives the same plan and the
/// same findings whichever path does it, and what one path makes, the others see. Free: no real model is used
/// (path C talks to the fake gateway; path D is driven by an MCP client, as a coding agent would drive it).
/// </summary>
public sealed class SameAnswerOnEveryPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "paths-tests", Guid.NewGuid().ToString("N"));
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Workspace(string name) => Path.Combine(_root, name);

    /// <summary>The job on path B: three console commands.</summary>
    private RunOutcome DoJobOnCommandLine(string workspace)
    {
        var plan = Paths.Cli(workspace, "plan", "from-spec", Scenario.SpecFile,
            "--temps", string.Join(',', Scenario.Temperatures.Select(t => t.ToString(CultureInfo.InvariantCulture))),
            "--supplies", string.Join(',', Scenario.Supplies.Select(v => v.ToString(CultureInfo.InvariantCulture))),
            "--samples", Scenario.Units.ToString(CultureInfo.InvariantCulture));
        Assert.True(plan.ExitCode == 0, plan.Error);
        var run = Paths.Cli(workspace, "run", Scenario.PlanId().Match(plan.Output).Value);
        Assert.True(run.ExitCode == 0, run.Error);
        return RunOutcome.Read(workspace, Scenario.RunId().Match(run.Output).Value);
    }

    /// <summary>The job on path C: one chat message, approved twice.</summary>
    private async Task<RunOutcome> DoJobInChat(string workspace, AiProvider provider)
    {
        await using var gateway = await FakeAiGateway.StartAsync(Scenario.ScriptedModel);
        var credential = new ResolvedCredential(CredentialSource.Gateway, "token", gateway.BaseUrl);
        var model = ProviderInfo.For(provider).DefaultModel;
        using var client = ChatClientFactory.Create(provider, model, credential);
        var session = new ChatSession(client, CapabilityCatalog.FromObject(new TestBenchCapabilities(new TestBenchService(workspace, BenchOptions.Instant))),
            new UsageMeter(model, null), ChatSessionOptions.For(TestBenchCapabilities.Instructions));

        var turn = await session.SendAsync(Scenario.Question, (_, _) => Task.FromResult(true), Ct);
        return RunOutcome.Read(workspace, Scenario.RunId().Match(turn.Text).Value);
    }

    /// <summary>The job on path D: the tool calls a coding agent would make over MCP.</summary>
    private async Task<RunOutcome> DoJobOverMcp(string workspace)
    {
        await using var mcp = await Paths.Mcp(workspace, Ct);
        var plan = await mcp.Call("create_test_plan_from_spec", Scenario.PlanArguments().Deserialize<Dictionary<string, object?>>()!, Ct);
        var started = await mcp.Call("start_test_run", new() { ["planId"] = plan.GetProperty("plan").GetProperty("id").GetString() }, Ct);
        var runId = started.GetProperty("run").GetProperty("id").GetString()!;
        var finished = await mcp.Call("wait_for_test_run", new() { ["runId"] = runId, ["maxSeconds"] = 60 }, Ct);
        Assert.Equal("Completed", finished.GetProperty("status").GetString());
        return RunOutcome.Read(workspace, runId);
    }

    [Fact]
    public async Task The_same_job_gives_the_same_plan_and_findings_on_paths_B_C_and_D()
    {
        var onCommandLine = DoJobOnCommandLine(Workspace("b"));
        var inClaudeChat = await DoJobInChat(Workspace("c-claude"), AiProvider.Anthropic);
        var inOpenAiChat = await DoJobInChat(Workspace("c-openai"), AiProvider.OpenAI);
        var overMcp = await DoJobOverMcp(Workspace("d"));

        Assert.Equal(Scenario.Measurements, onCommandLine.Measurements);
        Assert.Equal(onCommandLine, inClaudeChat);
        Assert.Equal(onCommandLine, inOpenAiChat);
        Assert.Equal(onCommandLine, overMcp);
    }

    [Fact]
    public async Task What_one_path_makes_the_others_see()
    {
        var workspace = Workspace("shared");

        // Path D (an agent) makes the plan; path B (a person at the command line) sees it and runs it.
        await using (var mcp = await Paths.Mcp(workspace, Ct))
            await mcp.Call("create_test_plan_from_spec", Scenario.PlanArguments().Deserialize<Dictionary<string, object?>>()!, Ct);
        Assert.Contains("TP-1002", Paths.Cli(workspace, "plans").Output);
        Assert.Equal(0, Paths.Cli(workspace, "run", "TP-1002").ExitCode);

        // Path D and path C both analyze that run, and agree with path B's summary.
        var cliSummary = Paths.Cli(workspace, "summary", "R-1002");
        await using var agent = await Paths.Mcp(workspace, Ct);
        var mcpSummary = (await agent.Call("get_run_summary", new() { ["runId"] = "R-1002" }, Ct)).GetProperty("summary");
        // The chat panel's tool, called directly: the same function the model's request would run.
        Assert.True(CapabilityCatalog.FromObject(new TestBenchCapabilities(new TestBenchService(workspace, BenchOptions.Instant)))
            .TryGet("get_run_summary", out var chatTool));
        var chatSummary = JsonSerializer.SerializeToElement(
            await chatTool.Function.InvokeAsync(new() { ["runId"] = "R-1002" }, Ct), CapabilityCatalog.SerializerOptions).GetProperty("summary");

        foreach (var summary in new[] { mcpSummary, chatSummary })
        {
            Assert.Contains($"{summary.GetProperty("failed").GetInt32()} failed", cliSummary.Output);
            Assert.Contains($"→ {summary.GetProperty("verdict").GetString()}", cliSummary.Output);
            foreach (var finding in summary.GetProperty("findings").EnumerateArray())
                Assert.Contains(finding.GetProperty("text").GetString()!, cliSummary.Output);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>What a finished job produced, without what is expected to differ between paths (ids, times, who made it).</summary>
public sealed record RunOutcome(string Device, string Conditions, string Tests, int Measurements, int Failed, string Verdict, string Findings)
{
    public static RunOutcome Read(string workspace, string runId)
    {
        var bench = new TestBenchService(workspace, BenchOptions.Instant);
        var run = bench.GetRun(runId);
        Assert.Equal(RunStatus.Completed, run.Status);
        var plan = bench.ListPlans().Single(p => p.Id == run.PlanId);
        var xml = System.Xml.Linq.XDocument.Parse(bench.GetPlanXml(plan.Id)).Root!;
        var summary = bench.Analyze(runId, MeasurementFilter.All);
        return new RunOutcome(
            plan.Device,
            xml.Element("Conditions")!.ToString(),
            xml.Element("Tests")!.ToString(),
            summary.Measurements,
            summary.Failed,
            summary.Verdict.ToString(),
            string.Join("\n", summary.Findings.Select(f => $"[{f.Severity}] {f.Text}")));
    }
}
