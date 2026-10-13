using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Demo.Ai.Credentials;

public enum CredentialSource { None, Gateway, EnvironmentVariable, UserKey }

/// <summary>What to connect with, and where it came from (shown to the user so they know who pays).</summary>
public sealed record ResolvedCredential(CredentialSource Source, string? ApiKey = null, Uri? BaseUrl = null)
{
    public static readonly ResolvedCredential None = new(CredentialSource.None);

    public bool IsAvailable => Source != CredentialSource.None;

    public string Describe() => Source switch
    {
        CredentialSource.Gateway => $"Your organization's AI gateway ({BaseUrl?.Host})",
        CredentialSource.EnvironmentVariable => "API key from an environment variable",
        CredentialSource.UserKey => $"Your saved API key ({Mask(ApiKey)})",
        _ => "Not configured",
    };

    public static string Mask(string? key) =>
        key is null || key.Length < 8 ? "••••" : $"{key[..4]}…{key[^4..]}";
}

/// <summary>IT-managed settings. On Windows these come from HKLM\Software\Policies (Group Policy / Intune).</summary>
public interface IMachinePolicy
{
    /// <summary>Base URL of an LLM gateway that proxies the provider's API.</summary>
    Uri? GatewayUrl(AiProvider provider);

    /// <summary>Token the gateway expects; optional when the gateway uses Windows/network auth.</summary>
    string? GatewayToken(AiProvider provider);

    /// <summary>When true, users cannot enter their own keys (only the gateway may be used).</summary>
    bool DisableUserKeys { get; }
}

public sealed class NoMachinePolicy : IMachinePolicy
{
    public static readonly NoMachinePolicy Instance = new();
    public Uri? GatewayUrl(AiProvider provider) => null;
    public string? GatewayToken(AiProvider provider) => null;
    public bool DisableUserKeys => false;
}

/// <summary>
/// Reads <c>HKLM\Software\Policies\ChatbotAddonDemo\{Provider}</c> values <c>GatewayUrl</c> / <c>GatewayToken</c>,
/// and <c>DisableUserKeys</c> (DWORD) on the root key.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryMachinePolicy : IMachinePolicy
{
    private const string Root = @"Software\Policies\ChatbotAddonDemo";

    public Uri? GatewayUrl(AiProvider provider) =>
        Read($@"{Root}\{provider}", "GatewayUrl") is { } s && Uri.TryCreate(s, UriKind.Absolute, out var uri) ? uri : null;

    public string? GatewayToken(AiProvider provider) => Read($@"{Root}\{provider}", "GatewayToken");

    public bool DisableUserKeys =>
        Registry.LocalMachine.OpenSubKey(Root)?.GetValue("DisableUserKeys") is int value && value != 0;

    private static string? Read(string key, string name) =>
        Registry.LocalMachine.OpenSubKey(key)?.GetValue(name) as string;
}

/// <summary>
/// Picks the credential for a provider. Precedence: gateway (IT) → environment variable → user's saved key.
/// </summary>
public sealed class CredentialResolver(
    ICredentialStore store,
    IMachinePolicy policy,
    Func<string, string?>? environment = null)
{
    private readonly Func<string, string?> _environment = environment ?? Environment.GetEnvironmentVariable;

    public static string KeyName(AiProvider provider) => $"{provider}-api-key";

    public bool UserKeysAllowed => !policy.DisableUserKeys;

    public ResolvedCredential Resolve(AiProvider provider)
    {
        if (policy.GatewayUrl(provider) is { } gateway)
            return new ResolvedCredential(CredentialSource.Gateway, policy.GatewayToken(provider), gateway);

        var info = ProviderInfo.For(provider);
        if (_environment(info.ApiKeyEnvironmentVariable) is { Length: > 0 } envKey)
            return new ResolvedCredential(CredentialSource.EnvironmentVariable, envKey);

        if (UserKeysAllowed && store.Get(KeyName(provider)) is { Length: > 0 } userKey)
            return new ResolvedCredential(CredentialSource.UserKey, userKey);

        return ResolvedCredential.None;
    }

    public void SaveUserKey(AiProvider provider, string key)
    {
        if (!UserKeysAllowed) throw new InvalidOperationException("Your administrator has disabled personal API keys.");
        store.Set(KeyName(provider), key.Trim());
    }

    public void RemoveUserKey(AiProvider provider) => store.Remove(KeyName(provider));
}
