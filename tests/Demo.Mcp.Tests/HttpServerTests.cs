using System.Net;
using System.Net.Sockets;
using Demo.Capabilities;
using Demo.Capabilities.Workshop;
using Demo.Domain;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Demo.Mcp.Tests;

/// <summary>The desktop app's in-process HTTP server, exercised with a real MCP client and raw HTTP for the hardening checks.</summary>
public sealed class HttpServerTests : IAsyncLifetime
{
    private readonly string _token = HttpMcpHost.NewToken();
    private readonly WorkshopService _service = new(new InMemoryDataStore(SeedData.Create(DateOnly.FromDateTime(DateTime.Today))));
    private HttpMcpHost _host = null!;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _host = await Start(McpExposurePolicy.ReadOnly);

    private Task<HttpMcpHost> Start(McpExposurePolicy policy) =>
        HttpMcpHost.StartAsync(
            CapabilityCatalog.FromObject(new WorkshopCapabilities(_service)),
            new HttpMcpHostOptions(FreePort(), _token, policy, new Implementation { Name = "demo-desktop", Version = "1.0.0" }));

    private Task<McpClient> Connect(HttpMcpHost host, string token) =>
        McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = host.Options.Endpoint,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        }), cancellationToken: Ct);

    [Fact]
    public async Task Authorized_client_lists_read_only_tools_and_calls_are_observed()
    {
        var seen = new List<string>();
        _host.ToolInvoked += i => { lock (seen) seen.Add(i.ToolName); };
        await using var client = await Connect(_host, _token);

        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        Assert.DoesNotContain(tools, t => t.ProtocolTool.Annotations?.DestructiveHint == true);

        var result = await client.CallToolAsync("list_low_stock_parts", cancellationToken: Ct);
        Assert.Contains("FLT-031", string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text)));
        Assert.Equal(["list_low_stock_parts"], seen);
    }

    [Fact]
    public async Task Writes_through_http_change_the_shared_live_service()
    {
        await using var host = await Start(new McpExposurePolicy(AllowWrites: true));
        var changed = 0;
        _service.Changed += (_, _) => changed++;
        await using var client = await Connect(host, _token);

        await client.CallToolAsync("set_work_order_priority", new Dictionary<string, object?> { ["id"] = "WO-1004", ["priority"] = "Urgent" }, cancellationToken: Ct);

        Assert.Equal(Priority.Urgent, _service.GetOrder("WO-1004").Priority);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Wrong_token_cannot_connect()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => Connect(_host, "wrong-token-wrong-token-wrong-token"));
    }

    [Theory]
    [InlineData(null, null, HttpStatusCode.Unauthorized)]                       // no token
    [InlineData("evil.example", null, HttpStatusCode.Forbidden)]                // DNS rebinding: foreign Host header
    [InlineData(null, "https://evil.example", HttpStatusCode.Forbidden)]       // cross-site browser request
    public async Task Hardening_rejects_bad_requests(string? hostHeader, string? origin, HttpStatusCode expected)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, _host.Options.Endpoint)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        if (hostHeader is not null)
        {
            request.Headers.Host = hostHeader;
            request.Headers.Authorization = new("Bearer", _token);
        }
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
            request.Headers.Authorization = new("Bearer", _token);
        }

        using var response = await http.SendAsync(request, Ct);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Listens_on_loopback_only()
    {
        var nonLoopback = (await Dns.GetHostAddressesAsync(Dns.GetHostName(), Ct))
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        Assert.SkipWhen(nonLoopback is null, "machine has no non-loopback IPv4 address");

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await Assert.ThrowsAsync<SocketException>(async () => await socket.ConnectAsync(nonLoopback, _host.Options.Port, Ct));
    }

    [Fact]
    public async Task Short_tokens_are_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => HttpMcpHost.StartAsync(
            CapabilityCatalog.FromObject(new WorkshopCapabilities(_service)),
            new HttpMcpHostOptions(FreePort(), "short", McpExposurePolicy.ReadOnly, new Implementation { Name = "x", Version = "1" })));
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
