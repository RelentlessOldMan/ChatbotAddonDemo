using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Demo.Capabilities;
using Demo.Capabilities.TestBench;
using Demo.Domain;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Demo.Mcp.Tests;

/// <summary>Path D1: a coding agent connects to the open app through the hub's named pipe, or the relay in front of it.</summary>
public sealed class AgentDoorTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "mcp-door", Guid.NewGuid().ToString("N"));
    private readonly string _pipe = $"ChatbotAddonDemo.Tests.{Guid.NewGuid():N}";
    private readonly FakeUser _user = new();
    private readonly List<IAsyncDisposable> _cleanup = [];
    private readonly Dictionary<McpClient, NamedPipeClientStream> _pipes = [];

    private TestBenchService Bench() => new(_workspace, BenchOptions.Instant);

    private AgentDoor Open()
    {
        var hub = new McpHub(CapabilityCatalog.FromObject(new TestBenchCapabilities(Bench())), "testbench", "Use the tools.");
        var door = hub.OpenAgentDoor(_pipe, _user);
        _cleanup.Add(door);
        return door;
    }

    private async Task<McpClient> ConnectAgent(string name = "claude-code")
    {
        var pipe = await AgentRelay.ConnectToAppAsync(_pipe, TimeSpan.FromSeconds(5), Ct);
        var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe),
            new McpClientOptions { ClientInfo = new Implementation { Name = name, Version = "1" } }, cancellationToken: Ct);
        _cleanup.Add(pipe);
        _cleanup.Add(client);
        _pipes[client] = pipe;
        return client;
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static readonly Dictionary<string, object?> NewPlan = new() { ["specFile"] = "LDO-3300_datasheet.csv", ["samples"] = 2 };

    [Fact]
    public async Task An_agent_sees_every_tool_with_its_annotations()
    {
        Open();
        var agent = await ConnectAgent();

        var tools = await agent.ListToolsAsync(cancellationToken: Ct);

        Assert.Equal("testbench", agent.ServerInfo.Name);
        Assert.Equal("Use the tools.", agent.ServerInstructions);
        Assert.True(tools.Single(t => t.Name == "get_run_summary").ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.False(tools.Single(t => t.Name == "start_test_run").ProtocolTool.Annotations?.ReadOnlyHint);
    }

    [Fact]
    public async Task Reads_run_without_asking_and_the_app_shows_them()
    {
        Open();
        var agent = await ConnectAgent();

        var result = await agent.CallToolAsync("list_test_plans", cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("TP-1001", Text(result));
        Assert.Empty(_user.Asked);
        var ran = Assert.Single(_user.Runs);
        Assert.Equal(("claude-code", "list_test_plans"), (ran.Agent, ran.Capability.Name));
    }

    [Fact]
    public async Task A_change_waits_for_the_user_and_runs_once_approved()
    {
        Open();
        var agent = await ConnectAgent();
        _user.Answer = true;

        var result = await agent.CallToolAsync("create_test_plan_from_spec", NewPlan, cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        var asked = Assert.Single(_user.Asked);
        Assert.Equal(("claude-code", "create_test_plan_from_spec", SafetyClass.Write), (asked.Agent, asked.Capability.Name, asked.Capability.Safety));
        Assert.Equal("LDO-3300_datasheet.csv", asked.Arguments["specFile"]?.ToString());
        Assert.Equal(asked.Id, Assert.Single(_user.Runs).Id);
        Assert.Contains(Bench().ListPlans(), p => Text(result).Contains($"\"{p.Id}\"") && p.Samples == 2);
    }

    [Fact]
    public async Task Nothing_happens_until_the_user_answers()
    {
        Open();
        var agent = await ConnectAgent();
        var before = Bench().ListPlans().Count;
        _user.Hold = true;

        var call = agent.CallToolAsync("create_test_plan_from_spec", NewPlan, cancellationToken: Ct).AsTask();
        await WaitUntil(() => _user.Asked.Count == 1);
        await Task.Delay(300, Ct); // time enough to have run, had it not waited

        Assert.False(call.IsCompleted);
        Assert.Equal(before, Bench().ListPlans().Count);
        _user.Decide(true);
        Assert.NotEqual(true, (await call).IsError);
        Assert.Equal(before + 1, Bench().ListPlans().Count);
    }

    [Theory]
    [InlineData("start_test_run", "planId", "TP-1001", SafetyClass.Execute)]
    [InlineData("cancel_test_run", "runId", "R-1001", SafetyClass.Execute)]
    [InlineData("generate_report", "runId", "R-1001", SafetyClass.Write)]
    public async Task Bench_actions_and_reports_are_asked_about_too(string tool, string parameter, string value, SafetyClass safety)
    {
        Open();
        var agent = await ConnectAgent();
        var runsBefore = Bench().ListRuns().Select(r => (r.Id, r.Status)).ToList();
        _user.Answer = false;

        var result = await agent.CallToolAsync(tool, new Dictionary<string, object?> { [parameter] = value }, cancellationToken: Ct);

        var asked = Assert.Single(_user.Asked);
        Assert.Equal((tool, safety), (asked.Capability.Name, asked.Capability.Safety));
        Assert.Equal(AgentDoor.Declined, Text(result));
        Assert.Empty(_user.Runs);
        Assert.Equal(runsBefore, Bench().ListRuns().Select(r => (r.Id, r.Status)).ToList());
    }

    [Fact]
    public async Task When_the_agent_hangs_up_its_card_is_withdrawn_and_nothing_runs()
    {
        Open();
        var agent = await ConnectAgent();
        var before = Bench().ListPlans().Count;
        _user.Hold = true;

        _ = agent.CallToolAsync("create_test_plan_from_spec", NewPlan, cancellationToken: Ct).AsTask();
        await WaitUntil(() => _user.Asked.Count == 1);
        await _pipes[agent].DisposeAsync(); // the agent quits (Ctrl+C, or its terminal is closed): no goodbye, just gone

        await WaitUntil(() => _user.Withdrawn);
        await WaitUntil(() => _user.Connected == 0);
        _user.Decide(true); // the user approves the card anyway, too late
        await Task.Delay(300, Ct);
        Assert.Empty(_user.Runs);
        Assert.Equal(before, Bench().ListPlans().Count);
    }

    [Fact]
    public async Task Only_this_Windows_user_can_open_the_pipe_and_never_from_another_computer()
    {
        Open();
        await ConnectAgent();
        var pipe = _pipes.Values.Single();

        var security = pipe.GetAccessControl();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        var me = WindowsIdentity.GetCurrent().User!;

        Assert.Equal(me, security.GetOwner(typeof(SecurityIdentifier)));
        Assert.All(rules.Where(r => r.AccessControlType == AccessControlType.Allow), r => Assert.Equal(me, r.IdentityReference));
        Assert.Contains(rules, r => r.AccessControlType == AccessControlType.Deny
            && r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.NetworkSid, null)));
    }

    [Fact]
    public async Task A_denied_change_is_an_error_and_nothing_is_created()
    {
        Open();
        var agent = await ConnectAgent();
        var before = Bench().ListPlans().Count;
        _user.Answer = false;

        var result = await agent.CallToolAsync("create_test_plan_from_spec", NewPlan, cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Equal(AgentDoor.Declined, Text(result));
        Assert.Equal(before, Bench().ListPlans().Count);
        Assert.Empty(_user.Runs);
    }

    [Fact]
    public async Task Several_agents_can_be_connected_and_each_is_counted()
    {
        Open();
        var first = await ConnectAgent("claude-code");
        var second = await ConnectAgent("codex");

        await first.CallToolAsync("list_test_plans", cancellationToken: Ct);
        await second.CallToolAsync("list_test_plans", cancellationToken: Ct);

        Assert.Equal(2, _user.Connected);
        await second.DisposeAsync();
        await _pipes[second].DisposeAsync(); // the agent hangs up

        Assert.Equal(["claude-code", "codex"], _user.Runs.Select(r => r.Agent).Order());
        await WaitUntil(() => _user.Connected == 1);
    }

    [Fact]
    public void A_second_app_can_not_open_the_same_door()
    {
        Open();

        var refused = Assert.ThrowsAny<IOException>(() => Open());
        Assert.Equal("Another TestBench window already lets coding agents connect.", refused.Message);
    }

    [Fact]
    public async Task Closing_the_door_ends_every_agent_conversation()
    {
        var door = Open();
        var agent = await ConnectAgent();

        await door.DisposeAsync();

        await WaitUntil(() => _user.Connected == 0);
        await Assert.ThrowsAnyAsync<Exception>(() => agent.CallToolAsync("list_test_plans", cancellationToken: Ct).AsTask());
    }

    [Fact]
    public async Task The_relay_says_what_to_do_when_the_app_isnt_open()
    {
        var errors = new StringWriter();

        var exit = await AgentRelay.RunAsync(_pipe, new MemoryStream(), new MemoryStream(), errors, TimeSpan.FromMilliseconds(200), Ct);

        Assert.Equal(1, exit);
        Assert.Equal(AgentRelay.NotRunning, errors.ToString().Trim());
    }

    [Fact]
    public async Task Through_the_relay_an_agent_reaches_the_open_app()
    {
        Open();
        // The agent's side of the relay's stdin and stdout.
        System.IO.Pipelines.Pipe stdin = new(), stdout = new();
        var relay = AgentRelay.RunAsync(_pipe, stdin.Reader.AsStream(), stdout.Writer.AsStream(), TextWriter.Null, TimeSpan.FromSeconds(5), Ct);
        var agent = await McpClient.CreateAsync(new StreamClientTransport(stdin.Writer.AsStream(), stdout.Reader.AsStream()), cancellationToken: Ct);

        var result = await agent.CallToolAsync("get_bench_info", cancellationToken: Ct);
        await agent.DisposeAsync();
        stdin.Writer.Complete(); // the agent closes the relay's stdin when it's done

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(0, await relay.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        await WaitUntil(() => _user.Connected == 0);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50, Ct);
        Assert.True(condition());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var item in Enumerable.Reverse(_cleanup))
        {
            try { await item.DisposeAsync(); } catch (Exception) { } // a client whose door already closed
        }
        try { Directory.Delete(_workspace, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// The person at the app: answers every approval card with <see cref="Answer"/>, or, with <see cref="Hold"/>,
    /// leaves it waiting until <see cref="Decide"/>, the way a card waits in the app.
    /// </summary>
    private sealed class FakeUser : IAgentSupervisor
    {
        private int _connected;
        private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Answer { get; set; }
        public bool Hold { get; set; }
        public ConcurrentQueue<AgentCall> Asked { get; } = new();
        public ConcurrentQueue<AgentCall> Runs { get; } = new();
        public int Connected => Volatile.Read(ref _connected);
        public bool Withdrawn { get; private set; }

        public void AgentConnected() => Interlocked.Increment(ref _connected);
        public void AgentDisconnected() => Interlocked.Decrement(ref _connected);

        public void Decide(bool approve) => _decision.TrySetResult(approve);

        public async Task<bool> ApproveAsync(AgentCall call, CancellationToken cancellationToken)
        {
            Asked.Enqueue(call);
            if (!Hold) return Answer;
            try { return await _decision.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                Withdrawn = true; // the app takes the card down
                throw;
            }
        }

        public void Ran(AgentCall call) => Runs.Enqueue(call);
    }
}
