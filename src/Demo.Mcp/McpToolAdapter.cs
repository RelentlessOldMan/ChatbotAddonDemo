using Demo.Capabilities;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace Demo.Mcp;

/// <summary>Which capabilities external agents may see. Read-only unless the user opts in.</summary>
public sealed record McpExposurePolicy(bool AllowWrites)
{
    public static readonly McpExposurePolicy ReadOnly = new(AllowWrites: false);

    public bool Exposes(Capability capability) => AllowWrites || capability.Safety == SafetyClass.Read;
}

/// <summary>Raised whenever an external agent calls a tool; the desktop app shows it in the status bar.</summary>
public sealed record ToolInvocation(string ToolName, DateTimeOffset At);

public static class McpToolAdapter
{
    /// <summary>
    /// Wraps catalog entries as MCP tools. The safety class becomes MCP tool annotations,
    /// so the agent's own permission prompts know which tools change data.
    /// </summary>
    public static IReadOnlyList<McpServerTool> ToMcpTools(
        CapabilityCatalog catalog,
        McpExposurePolicy policy,
        Action<ToolInvocation>? onInvoked = null) =>
        catalog.Capabilities
            .Where(policy.Exposes)
            .Select(c => McpServerTool.Create(
                onInvoked is null ? c.Function : new ObservedFunction(c.Function, onInvoked),
                new McpServerToolCreateOptions
                {
                    Name = c.Name,
                    Title = c.Title,
                    Description = c.Description,
                    ReadOnly = c.Safety == SafetyClass.Read,
                    Destructive = c.Safety == SafetyClass.Write,
                    OpenWorld = false,
                    SerializerOptions = CapabilityCatalog.SerializerOptions,
                }))
            .ToList();

    private sealed class ObservedFunction(AIFunction inner, Action<ToolInvocation> onInvoked) : DelegatingAIFunction(inner)
    {
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            onInvoked(new ToolInvocation(Name, DateTimeOffset.Now));
            return base.InvokeCoreAsync(arguments, cancellationToken);
        }
    }
}
