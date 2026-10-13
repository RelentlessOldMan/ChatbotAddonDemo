using System.Text;
using Demo.Ai;
using Demo.Ai.Credentials;

namespace Demo.Ai.Tests;

public class CredentialTests
{
    private sealed class FakePolicy(Uri? gateway = null, string? token = null, bool disableUserKeys = false) : IMachinePolicy
    {
        public Uri? GatewayUrl(AiProvider provider) => gateway;
        public string? GatewayToken(AiProvider provider) => token;
        public bool DisableUserKeys => disableUserKeys;
    }

    private static CredentialResolver Resolver(ICredentialStore store, IMachinePolicy? policy = null, Dictionary<string, string>? env = null) =>
        new(store, policy ?? NoMachinePolicy.Instance, name => env?.GetValueOrDefault(name));

    [Fact]
    public void Nothing_configured_resolves_to_None()
    {
        var resolved = Resolver(new InMemoryCredentialStore()).Resolve(AiProvider.Anthropic);
        Assert.False(resolved.IsAvailable);
        Assert.Equal("Not configured", resolved.Describe());
    }

    [Fact]
    public void User_key_is_used_and_masked_in_descriptions()
    {
        var resolver = Resolver(new InMemoryCredentialStore());
        resolver.SaveUserKey(AiProvider.OpenAI, "  sk-test-1234567890abcd  ");

        var resolved = resolver.Resolve(AiProvider.OpenAI);

        Assert.Equal(CredentialSource.UserKey, resolved.Source);
        Assert.Equal("sk-test-1234567890abcd", resolved.ApiKey);
        Assert.Equal("Your saved API key (sk-t…abcd)", resolved.Describe());
        Assert.False(Resolver(new InMemoryCredentialStore()).Resolve(AiProvider.Anthropic).IsAvailable);
    }

    [Fact]
    public void Precedence_is_gateway_then_environment_then_user_key()
    {
        var store = new InMemoryCredentialStore();
        store.Set(CredentialResolver.KeyName(AiProvider.Anthropic), "user-key");
        var env = new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "env-key" };

        Assert.Equal(CredentialSource.UserKey, Resolver(store).Resolve(AiProvider.Anthropic).Source);
        Assert.Equal("env-key", Resolver(store, env: env).Resolve(AiProvider.Anthropic).ApiKey);

        var gateway = Resolver(store, new FakePolicy(new Uri("https://llm.corp.example/anthropic"), "gw-token"), env).Resolve(AiProvider.Anthropic);
        Assert.Equal(CredentialSource.Gateway, gateway.Source);
        Assert.Equal("gw-token", gateway.ApiKey);
        Assert.Equal("Your organization's AI gateway (llm.corp.example)", gateway.Describe());
    }

    [Fact]
    public void Policy_can_disable_personal_keys()
    {
        var store = new InMemoryCredentialStore();
        store.Set(CredentialResolver.KeyName(AiProvider.Anthropic), "user-key");
        var resolver = Resolver(store, new FakePolicy(disableUserKeys: true));

        Assert.False(resolver.Resolve(AiProvider.Anthropic).IsAvailable);
        Assert.Throws<InvalidOperationException>(() => resolver.SaveUserKey(AiProvider.Anthropic, "x"));
    }

    [Fact]
    public void Dpapi_store_round_trips_without_writing_plaintext()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dpapi-{Guid.NewGuid():N}");
        try
        {
            var store = new DpapiCredentialStore(dir);
            const string secret = "sk-ant-super-secret-value";
            store.Set("Anthropic-api-key", secret);

            Assert.Equal(secret, new DpapiCredentialStore(dir).Get("Anthropic-api-key"));
            var raw = File.ReadAllBytes(Path.Combine(dir, "Anthropic-api-key.bin"));
            Assert.DoesNotContain("super-secret", Encoding.UTF8.GetString(raw));
            Assert.DoesNotContain("super-secret", Encoding.Unicode.GetString(raw));

            store.Remove("Anthropic-api-key");
            Assert.Null(store.Get("Anthropic-api-key"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Dpapi_store_treats_tampered_files_as_absent()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dpapi-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "x.bin"), [1, 2, 3, 4]);
            Assert.Null(new DpapiCredentialStore(dir).Get("x"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData(@"a\b")]
    public void Dpapi_store_rejects_path_like_names(string name) =>
        Assert.Throws<ArgumentException>(() => new DpapiCredentialStore(Path.GetTempPath()).Get(name));

    [Fact]
    public void Settings_never_contain_secrets_and_survive_corruption()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new AiSettingsStore(path);
            store.Save(new AiSettings { ChatEnabled = true, Provider = AiProvider.OpenAI, Models = { [AiProvider.OpenAI] = "gpt-x" } });
            var loaded = store.Load();
            Assert.True(loaded.ChatEnabled);
            Assert.Equal("gpt-x", loaded.ModelFor(AiProvider.OpenAI));
            Assert.Equal("claude-opus-5-5", loaded.ModelFor(AiProvider.Anthropic));

            File.WriteAllText(path, "{ not json");
            Assert.False(store.Load().ChatEnabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(AiProvider.Anthropic)]
    [InlineData(AiProvider.OpenAI)]
    public void Factory_builds_clients_for_both_providers_without_network(AiProvider provider)
    {
        using var client = ChatClientFactory.Create(provider, "model-x", new ResolvedCredential(CredentialSource.UserKey, "k"));
        Assert.NotNull(client);
        using var gateway = ChatClientFactory.Create(provider, "model-x", new ResolvedCredential(CredentialSource.Gateway, null, new Uri("https://gw.example/")));
        Assert.NotNull(gateway);
    }

    [Fact]
    public void Factory_refuses_without_credentials() =>
        Assert.Throws<InvalidOperationException>(() => ChatClientFactory.Create(AiProvider.Anthropic, "m", ResolvedCredential.None));
}
