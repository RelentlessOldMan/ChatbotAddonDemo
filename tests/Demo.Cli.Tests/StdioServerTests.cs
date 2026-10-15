using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Demo.Domain;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Demo.Cli.Tests;

/// <summary>End to end: start the real console exe the way a coding agent does, and talk MCP to it over stdio.</summary>
public sealed class StdioServerTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "mcp-stdio", Guid.NewGuid().ToString("N"));

    private async Task<McpClient> Connect(params string[] flags) =>
        await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "demo-cli",
            Command = CliProcess.Exe,
            Arguments = ["--workspace", _workspace, "mcp", .. flags],
            EnvironmentVariables = new Dictionary<string, string?> { ["DEMO_BENCH_DELAY_MS"] = "0" },
        }), cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public async Task Default_is_read_only_and_tools_carry_annotations()
    {
        await using var client = await Connect();

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("testbench", client.ServerInfo.Name);
        Assert.Contains(tools, t => t.Name == "get_run_summary");
        Assert.DoesNotContain(tools, t => t.Name is "create_test_plan" or "start_test_run");
        Assert.All(tools, t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint));
        Assert.Contains("read-only", client.ServerInstructions);
        Assert.Contains("get_bench_info", client.ServerInstructions); // the same domain instructions the in-app assistant gets
    }

    [Theory]
    [InlineData("--allow-writes", "create_test_plan", "start_test_run")]
    [InlineData("--allow-runs", "start_test_run", "create_test_plan")]
    public async Task Writes_and_bench_runs_are_allowed_separately(string flag, string exposed, string hidden)
    {
        await using var client = await Connect(flag);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        var tool = Assert.Single(tools, t => t.Name == exposed);
        Assert.False(tool.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.DoesNotContain(tools, t => t.Name == hidden);
        Assert.Equal(exposed == "start_test_run", tool.ProtocolTool.Annotations?.OpenWorldHint);
    }

    [Fact]
    public async Task A_hidden_tool_cannot_be_called_by_name_either()
    {
        await using var client = await Connect("--allow-writes");

        // An agent that knows the name from elsewhere (docs, an earlier session) still cannot use the bench.
        await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await client.CallToolAsync("start_test_run", new Dictionary<string, object?> { ["planId"] = "TP-1001" }, cancellationToken: TestContext.Current.CancellationToken));

        var bench = new TestBenchService(_workspace, BenchOptions.Instant);
        Assert.Null(bench.BenchBusyWith());
        Assert.Equal(["R-1001"], bench.ListRuns(null).Select(r => r.Id));
    }

    [Fact]
    public async Task Cancel_is_marked_destructive_and_rewriting_a_report_idempotent()
    {
        await using var client = await Connect("--allow-writes", "--allow-runs");

        var tools = (await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken))
            .ToDictionary(t => t.Name, t => t.ProtocolTool.Annotations);

        Assert.True(tools["cancel_test_run"]?.DestructiveHint);
        Assert.False(tools["start_test_run"]?.DestructiveHint);
        Assert.False(tools["create_test_plan"]?.DestructiveHint);
        Assert.False(tools["generate_report"]?.DestructiveHint);
        Assert.True(tools["generate_report"]?.IdempotentHint);
    }

    [Fact]
    public async Task An_agent_can_plan_run_and_analyze_and_the_files_land_in_the_workspace()
    {
        await using var client = await Connect("--allow-writes", "--allow-runs");
        var ct = TestContext.Current.CancellationToken;

        var created = Json(await client.CallToolAsync("create_test_plan_from_spec", new Dictionary<string, object?>
        {
            ["specFile"] = "LDO-3300_datasheet.csv",
            ["samples"] = 3,
            ["temperaturesC"] = new[] { -40, 25, 85, 125 },
        }, cancellationToken: ct));
        var planId = created.GetProperty("plan").GetProperty("id").GetString();

        var started = Json(await client.CallToolAsync("start_test_run", new Dictionary<string, object?> { ["planId"] = planId }, cancellationToken: ct));
        var runId = started.GetProperty("run").GetProperty("id").GetString();
        Json(await client.CallToolAsync("wait_for_test_run", new Dictionary<string, object?> { ["runId"] = runId }, cancellationToken: ct));
        var summary = Json(await client.CallToolAsync("get_run_summary", new Dictionary<string, object?> { ["runId"] = runId }, cancellationToken: ct));

        Assert.Equal("Fail", summary.GetProperty("summary").GetProperty("verdict").GetString());
        var folder = summary.GetProperty("files").GetProperty("folder").GetString()!;
        Assert.StartsWith(_workspace, folder);
        Assert.True(File.Exists(Path.Combine(folder, "results.csv")));
        Assert.True(File.Exists(Path.Combine(folder, "plots", "IQ.png")));
    }

    [Fact]
    public async Task Domain_errors_are_returned_as_readable_results()
    {
        await using var client = await Connect();
        var result = await client.CallToolAsync("get_test_plan", new Dictionary<string, object?> { ["planId"] = "TP-404" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Contains("does not exist", Text(result));
    }

    [Theory]
    [InlineData("""{"runId":"R-1001","temperatureC":"hot"}""", """'temperatureC' must be a number, not "hot".""")]
    [InlineData("""{"runId":"R-1001","dut":1.5}""", "'dut' must be a whole number, not 1.5.")]
    [InlineData("""{"temperatureC":25}""", "'runId' is required.")]
    public async Task Arguments_that_do_not_fit_are_errors_that_name_the_argument(string arguments, string problem)
    {
        await using var client = await Connect();
        var result = await client.CallToolAsync("get_run_summary", JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments)!,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Equal($"Invalid arguments for get_run_summary: {problem}", Text(result));
    }

    [Fact]
    public async Task Results_are_compact_json()
    {
        await using var client = await Connect();
        var result = await client.CallToolAsync("list_test_plans", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(true, result.IsError);
        Assert.StartsWith("[{\"id\":\"TP-1001\",", Text(result));
        Assert.DoesNotContain("\n", Text(result));
    }

    [Fact]
    public async Task Closing_the_connection_cancels_a_run_the_agent_started()
    {
        // Raw stdio rather than McpClient: disposing McpClient waits and then kills the server, while an agent
        // that quits closes the server's stdin, which is the signal under test here.
        var info = new ProcessStartInfo(CliProcess.Exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "--workspace", _workspace, "mcp", "--allow-writes", "--allow-runs" }) info.ArgumentList.Add(a);
        info.Environment.Remove("DEMO_BENCH_DELAY_MS"); // demo speed
        using var server = Process.Start(info)!;
        var errors = server.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            await Request(server, 1, "initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" } });
            await server.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized" }));
            // A big plan: over a thousand measurements take most of a minute at demo speed, so the run is still going when
            // the agent quits however slow the machine is (TP-1001's 45 take about a second).
            var created = await Request(server, 2, "tools/call", new
            {
                name = "create_test_plan_from_spec",
                arguments = new { specFile = "LDO-3300_datasheet.csv", samples = 10, temperaturesC = new[] { -40, -20, 0, 25, 50, 85, 105, 125 }, supplyVoltages = new[] { 3.6, 4.2, 5, 5.5, 6 } },
            });
            var plan = JsonDocument.Parse(created.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.GetProperty("plan");
            Assert.True(plan.GetProperty("measurementCount").GetInt32() >= 1000);
            var planId = plan.GetProperty("id").GetString();
            var result = await Request(server, 3, "tools/call", new { name = "start_test_run", arguments = new { planId } });
            var run = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.GetProperty("run");
            Assert.Equal("Running", run.GetProperty("status").GetString());

            server.StandardInput.Close(); // the agent quits

            Assert.True(server.WaitForExit(TimeSpan.FromSeconds(15)), "the server should exit when its client goes away");
            Assert.True(server.ExitCode == 0, await errors);
            var bench = new TestBenchService(_workspace, BenchOptions.Instant);
            Assert.Equal(RunStatus.Cancelled, bench.GetRun(run.GetProperty("id").GetString()!).Status);
            Assert.Null(bench.BenchBusyWith());
        }
        finally
        {
            if (!server.HasExited) server.Kill(entireProcessTree: true);
        }
    }

    private static async Task<JsonElement> Request(Process server, int id, string method, object parameters)
    {
        await server.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
        await server.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
        while (await server.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken) is { } line)
        {
            var message = JsonDocument.Parse(line).RootElement;
            if (message.TryGetProperty("id", out var replyTo) && replyTo.ValueKind == JsonValueKind.Number && replyTo.GetInt32() == id)
                return message.GetProperty("result");
        }
        throw new EndOfStreamException($"The server closed stdout before answering {method}.");
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));

    private static JsonElement Json(CallToolResult result) => JsonDocument.Parse(Text(result)).RootElement;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
