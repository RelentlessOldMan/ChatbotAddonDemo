using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Demo.Ai.Credentials;

public enum CredentialSource { None, Gateway, EnvironmentVariable, UserKey }

/// <summary>What to connect with, and where it came from (shown to the user so they know who pays).</summary>
public sealed record ResolvedCredential(CredentialSource Source, string? ApiKey = null, Uri? BaseUrl = null)
{
    public static readonly ResolvedCredential None = new(CredentialSource.None);

    /// <summary>Nothing usable, because of a configuration problem the user (or IT) should read about.</summary>
    public static ResolvedCredential Unusable(string problem) => new(CredentialSource.None) { Problem = problem };

    /// <summary>Why no credential is available, when it's a misconfiguration rather than simply nothing set up.</summary>
    public string? Problem { get; init; }

    public bool IsAvailable => Source != CredentialSource.None;

    public string Describe() => Source switch
    {
        CredentialSource.Gateway => $"Your organization's AI gateway ({BaseUrl?.Host})",
        CredentialSource.EnvironmentVariable => "API key from an environment variable",
        CredentialSource.UserKey => $"Your saved API key ({Mask(ApiKey)})",
        _ => Problem ?? "Not configured",
    };

    public static string Mask(string? key) =>
        key is null || key.Length < 8 ? "••••" : $"{key[..4]}…{key[^4..]}";
}

/// <summary>IT-managed settings. On Windows these come from HKLM\Software\Policies (Group Policy / Intune).</summary>
public interface IMachinePolicy
{
    /// <summary>
    /// Base URL of an LLM gateway that proxies the provider's API, exactly as IT entered it (null when not set).
    /// Returned unparsed so a value that isn't a valid address still blocks personal keys instead of being ignored.
    /// </summary>
    string? GatewayUrl(AiProvider provider);

    /// <summary>Token the gateway expects; optional when the gateway uses Windows/network auth.</summary>
    string? GatewayToken(AiProvider provider);

    /// <summary>When true, users cannot enter their own keys (only the gateway may be used).</summary>
    bool DisableUserKeys { get; }
}

public sealed class NoMachinePolicy : IMachinePolicy
{
    public static readonly NoMachinePolicy Instance = new();
    public string? GatewayUrl(AiProvider provider) => null;
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

    public string? GatewayUrl(AiProvider provider) => Read($@"{Root}\{provider}", "GatewayUrl");

    public string? GatewayToken(AiProvider provider) => Read($@"{Root}\{provider}", "GatewayToken");

    public bool DisableUserKeys
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(Root);
            return key?.GetValue("DisableUserKeys") is int value && value != 0;
        }
    }

    private static string? Read(string path, string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(path);
        return key?.GetValue(name) as string;
    }
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
        if (policy.GatewayUrl(provider) is { } gatewayText)
        {
            // Never fall back to personal keys when IT has set a gateway, even a misconfigured one.
            if (!Uri.TryCreate(gatewayText.Trim(), UriKind.Absolute, out var gateway))
                return ResolvedCredential.Unusable("The AI gateway address set by your administrator isn't a valid address, so it isn't used. Ask IT to correct it.");
            return IsSecure(gateway)
                ? new ResolvedCredential(CredentialSource.Gateway, policy.GatewayToken(provider), gateway)
                : ResolvedCredential.Unusable($"The AI gateway address set by your administrator ({gateway.Scheme}://{gateway.Authority}) doesn't use https, so it isn't used. Ask IT to change it to https.");
        }

        // DisableUserKeys covers every personal key, including one in an environment variable.
        var info = ProviderInfo.For(provider);
        if (UserKeysAllowed && _environment(info.ApiKeyEnvironmentVariable) is { Length: > 0 } envKey)
            return new ResolvedCredential(CredentialSource.EnvironmentVariable, envKey);

        if (UserKeysAllowed && store.Get(KeyName(provider)) is { Length: > 0 } userKey)
            return new ResolvedCredential(CredentialSource.UserKey, userKey);

        return ResolvedCredential.None;
    }

    /// <summary>The gateway token travels with every request, so only https is allowed (plain http only to this PC).</summary>
    private static bool IsSecure(Uri gateway) =>
        gateway.Scheme == Uri.UriSchemeHttps || (gateway.Scheme == Uri.UriSchemeHttp && gateway.IsLoopback);

    public void SaveUserKey(AiProvider provider, string key)
    {
        if (!UserKeysAllowed) throw new InvalidOperationException("Your administrator has disabled personal API keys.");
        store.Set(KeyName(provider), key.Trim());
    }

    public void RemoveUserKey(AiProvider provider) => store.Remove(KeyName(provider));
}
