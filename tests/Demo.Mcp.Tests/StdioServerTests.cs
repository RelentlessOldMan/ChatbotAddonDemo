using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Demo.Mcp.Tests;

/// <summary>End-to-end: spawn the real console exe as an agent would, and talk MCP to it.</summary>
public sealed class StdioServerTests : IAsyncLifetime
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"mcp-stdio-{Guid.NewGuid():N}.json");

    private static string CliExe => Path.Combine(AppContext.BaseDirectory, "demo-cli.exe");

    private async Task<McpClient> Connect(params string[] extraArgs) =>
        await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "demo-cli",
            Command = CliExe,
            Arguments = ["--data", _dataPath, "mcp", .. extraArgs],
        }), cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public async Task Default_is_read_only_and_tools_carry_annotations()
    {
        await using var client = await Connect();

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("demo-cli", client.ServerInfo.Name);
        Assert.Contains(tools, t => t.Name == "list_overdue_orders");
        Assert.DoesNotContain(tools, t => t.Name == "adjust_stock");
        Assert.All(tools, t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint));
        Assert.Contains("read-only", client.ServerInstructions);
    }

    [Fact]
    public async Task Allow_writes_exposes_destructive_tools_that_change_the_data_file()
    {
        await using var client = await Connect("--allow-writes");
        var ct = TestContext.Current.CancellationToken;

        var tools = await client.ListToolsAsync(cancellationToken: ct);
        var adjust = Assert.Single(tools, t => t.Name == "adjust_stock");
        Assert.True(adjust.ProtocolTool.Annotations?.DestructiveHint);
        Assert.False(adjust.ProtocolTool.Annotations?.ReadOnlyHint);

        var result = await client.CallToolAsync("adjust_stock", new Dictionary<string, object?> { ["sku"] = "FLT-031", ["delta"] = 4, ["reason"] = "received" }, cancellationToken: ct);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(5, JsonDocument.Parse(Text(result)).RootElement.GetProperty("onHand").GetInt32());
        Assert.Contains("\"OnHand\": 5", await File.ReadAllTextAsync(_dataPath, ct));
    }

    [Fact]
    public async Task Domain_errors_are_returned_as_readable_results()
    {
        await using var client = await Connect();
        var result = await client.CallToolAsync("get_work_order", new Dictionary<string, object?> { ["id"] = "WO-404" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("does not exist", Text(result));
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        File.Delete(_dataPath);
        return ValueTask.CompletedTask;
    }
}
