using Demo.Capabilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Demo.Cli;

/// <summary>
/// <c>demo-cli mcp</c>: this process becomes an MCP server on stdin/stdout. The coding agent starts
/// the process itself, so there is no port and no token: the trust boundary is the user who
/// configured the agent, and the process runs with exactly their permissions.
/// </summary>
public static class StdioMcpHost
{
    public static async Task RunAsync(CapabilityCatalog catalog, McpExposurePolicy policy, Implementation serverInfo, string instructions, CancellationToken cancellationToken = default)
    {
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);

        // stdout is the protocol channel; anything else written there corrupts it.
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = serverInfo;
                o.ServerInstructions = instructions + "\n" + policy switch
                {
                    { AllowWrites: false, AllowRuns: false } => "This server is read-only: the user has not allowed agents to create files or run the bench.",
                    { AllowRuns: false } => "The user has not allowed agents to run the bench; you can create plans and reports.",
                    { AllowWrites: false } => "The user has not allowed agents to create plans or reports; you can run existing plans.",
                    _ => "Tools that are not read-only create files or run the bench; confirm intent with the user before using them.",
                };
            })
            .WithStdioServerTransport()
            .WithTools(McpToolAdapter.ToMcpTools(catalog, policy));

        await builder.Build().RunAsync(cancellationToken);
    }
}
