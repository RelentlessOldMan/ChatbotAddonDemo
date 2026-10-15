using System.Globalization;
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

    /// <summary>
    /// Used instead of the dollar limit when the model's price is unknown (see <see cref="PriceTable"/>), so a
    /// limit set in Settings never silently does nothing. Applies only while <see cref="SessionSpendCapUsd"/> is set.
    /// </summary>
    public const long DefaultUnpricedTokenLimit = 500_000;

    public string ModelFor(AiProvider provider) =>
        Models.TryGetValue(provider, out var model) && !string.IsNullOrWhiteSpace(model)
            ? model
            : ProviderInfo.For(provider).DefaultModel;
}

/// <summary>
/// The spending limit as typed in Settings. Only an empty box means no limit: anything else must be a positive
/// dollar amount, so a typo can never silently remove the limit. A dot is always the decimal point (whatever the
/// Windows region), so "1.50" can't be read as 150.
/// </summary>
public static class SpendLimitText
{
    public const string Problem = "Enter a dollar amount above zero, such as 1.50, or leave the box empty for no limit.";

    /// <summary>False when the text isn't a valid limit; <paramref name="limitUsd"/> is null for "no limit".</summary>
    public static bool TryParse(string? text, out decimal? limitUsd)
    {
        limitUsd = null;
        var value = (text ?? "").Trim();
        if (value.Length == 0) return true;
        if (value.StartsWith('$')) value = value[1..].TrimStart();
        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
            return false;
        limitUsd = amount;
        return true;
    }

    public static string Format(decimal? limitUsd) => limitUsd?.ToString("0.00##", CultureInfo.InvariantCulture) ?? "";
}

public sealed class AiSettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }, // "Provider": 7 is not a provider
    };

    public string Path { get; } = path;

    public AiSettings Load()
    {
        // A corrupt or unreadable settings file must not stop the legacy tool from starting.
        try
        {
            if (!File.Exists(Path)) return new AiSettings();
            return Normalize(JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(Path), Options) ?? new AiSettings());
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AiSettings();
        }
    }

    /// <summary>Repairs values the JSON can hold but the app can't use, such as <c>"Models": null</c>.</summary>
    private static AiSettings Normalize(AiSettings loaded)
    {
        var defaults = new AiSettings();
        return loaded with
        {
            Provider = Enum.IsDefined(loaded.Provider) ? loaded.Provider : defaults.Provider,
            Models = (loaded.Models ?? []).Where(m => Enum.IsDefined(m.Key) && m.Value is not null).ToDictionary(),
            // Null is a deliberate "no limit"; zero or less can only come from a damaged file, so the default applies.
            SessionSpendCapUsd = loaded.SessionSpendCapUsd is <= 0 ? defaults.SessionSpendCapUsd : loaded.SessionSpendCapUsd,
        };
    }

    public void Save(AiSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(settings, Options));
    }
}
