using System.Text.Json;
using System.Text.Json.Serialization;

namespace Demo.Ai;

/// <summary>
/// User-level AI preferences. Contains NO secrets; keys live in <see cref="Credentials.ICredentialStore"/>.
/// </summary>
public sealed record AiSettings
{
    /// <summary>Master opt-in. While false the chat panel is hidden and nothing is sent anywhere.</summary>
    public bool ChatEnabled { get; init; }

    public AiProvider Provider { get; init; } = AiProvider.Anthropic;

    /// <summary>Per-provider model override; missing entries use <see cref="ProviderInfo.DefaultModel"/>.</summary>
    public Dictionary<AiProvider, string> Models { get; init; } = [];

    /// <summary>Optional spend cap per chat session, in USD. Null = no cap.</summary>
    public decimal? SessionSpendCapUsd { get; init; } = 1.00m;

    /// <summary>Agent access (MCP) for the desktop app. Off by default.</summary>
    public bool McpEnabled { get; init; }

    public bool McpAllowWrites { get; init; }

    public int McpPort { get; init; } = 47821;

    public string ModelFor(AiProvider provider) =>
        Models.TryGetValue(provider, out var model) && !string.IsNullOrWhiteSpace(model)
            ? model
            : ProviderInfo.For(provider).DefaultModel;
}

public sealed class AiSettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Path { get; } = path;

    public AiSettings Load()
    {
        if (!File.Exists(Path)) return new AiSettings();
        try
        {
            return JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(Path), Options) ?? new AiSettings();
        }
        catch (JsonException)
        {
            return new AiSettings(); // A corrupt settings file must not stop the legacy tool from starting.
        }
    }

    public void Save(AiSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(settings, Options));
    }
}
