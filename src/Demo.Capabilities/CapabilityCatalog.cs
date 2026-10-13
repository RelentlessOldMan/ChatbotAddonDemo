using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Demo.Domain;
using Microsoft.Extensions.AI;

namespace Demo.Capabilities;

/// <summary>
/// The single list of things AI may do with the tool. The in-app chat consumes
/// <see cref="Capability.Function"/> directly; the MCP server (<c>demo-cli mcp</c>) wraps the same objects.
/// </summary>
public sealed class CapabilityCatalog
{
    public static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public CapabilityCatalog(IEnumerable<Capability> capabilities)
    {
        Capabilities = capabilities.ToList();
        var duplicate = Capabilities.GroupBy(c => c.Name).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate capability name '{duplicate.Key}'.");
    }

    public IReadOnlyList<Capability> Capabilities { get; }

    public Capability this[string name] => Capabilities.Single(c => c.Name == name);

    /// <summary>Looks up a capability without throwing, e.g. for a tool name a model made up.</summary>
    public bool TryGet(string name, [MaybeNullWhen(false)] out Capability capability)
    {
        capability = Capabilities.FirstOrDefault(c => c.Name == name);
        return capability is not null;
    }

    public CapabilityCatalog Where(Func<Capability, bool> predicate) => new(Capabilities.Where(predicate));

    public CapabilityCatalog Concat(CapabilityCatalog other) => new(Capabilities.Concat(other.Capabilities));

    /// <summary>Builds capabilities from every <see cref="CapabilityAttribute"/> method on <paramref name="target"/>.</summary>
    public static CapabilityCatalog FromObject(object target)
    {
        var capabilities =
            from method in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            let attribute = method.GetCustomAttribute<CapabilityAttribute>()
            where attribute is not null
            select Create(method, target, attribute);
        return new CapabilityCatalog(capabilities);
    }

    private static Capability Create(MethodInfo method, object target, CapabilityAttribute attribute)
    {
        var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description
            ?? throw new InvalidOperationException($"{method.Name} needs a [Description]; it is the model's only documentation.");

        var function = AIFunctionFactory.Create(method, target, new AIFunctionFactoryOptions
        {
            Name = attribute.Name,
            Description = description,
            SerializerOptions = SerializerOptions,
        });

        return new Capability(new DomainErrorsAsResults(function), attribute.Safety, attribute.Title ?? attribute.Name);
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions);
        options.Converters.Add(new JsonStringEnumConverter());
        options.WriteIndented = false; // results go to models: whitespace only costs tokens
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>
    /// Business-rule violations are normal outcomes the model should read and react to
    /// ("WO-9 does not exist"), so they come back as <c>{"error": "..."}</c> results.
    /// Unexpected exceptions still propagate and are reported generically.
    /// </summary>
    private sealed class DomainErrorsAsResults(AIFunction inner) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            try
            {
                return await base.InvokeCoreAsync(arguments, cancellationToken);
            }
            catch (DomainException ex)
            {
                return new CapabilityError(ex.Message);
            }
        }
    }
}

/// <summary>
/// What a capability returns instead of throwing when a business rule says no. Serializes as
/// <c>{"error": "..."}</c>, which a chat model reads like any other result; front ends that have
/// their own error channel (MCP's <c>isError</c>) check for this type.
/// </summary>
public sealed record CapabilityError(string Error);
