using System.Collections.Concurrent;
using System.IO.Pipes;
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
        var pipe = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000, Ct);
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
        await second.DisposeAsync();
        await _pipes[second].DisposeAsync(); // the agent hangs up

        Assert.Equal(["claude-code", "codex"], _user.Runs.Select(r => r.Agent).Order());
        await WaitUntil(() => _user.Connected == 1);
    }

    [Fact]
    public async Task A_second_app_can_not_open_the_same_door()
    {
        Open();

        Assert.ThrowsAny<IOException>(() => Open());
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

    /// <summary>The person at the app: answers every approval card with <see cref="Answer"/>.</summary>
    private sealed class FakeUser : IAgentSupervisor
    {
        private int _connected;
        public bool Answer { get; set; }
        public ConcurrentQueue<AgentCall> Asked { get; } = new();
        public ConcurrentQueue<AgentCall> Runs { get; } = new();
        public int Connected => Volatile.Read(ref _connected);

        public void AgentConnected() => Interlocked.Increment(ref _connected);
        public void AgentDisconnected() => Interlocked.Decrement(ref _connected);

        public Task<bool> ApproveAsync(AgentCall call, CancellationToken cancellationToken)
        {
            Asked.Enqueue(call);
            return Task.FromResult(Answer);
        }

        public void Ran(AgentCall call) => Runs.Enqueue(call);
    }
}
