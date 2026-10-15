using System.ComponentModel;
using System.IO.Pipelines;
using System.Text.Json;
using Demo.Capabilities;
using Demo.Capabilities.TestBench;
using Demo.Domain;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Demo.Mcp.Tests;

/// <summary>Path C3: the chat panel's tools, as they come back through the hub.</summary>
public sealed class HubTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "mcp-hub", Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _cleanup = [];
    private readonly Dictionary<string, McpServerTool[]> _fakeServers = [];

    private CapabilityCatalog Direct() => CapabilityCatalog.FromObject(new TestBenchCapabilities(new TestBenchService(_workspace, BenchOptions.Instant)));

    private McpHub Hub() => new(Direct(), "testbench", "instructions") { OutsideTransport = ConnectToFake };

    /// <summary>Stands in for Perforce, GitHub and the like: a real MCP server, reached over in-memory pipes.</summary>
    private IClientTransport ConnectToFake(OutsideServer server)
    {
        if (!_fakeServers.TryGetValue(server.Name, out var tools)) return server.CreateTransport();
        Pipe toServer = new(), toClient = new();
        var fake = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()),
            new McpServerOptions { ServerInfo = new() { Name = server.Name, Version = "1" }, ToolCollection = [.. tools] });
        _ = fake.RunAsync();
        _cleanup.Add(fake);
        return new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream());
    }

    private void FakeGitHub() => _fakeServers["github"] =
    [
        McpServerTool.Create(([Description("Words to look for.")] string query) => $"2 issues match '{query}'",
            new McpServerToolCreateOptions { Name = "search_issues", Title = "Search issues", ReadOnly = true }),
        McpServerTool.Create(([Description("Issue title.")] string title) => new { number = 7, title },
            new McpServerToolCreateOptions { Name = "create_issue", Title = "Create an issue" }),
        McpServerTool.Create(() => new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "Repository not found" }] },
            new McpServerToolCreateOptions { Name = "get.repo" }),
    ];

    private static OutsideServer Server(string name, bool trusted = false) => new(name, $"{name}-mcp.exe", trusted, ServerOrigin.User);

    private async Task<HubTools> Connect(McpHub hub, params OutsideServer[] outside)
    {
        var tools = await hub.ConnectChatAsync(outside, Ct);
        _cleanup.Add(tools);
        return tools;
    }

    [Fact]
    public async Task The_chat_gets_the_same_tools_through_the_hub_as_the_catalog_has()
    {
        var direct = Direct();

        var tools = await Connect(Hub());

        Assert.Equal(direct.Capabilities.Select(c => c.Name), tools.Catalog.Capabilities.Select(c => c.Name));
        Assert.Equal(direct.Capabilities.Select(c => c.Safety), tools.Catalog.Capabilities.Select(c => c.Safety));
        Assert.Equal(direct.Capabilities.Select(c => c.Title), tools.Catalog.Capabilities.Select(c => c.Title));
        Assert.Equal(direct.Capabilities.Select(c => c.Description), tools.Catalog.Capabilities.Select(c => c.Description));
        Assert.Equal(direct.Capabilities.Select(c => c.Function.JsonSchema.GetRawText()), tools.Catalog.Capabilities.Select(c => c.Function.JsonSchema.GetRawText()));
        Assert.All(tools.Catalog.Capabilities, c => Assert.Null(c.Server));
        Assert.Empty(tools.Problems);
    }

    [Fact]
    public async Task Results_come_back_as_the_JSON_the_tool_returned()
    {
        var tools = await Connect(Hub());

        var result = await tools.Catalog["list_test_plans"].Function.InvokeAsync(new(), Ct);

        var plans = Assert.IsType<JsonElement>(result);
        Assert.Equal(JsonValueKind.Array, plans.ValueKind);
        Assert.Contains(plans.EnumerateArray(), p => p.GetProperty("id").GetString() == "TP-1001");
    }

    [Fact]
    public async Task A_business_rule_error_reads_the_same_as_without_the_hub()
    {
        var tools = await Connect(Hub());

        var result = await tools.Catalog["get_test_plan"].Function.InvokeAsync(new() { ["planId"] = "TP-9999" }, Ct);
        var direct = await Direct()["get_test_plan"].Function.InvokeAsync(new() { ["planId"] = "TP-9999" }, Ct);

        Assert.Equal(Assert.IsType<CapabilityError>(direct), Assert.IsType<CapabilityError>(result));
    }

    [Fact]
    public async Task A_change_through_the_hub_happens_in_the_app()
    {
        var tools = await Connect(Hub());

        var result = (JsonElement)(await tools.Catalog["create_test_plan_from_spec"].Function.InvokeAsync(
            new() { ["specFile"] = "LDO-3300_datasheet.csv", ["samples"] = 2 }, Ct))!;

        var id = result.GetProperty("plan").GetProperty("id").GetString()!;
        Assert.Contains(new TestBenchService(_workspace, BenchOptions.Instant).ListPlans(), p => p.Id == id);
    }

    [Fact]
    public async Task Outside_tools_are_prefixed_and_need_approval()
    {
        FakeGitHub();

        var tools = await Connect(Hub(), Server("github"));

        var search = tools.Catalog["github_search_issues"];
        Assert.Equal(SafetyClass.Execute, search.Safety); // read-only by its own account, but that account isn't trusted
        Assert.Equal("github", search.Server);
        Assert.Equal("github: Search issues", search.Title);
        Assert.True(tools.Catalog.TryGet("github_get_repo", out _)); // '.' isn't allowed in a tool name by every provider
        Assert.Contains(tools.Catalog.Capabilities, c => c.Name == "get_run_summary");
    }

    [Fact]
    public async Task IT_can_mark_a_server_safe_so_its_tools_run_without_asking()
    {
        FakeGitHub();

        var tools = await Connect(Hub(), Server("github", trusted: true));

        Assert.All(tools.Catalog.Capabilities.Where(c => c.Server == "github"), c => Assert.Equal(SafetyClass.Read, c.Safety));
    }

    [Fact]
    public async Task Outside_results_and_errors_are_readable_by_the_model()
    {
        FakeGitHub();
        var tools = await Connect(Hub(), Server("github"));

        var text = await tools.Catalog["github_search_issues"].Function.InvokeAsync(new() { ["query"] = "LDO" }, Ct);
        var json = (JsonElement)(await tools.Catalog["github_create_issue"].Function.InvokeAsync(new() { ["title"] = "IQ too high" }, Ct))!;
        var error = await tools.Catalog["github_get_repo"].Function.InvokeAsync(new(), Ct);

        Assert.Equal("2 issues match 'LDO'", text);
        Assert.Equal(7, json.GetProperty("number").GetInt32());
        Assert.Equal(new CapabilityError("Repository not found"), error);
    }

    [Fact]
    public async Task A_server_that_cant_be_reached_is_skipped_and_named()
    {
        FakeGitHub();

        var tools = await Connect(Hub(), Server("perforce"), new OutsideServer("bad name!", "x", false, ServerOrigin.User), Server("github"));

        Assert.Collection(tools.Problems,
            p => Assert.StartsWith("Couldn't connect to perforce", p),
            p => Assert.Contains("isn't a usable server name", p));
        Assert.Contains(tools.Catalog.Capabilities, c => c.Name == "github_search_issues");
        Assert.Contains(tools.Catalog.Capabilities, c => c.Name == "list_test_plans");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var item in Enumerable.Reverse(_cleanup)) await item.DisposeAsync();
        try { Directory.Delete(_workspace, recursive: true); } catch (IOException) { }
    }
}
