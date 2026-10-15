using System.Text.Json;
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
    public bool Exposes(Capability capability) => capability.Safety switch
    {
        SafetyClass.Read => true,
        SafetyClass.Write => AllowWrites,
        SafetyClass.Execute => AllowRuns,
        _ => false,
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
    /// So do arguments that don't fit the tool (a missing runId, "hot" for a temperature), naming the argument so the
    /// agent can fix its call. Any other exception still gets the SDK's generic message: no internals leak out.
    /// </summary>
    private sealed class ErrorsAsToolErrors(AIFunction inner) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            object? result;
            try
            {
                result = await base.InvokeCoreAsync(arguments, cancellationToken);
            }
            // Binding throws JsonException (wrong type) or ArgumentException (missing value) without naming the argument,
            // so find the culprit here. If every argument binds, the exception came from inside the tool: let it propagate.
            catch (Exception ex) when (ex is JsonException or ArgumentException && BadArgument(arguments) is { } problem)
            {
                return ToolError($"Invalid arguments for {Name}: {problem}");
            }
            return result is CapabilityError error ? ToolError(error.Error) : result;
        }

        private static CallToolResult ToolError(string text) => new() { IsError = true, Content = [new TextContentBlock { Text = text }] };

        private string? BadArgument(AIFunctionArguments arguments)
        {
            foreach (var parameter in UnderlyingMethod?.GetParameters() ?? [])
            {
                if (parameter.ParameterType == typeof(CancellationToken)) continue;
                if (!arguments.TryGetValue(parameter.Name!, out var value) || value is null)
                {
                    if (!parameter.IsOptional) return $"'{parameter.Name}' is required.";
                    continue;
                }
                if (value is not JsonElement json) continue;
                try
                {
                    JsonSerializer.Deserialize(json, parameter.ParameterType, CapabilityCatalog.SerializerOptions);
                }
                catch (JsonException)
                {
                    var given = json.GetRawText();
                    return $"'{parameter.Name}' must be {Expected(parameter.ParameterType)}, not {(given.Length > 40 ? given[..40] + "…" : given)}.";
                }
            }
            return null;
        }

        private static string Expected(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type.IsArray) return $"a list of {Plural(type.GetElementType()!)}";
            return Type.GetTypeCode(type) switch
            {
                TypeCode.String => "a string",
                TypeCode.Boolean => "true or false",
                TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 => "a whole number",
                TypeCode.Single or TypeCode.Double or TypeCode.Decimal => "a number",
                _ => $"a {type.Name}",
            };
        }

        private static string Plural(Type element) => Expected(element) switch
        {
            "a string" => "strings",
            "a whole number" => "whole numbers",
            "a number" => "numbers",
            var other => other,
        };
    }
}
