using Demo.Capabilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Demo.Mcp;

/// <summary>
/// MCP over stdin/stdout, for the console app. The agent spawns the process, so
/// there is no port and no auth: the trust boundary is the user who configured the agent.
/// </summary>
public static class StdioMcpHost
{
    public static async Task RunAsync(CapabilityCatalog catalog, McpExposurePolicy policy, Implementation serverInfo, CancellationToken cancellationToken = default)
    {
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);

        // stdout is the protocol channel; anything else written there corrupts it.
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = serverInfo;
                o.ServerInstructions = ServerInstructions.For(policy);
            })
            .WithStdioServerTransport()
            .WithTools(McpToolAdapter.ToMcpTools(catalog, policy));

        await builder.Build().RunAsync(cancellationToken);
    }
}

internal static class ServerInstructions
{
    public static string For(McpExposurePolicy policy) =>
        "Tools for a work-order and parts-inventory system. Ids look like 'WO-1003' (work orders) and 'FLT-031' (part SKUs). " +
        (policy.AllowWrites
            ? "Tools marked destructive change real data; confirm intent with the user before using them."
            : "This server is read-only; the user has not enabled changes from agents.");
}
