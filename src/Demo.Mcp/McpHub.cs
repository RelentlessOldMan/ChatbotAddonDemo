using System.IO.Pipelines;
using System.Text;
using Demo.Capabilities;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Demo.Mcp;

/// <summary>
/// The desktop app's MCP hub: the one door every AI uses to reach the app. The chat panel calls the app's tools
/// through it in-process (path C3), a coding agent connects to the open app through it (path D1, see
/// <see cref="AgentDoor"/>), and it connects the chat to outside MCP servers the user or IT chose.
/// Both kinds of AI get the same tools, with the same descriptions, annotations and error handling.
/// </summary>
public sealed class McpHub(CapabilityCatalog catalog, string serverName, string instructions)
{
    /// <summary>How long an outside server gets to start and answer before the chat goes on without it.</summary>
    public static readonly TimeSpan OutsideServerTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The app's own tools, as served to every connection.</summary>
    public CapabilityCatalog Catalog { get; } = catalog;

    public string ServerName { get; } = serverName;

    public string Instructions { get; } = instructions;

    /// <summary>How outside servers are reached. Tests replace it to connect to a fake server in-process.</summary>
    internal Func<OutsideServer, IClientTransport> OutsideTransport { get; init; } = server => server.CreateTransport();

    /// <summary>
    /// Path C3: the chat panel's connection. Returns the app's own tools, as they come back over MCP, plus the
    /// tools of every outside server that could be reached. A server that can't be reached is skipped and named
    /// in <see cref="HubTools.Problems"/>: one broken server never stops the chat.
    /// </summary>
    public async Task<HubTools> ConnectChatAsync(IReadOnlyList<OutsideServer> outside, CancellationToken cancellationToken)
    {
        var connections = new List<IAsyncDisposable>();
        var problems = new List<string>();
        try
        {
            // No approval gate on this connection: the chat session asks the user itself, before it calls.
            var own = await InProcessConnection.OpenAsync(CreateServerOptions(McpToolAdapter.ToMcpTools(Catalog, McpExposurePolicy.All)), cancellationToken);
            connections.Add(own);
            // In the catalog's order, not the server's alphabetical one, so the model sees the same tool list as without the hub.
            var order = Catalog.Capabilities.Select((c, i) => (c.Name, i)).ToDictionary();
            var capabilities = (await own.Client.ListToolsAsync(cancellationToken: cancellationToken))
                .OrderBy(t => order.GetValueOrDefault(t.Name, int.MaxValue))
                .Select(McpCapabilities.FromOwnTool)
                .ToList();

            foreach (var server in outside)
            {
                if (server.Problem() is { } problem)
                {
                    problems.Add(problem);
                    continue;
                }
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(OutsideServerTimeout);
                    var client = await McpClient.CreateAsync(OutsideTransport(server), cancellationToken: timeout.Token);
                    connections.Add(client);
                    foreach (var tool in await client.ListToolsAsync(cancellationToken: timeout.Token))
                    {
                        var name = PrefixedName(server, tool.Name);
                        if (capabilities.Any(c => c.Name == name)) continue; // a clash can only come from a server reusing a name
                        capabilities.Add(McpCapabilities.FromOutsideTool(tool, server, name));
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    problems.Add(ex is OperationCanceledException
                        ? $"Couldn't connect to {server.Name}: it didn't answer within {OutsideServerTimeout.TotalSeconds:0} seconds."
                        : $"Couldn't connect to {server.Name}: {ex.GetBaseException().Message}");
                }
            }
            return new HubTools(new CapabilityCatalog(capabilities), problems, connections);
        }
        catch
        {
            foreach (var connection in connections) await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Path D1: lets coding agents connect to this app over a named pipe. See <see cref="AgentDoor"/>.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public AgentDoor OpenAgentDoor(string pipeName, IAgentSupervisor supervisor) => AgentDoor.Open(this, pipeName, supervisor);

    internal McpServerOptions CreateServerOptions(IEnumerable<McpServerTool> tools) => new()
    {
        ServerInfo = new Implementation { Name = ServerName, Version = typeof(McpHub).Assembly.GetName().Version!.ToString(3) },
        ServerInstructions = Instructions,
        ToolCollection = [.. tools],
    };

    /// <summary>
    /// An outside tool's name as the model sees it: the server's name first, so <c>search</c> from two servers can't
    /// clash, and only the characters every provider accepts in a tool name, at most 64 of them.
    /// </summary>
    internal static string PrefixedName(OutsideServer server, string toolName)
    {
        var name = new StringBuilder(server.Name.ToLowerInvariant()).Append('_');
        foreach (var c in toolName) name.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        return name.Length > 64 ? name.ToString(0, 64) : name.ToString();
    }

    /// <summary>A server and a client joined by in-memory pipes: MCP without a process or a port.</summary>
    private sealed class InProcessConnection : IAsyncDisposable
    {
        private readonly McpServer _server;
        private readonly Task _serving;

        private InProcessConnection(McpServer server, Task serving, McpClient client)
        {
            _server = server;
            _serving = serving;
            Client = client;
        }

        public McpClient Client { get; }

        public static async Task<InProcessConnection> OpenAsync(McpServerOptions options, CancellationToken cancellationToken)
        {
            Pipe toServer = new(), toClient = new();
            var server = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()), options);
            var serving = server.RunAsync();
            try
            {
                var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()), cancellationToken: cancellationToken);
                return new InProcessConnection(server, serving, client);
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _server.DisposeAsync();
            try { await _serving; } catch (OperationCanceledException) { }
        }
    }
}

/// <summary>The tools the chat panel can use, and the connections behind them (closed by <see cref="DisposeAsync"/>).</summary>
public sealed class HubTools(CapabilityCatalog catalog, IReadOnlyList<string> problems, IReadOnlyList<IAsyncDisposable> connections) : IAsyncDisposable
{
    public CapabilityCatalog Catalog { get; } = catalog;

    /// <summary>Outside servers that were skipped, and why; shown to the user.</summary>
    public IReadOnlyList<string> Problems { get; } = problems;

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in connections.Reverse())
        {
            try { await connection.DisposeAsync(); }
            catch (Exception) { } // a server that's already gone (crashed, or the process was ended) has nothing left to close
        }
    }
}
