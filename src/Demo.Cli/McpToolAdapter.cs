using Demo.Capabilities;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
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
    private const string CancelRun = "cancel_test_run";
    private const string GenerateReport = "generate_report";

    /// <summary>
    /// Wraps catalog entries as MCP tools. The safety class becomes MCP tool annotations, so the
    /// agent's own permission prompts know which tools only read, which write, and which touch hardware.
    /// </summary>
    public static IReadOnlyList<McpServerTool> ToMcpTools(CapabilityCatalog catalog, McpExposurePolicy policy) =>
        catalog.Capabilities
            .Where(policy.Exposes)
            .Select(c => McpServerTool.Create(new ErrorsAsToolErrors(c.Function), new McpServerToolCreateOptions
            {
                Name = c.Name,
                Title = c.Title,
                Description = c.Description,
                ReadOnly = c.Safety == SafetyClass.Read,
                // Plans and runs are always new files. Cancelling aborts a run: its remaining measurements are never taken.
                Destructive = c.Name == CancelRun,
                // The report is rebuilt from the run's stored results, so writing it again yields the same report.html.
                Idempotent = c.Safety == SafetyClass.Read || c.Name == GenerateReport,
                OpenWorld = c.Safety == SafetyClass.Execute, // drives physical equipment outside this process
                SerializerOptions = CapabilityCatalog.SerializerOptions,
            }))
            .ToList();

    /// <summary>
    /// The catalog reports business-rule violations as <see cref="CapabilityError"/> results (readable by a chat
    /// model); MCP has a dedicated channel for that, so they become <c>isError</c> results with the message as text.
    /// </summary>
    private sealed class ErrorsAsToolErrors(AIFunction inner) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            await base.InvokeCoreAsync(arguments, cancellationToken) switch
            {
                CapabilityError error => new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = error.Error }] },
                var result => result,
            };
    }
}
