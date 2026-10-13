using Microsoft.Extensions.AI;

namespace Demo.Capabilities;

/// <summary>How dangerous a capability is. Drives approval in chat and exposure/annotations in MCP.</summary>
public enum SafetyClass
{
    /// <summary>Reads data only. Runs without asking.</summary>
    Read,

    /// <summary>Changes data. Needs a human's approval in chat; hidden from MCP unless writes are allowed.</summary>
    Write,
}

/// <summary>
/// Marks a method as a capability that AI front ends may call. The method's
/// parameters (with <see cref="System.ComponentModel.DescriptionAttribute"/>) become the JSON schema.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CapabilityAttribute(string name, SafetyClass safety) : Attribute
{
    /// <summary>snake_case tool name shown to models, e.g. <c>list_overdue_orders</c>.</summary>
    public string Name { get; } = name;

    public SafetyClass Safety { get; } = safety;

    /// <summary>Human-readable title for approval cards and MCP clients.</summary>
    public string? Title { get; init; }
}

/// <summary>One entry in the catalog: a model-callable function plus its safety metadata.</summary>
public sealed record Capability(AIFunction Function, SafetyClass Safety, string Title)
{
    public string Name => Function.Name;
    public string Description => Function.Description;
}
