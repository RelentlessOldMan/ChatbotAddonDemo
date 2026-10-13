using Demo.Capabilities;
using ModelContextProtocol.Server;

namespace Demo.Cli;

/// <summary>
/// Which capabilities a coding agent may see. Read-only unless the person who configured the agent
/// adds <c>--allow-writes</c> (plans, reports) and/or <c>--allow-runs</c> (the bench).
/// </summary>
public sealed record McpExposurePolicy(bool AllowWrites, bool AllowRuns)
{
    public static readonly McpExposurePolicy ReadOnly = new(AllowWrites: false, AllowRuns: false);

    public bool Exposes(Capability capability) => capability.Safety switch
    {
        SafetyClass.Read => true,
        SafetyClass.Write => AllowWrites,
        SafetyClass.Execute => AllowRuns,
        _ => false,
    };

    public string Describe() => (AllowWrites, AllowRuns) switch
    {
        (false, false) => "read-only",
        (true, false) => "read + write files",
        (false, true) => "read + run the bench",
        _ => "read + write files + run the bench",
    };
}

public static class McpToolAdapter
{
    /// <summary>
    /// Wraps catalog entries as MCP tools. The safety class becomes MCP tool annotations, so the
    /// agent's own permission prompts know which tools only read, which write, and which touch hardware.
    /// </summary>
    public static IReadOnlyList<McpServerTool> ToMcpTools(CapabilityCatalog catalog, McpExposurePolicy policy) =>
        catalog.Capabilities
            .Where(policy.Exposes)
            .Select(c => McpServerTool.Create(c.Function, new McpServerToolCreateOptions
            {
                Name = c.Name,
                Title = c.Title,
                Description = c.Description,
                ReadOnly = c.Safety == SafetyClass.Read,
                Destructive = false,                     // nothing overwrites or deletes: plans and reports are new files
                OpenWorld = c.Safety == SafetyClass.Execute, // drives physical equipment outside this process
                SerializerOptions = CapabilityCatalog.SerializerOptions,
            }))
            .ToList();
}
