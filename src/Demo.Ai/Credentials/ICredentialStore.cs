namespace Demo.Ai.Credentials;

/// <summary>Where user-entered secrets are kept at rest. Implementations must never write plaintext.</summary>
public interface ICredentialStore
{
    string? Get(string name);
    void Set(string name, string secret);
    void Remove(string name);
}

public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _secrets = [];
    public string? Get(string name) => _secrets.GetValueOrDefault(name);
    public void Set(string name, string secret) => _secrets[name] = secret;
    public void Remove(string name) => _secrets.Remove(name);
}
