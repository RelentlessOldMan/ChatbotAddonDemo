using System.Text.Json;
using Demo.Capabilities;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Demo.Mcp;

/// <summary>
/// Turns tools from an MCP connection back into catalog entries, so the chat session treats a tool that came
/// through the hub exactly like one it was handed directly: same name, same approval rules, same results.
/// </summary>
public static class McpCapabilities
{
    /// <summary>
    /// One of the app's own tools, served by the hub. Its safety class comes back from the annotations
    /// <see cref="McpToolAdapter"/> put on it: read-only stays a read, open-world drives the bench.
    /// </summary>
    public static Capability FromOwnTool(McpClientTool tool) =>
        new(new ResultsForModels(tool), SafetyOf(tool), tool.ProtocolTool.Title ?? tool.Name);

    /// <summary>
    /// A tool from an outside server. Its annotations were written by whoever made that server, so they aren't
    /// trusted: every call asks the user first, unless IT has marked the whole server as safe.
    /// </summary>
    public static Capability FromOutsideTool(McpClientTool tool, OutsideServer server, string name) =>
        new(new ResultsForModels(tool.WithName(name)), server.Trusted ? SafetyClass.Read : SafetyClass.Execute,
            $"{server.Name}: {OneLine(tool.ProtocolTool.Title ?? tool.Name)}")
        { Server = server.Name };

    /// <summary>
    /// Why the AI providers would refuse this tool's inputs, or null if they'd take them. Every tool goes out with every
    /// message, so one tool a provider refuses would fail the whole chat, not just that tool. Checked: the inputs are
    /// an object (both providers require it, and OpenAI refuses anyOf, oneOf, allOf, not or enum at the top), and
    /// every list says what it holds (OpenAI refuses an array without items).
    /// </summary>
    internal static string? SchemaProblem(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "object")
            return "its inputs aren't described as an object";
        foreach (var keyword in (string[])["anyOf", "oneOf", "allOf", "not", "enum"])
            if (schema.TryGetProperty(keyword, out _)) return $"its inputs use '{keyword}' at the top level";
        return ListWithoutItems(schema) ? "one of its inputs is a list that doesn't say what it holds" : null;
    }

    private static bool ListWithoutItems(JsonElement schema) => schema.ValueKind switch
    {
        JsonValueKind.Object =>
            (schema.TryGetProperty("type", out var type) && IsArrayType(type) && !schema.TryGetProperty("items", out _))
            || schema.EnumerateObject().Any(p => p.Name is not ("enum" or "const" or "default" or "examples") && ListWithoutItems(p.Value)),
        JsonValueKind.Array => schema.EnumerateArray().Any(ListWithoutItems),
        _ => false,
    };

    private static bool IsArrayType(JsonElement type) =>
        type.ValueKind == JsonValueKind.String ? type.GetString() == "array"
        : type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && t.GetString() == "array");

    /// <summary>Someone else's title, made safe to show on an approval card: one line, so it can't pass for more card text.</summary>
    private static string OneLine(string title)
    {
        var line = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        line = new string(line.Where(c => !char.IsControl(c)).ToArray());
        return line.Length > 80 ? line[..80] + "…" : line;
    }

    private static SafetyClass SafetyOf(McpClientTool tool) => tool.ProtocolTool.Annotations switch
    {
        { ReadOnlyHint: true } => SafetyClass.Read,
        { OpenWorldHint: false } => SafetyClass.Write,
        _ => SafetyClass.Execute, // MCP's default for a tool with no hints: may change things, and outside this app
    };

    /// <summary>
    /// What a tool returns over MCP, in the shape the catalog's functions return it: JSON results as JSON, and a
    /// tool error as <c>{"error": "..."}</c>, which the model reads like any other result. Without this the model
    /// would see MCP's envelope (content blocks, <c>isError</c>) instead of the answer.
    /// </summary>
    internal static object? ForModel(object? result) => result switch
    {
        TextContent text => JsonOrText(text.Text),
        IEnumerable<AIContent> contents when contents.ToList() is var list && list.All(c => c is TextContent) =>
            JsonOrText(string.Concat(list.Cast<TextContent>().Select(t => t.Text))),
        JsonElement { ValueKind: JsonValueKind.Object } envelope
            when envelope.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True =>
            new CapabilityError(Text(envelope)),
        _ => result,
    };

    private static object JsonOrText(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return text;
        }
    }

    private static string Text(JsonElement envelope) =>
        envelope.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? string.Join("\n", content.EnumerateArray()
                .Where(b => b.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                .Select(b => b.GetProperty("text").GetString()))
            : "The tool reported an error.";

    private sealed class ResultsForModels(AIFunction inner) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            ForModel(await base.InvokeCoreAsync(arguments, cancellationToken));
    }
}
