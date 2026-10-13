using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Demo.Cli.Tests;

/// <summary>End to end: start the real console exe the way a coding agent does, and talk MCP to it over stdio.</summary>
public sealed class StdioServerTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "mcp-stdio", Guid.NewGuid().ToString("N"));

    private static string CliExe => Path.Combine(AppContext.BaseDirectory, "demo-cli.exe");

    private async Task<McpClient> Connect(params string[] flags) =>
        await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "demo-cli",
            Command = CliExe,
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
        Assert.Contains("does not exist", Text(result));
    }

    [Fact]
    public void Ordinary_commands_still_work_without_any_AI()
    {
        var plans = RunCli("plans");
        var created = RunCli("plan from-spec LDO-3300_datasheet.csv --temps 25,125 --samples 2 --tests IQ");
        var run = RunCli("run TP-1002");

        Assert.Contains("TP-1001", plans);
        Assert.Contains("Created TP-1002 (4 measurements)", created);
        Assert.Contains("R-1002: Completed", run);
    }

    private string RunCli(string arguments)
    {
        var info = new ProcessStartInfo(CliExe, $"--workspace \"{_workspace}\" {arguments}") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment["DEMO_BENCH_DELAY_MS"] = "0";
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));

    private static JsonElement Json(CallToolResult result) => JsonDocument.Parse(Text(result)).RootElement;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
