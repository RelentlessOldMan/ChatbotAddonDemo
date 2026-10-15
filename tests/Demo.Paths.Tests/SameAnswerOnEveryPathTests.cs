using System.Globalization;
using System.Text.Json;
using Demo.Ai;
using Demo.Ai.Credentials;
using Demo.Capabilities;
using Demo.Capabilities.TestBench;
using Demo.Domain;
using ModelContextProtocol.Client;

namespace Demo.Paths.Tests;

/// <summary>
/// Every path ends in the same core logic, so they must agree: the same job gives the same plan and the same findings
/// whichever path does it. And within each app, what one path makes, the others see. (The two apps are separate
/// programs with separate workspaces, so nothing is shared between them.) Free: no real model is used (path C talks
/// to the fake gateway; paths D1 and D2 are driven by an MCP client, as a coding agent would drive them).
/// </summary>
public sealed class SameAnswerOnEveryPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "paths-tests", Guid.NewGuid().ToString("N"));
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Workspace(string name) => Path.Combine(_root, name);

    private static Dictionary<string, object?> PlanArguments => Scenario.PlanArguments().Deserialize<Dictionary<string, object?>>()!;

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

    /// <summary>The job on path C: one chat message, approved twice; the tools are called through the hub (C3).</summary>
    private async Task<RunOutcome> DoJobInChat(string workspace, AiProvider provider)
    {
        await using var gateway = await FakeAiGateway.StartAsync(Scenario.ScriptedModel);
        await using var tools = await Paths.DesktopHub(workspace).ConnectChatAsync([], Ct);
        var credential = new ResolvedCredential(CredentialSource.Gateway, "token", gateway.BaseUrl);
        var model = ProviderInfo.For(provider).DefaultModel;
        using var client = ChatClientFactory.Create(provider, model, credential);
        var session = new ChatSession(client, tools.Catalog, new UsageMeter(model, null), ChatSessionOptions.For(TestBenchCapabilities.Instructions));

        var turn = await session.SendAsync(Scenario.Question, (_, _) => Task.FromResult(true), Ct);
        return RunOutcome.Read(workspace, Scenario.RunId().Match(turn.Text).Value);
    }

    /// <summary>The job on path D1 or D2: the tool calls a coding agent would make over MCP.</summary>
    private async Task<RunOutcome> DoJobOverMcp(McpClient agent, string workspace)
    {
        var plan = await agent.Call("create_test_plan_from_spec", PlanArguments, Ct);
        var started = await agent.Call("start_test_run", new() { ["planId"] = plan.GetProperty("plan").GetProperty("id").GetString() }, Ct);
        var runId = started.GetProperty("run").GetProperty("id").GetString()!;
        var finished = await agent.Call("wait_for_test_run", new() { ["runId"] = runId, ["maxSeconds"] = 60 }, Ct);
        Assert.Equal("Completed", finished.GetProperty("status").GetString());
        return RunOutcome.Read(workspace, runId);
    }

    private async Task<RunOutcome> DoJobThroughTheOpenApp(string workspace)
    {
        var (agent, door) = await Paths.AgentToOpenApp(Paths.DesktopHub(workspace), Ct);
        await using (door)
        await using (agent)
            return await DoJobOverMcp(agent, workspace);
    }

    private async Task<RunOutcome> DoJobThroughTheConsoleTool(string workspace)
    {
        await using var agent = await Paths.Mcp(workspace, Ct);
        return await DoJobOverMcp(agent, workspace);
    }

    [Fact]
    public async Task The_same_job_gives_the_same_plan_and_findings_on_paths_B_C_D1_and_D2()
    {
        var onCommandLine = DoJobOnCommandLine(Workspace("b"));
        var inClaudeChat = await DoJobInChat(Workspace("c-claude"), AiProvider.Anthropic);
        var inOpenAiChat = await DoJobInChat(Workspace("c-openai"), AiProvider.OpenAI);
        var throughOpenApp = await DoJobThroughTheOpenApp(Workspace("d1"));
        var throughConsoleTool = await DoJobThroughTheConsoleTool(Workspace("d2"));

        Assert.Equal(Scenario.Measurements, onCommandLine.Measurements);
        Assert.Equal(onCommandLine, inClaudeChat);
        Assert.Equal(onCommandLine, inOpenAiChat);
        Assert.Equal(onCommandLine, throughOpenApp);
        Assert.Equal(onCommandLine, throughConsoleTool);
    }

    [Fact]
    public async Task In_the_desktop_app_what_an_agent_makes_the_screens_and_the_chat_see()
    {
        var workspace = Workspace("desktop");
        var hub = Paths.DesktopHub(workspace);
        var screens = new TestBenchService(workspace, BenchOptions.Instant); // what the screens call (path A)

        // Path D1 (an agent) makes the plan; path A (the screens) sees it and runs it.
        var (agent, door) = await Paths.AgentToOpenApp(hub, Ct);
        await using var closeDoor = door;
        await using var disconnect = agent;
        await agent.Call("create_test_plan_from_spec", PlanArguments, Ct);
        Assert.Contains(screens.ListPlans(), p => p.Id == "TP-1002");
        var run = screens.StartRun("TP-1002");
        await screens.WaitForRunAsync(run.Id, TimeSpan.FromSeconds(60), Ct);

        // Path D1 and path C (the chat's tool, through the hub) both analyze that run, and agree with the screens.
        var agentSummary = (await agent.Call("get_run_summary", new() { ["runId"] = run.Id }, Ct)).GetProperty("summary");
        await using var tools = await hub.ConnectChatAsync([], Ct);
        Assert.True(tools.Catalog.TryGet("get_run_summary", out var chatTool));
        var chatSummary = JsonSerializer.SerializeToElement(
            await chatTool.Function.InvokeAsync(new() { ["runId"] = run.Id }, Ct), CapabilityCatalog.SerializerOptions).GetProperty("summary");

        var onScreen = screens.Analyze(run.Id, MeasurementFilter.All);
        foreach (var summary in new[] { agentSummary, chatSummary })
        {
            Assert.Equal(onScreen.Failed, summary.GetProperty("failed").GetInt32());
            Assert.Equal(onScreen.Verdict.ToString(), summary.GetProperty("verdict").GetString());
            Assert.Equal(onScreen.Findings.Select(f => f.Text), summary.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("text").GetString()));
        }
    }

    [Fact]
    public async Task In_the_console_tool_what_an_agent_makes_the_command_line_sees()
    {
        var workspace = Workspace("console");

        // Path D2 (an agent) makes the plan; path B (a person at the command line) sees it and runs it.
        await using var agent = await Paths.Mcp(workspace, Ct);
        await agent.Call("create_test_plan_from_spec", PlanArguments, Ct);
        Assert.Contains("TP-1002", Paths.Cli(workspace, "plans").Output);
        Assert.Equal(0, Paths.Cli(workspace, "run", "TP-1002").ExitCode);

        // Path D2 analyzes that run, and agrees with path B's summary.
        var cliSummary = Paths.Cli(workspace, "summary", "R-1002");
        var summary = (await agent.Call("get_run_summary", new() { ["runId"] = "R-1002" }, Ct)).GetProperty("summary");
        Assert.Contains($"{summary.GetProperty("failed").GetInt32()} failed", cliSummary.Output);
        Assert.Contains($"→ {summary.GetProperty("verdict").GetString()}", cliSummary.Output);
        foreach (var finding in summary.GetProperty("findings").EnumerateArray())
            Assert.Contains(finding.GetProperty("text").GetString()!, cliSummary.Output);
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
