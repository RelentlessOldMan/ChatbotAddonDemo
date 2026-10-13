namespace Demo.Ai;

public enum AiProvider { Anthropic, OpenAI }

/// <summary>Static facts about each provider, used by settings UI and credential resolution.</summary>
public sealed record ProviderInfo(
    AiProvider Provider,
    string DisplayName,
    string DefaultModel,
    string ApiKeyEnvironmentVariable,
    string ConsoleUrl)
{
    public static readonly ProviderInfo Anthropic = new(
        AiProvider.Anthropic, "Anthropic (Claude)", "claude-opus-5-5", "ANTHROPIC_API_KEY", "https://platform.claude.com/settings/keys");

    public static readonly ProviderInfo OpenAI = new(
        AiProvider.OpenAI, "OpenAI", "gpt-6-astra", "OPENAI_API_KEY", "https://platform.openai.com/api-keys");

    public static IReadOnlyList<ProviderInfo> All { get; } = [Anthropic, OpenAI];

    public static ProviderInfo For(AiProvider provider) => All.Single(p => p.Provider == provider);
}
