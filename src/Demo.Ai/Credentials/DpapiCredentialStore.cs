using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Demo.Ai.Credentials;

/// <summary>
/// Stores each secret as a DPAPI-encrypted file (CurrentUser scope): only this
/// Windows user on this machine can decrypt it. See docs/adr/003-credential-handling.md.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialStore(string directory) : ICredentialStore
{
    // Binds ciphertext to this app so another DPAPI consumer can't be tricked into decrypting it.
    private static readonly byte[] Entropy = "ChatbotAddonDemo.credentials.v1"u8.ToArray();

    public string? Get(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null; // Copied from another user/machine: treat as absent; the user re-enters it.
        }
    }

    public void Set(string name, string secret)
    {
        Directory.CreateDirectory(directory);
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor(name), cipher);
    }

    public void Remove(string name) => File.Delete(PathFor(name));

    private string PathFor(string name)
    {
        if (name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException($"Invalid credential name '{name}'.", nameof(name));
        return Path.Combine(directory, name + ".bin");
    }
}
