using System.Text;
using Demo.Ai;
using Demo.Ai.Credentials;
using Microsoft.Extensions.AI;

namespace Demo.Ai.Tests;

public class CredentialTests
{
    private sealed class FakePolicy(string? gateway = null, string? token = null, bool disableUserKeys = false) : IMachinePolicy
    {
        public string? GatewayUrl(AiProvider provider) => gateway;
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

        var gateway = Resolver(store, new FakePolicy("https://llm.corp.example/anthropic", "gw-token"), env).Resolve(AiProvider.Anthropic);
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
    public void Policy_that_disables_personal_keys_also_ignores_environment_keys()
    {
        var env = new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "env-key" };

        Assert.False(Resolver(new InMemoryCredentialStore(), new FakePolicy(disableUserKeys: true), env).Resolve(AiProvider.Anthropic).IsAvailable);

        var gateway = Resolver(new InMemoryCredentialStore(), new FakePolicy("https://llm.corp.example/", disableUserKeys: true), env).Resolve(AiProvider.Anthropic);
        Assert.Equal(CredentialSource.Gateway, gateway.Source);
    }

    [Theory]
    [InlineData("http://llm.corp.example/anthropic")]
    [InlineData("ftp://llm.corp.example/")]
    public void Gateway_must_use_https(string url)
    {
        var store = new InMemoryCredentialStore();
        store.Set(CredentialResolver.KeyName(AiProvider.Anthropic), "user-key");

        var resolved = Resolver(store, new FakePolicy(url, "gw-token")).Resolve(AiProvider.Anthropic);

        Assert.False(resolved.IsAvailable); // and no fallback to the personal key
        Assert.Null(resolved.ApiKey);
        Assert.Contains("doesn't use https", resolved.Describe());
        var refused = Assert.Throws<InvalidOperationException>(() => ChatClientFactory.Create(AiProvider.Anthropic, "m", resolved));
        Assert.Contains("https", refused.Message);
    }

    [Theory]
    [InlineData("llm.corp.example/anthropic")] // no scheme: not an absolute address
    [InlineData("https://")]
    [InlineData("not a url")]
    [InlineData("")] // present but empty is still "IT set a gateway"
    public void Gateway_that_is_not_a_valid_address_blocks_personal_keys(string url)
    {
        var store = new InMemoryCredentialStore();
        store.Set(CredentialResolver.KeyName(AiProvider.Anthropic), "user-key");
        var env = new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "env-key" };

        var resolved = Resolver(store, new FakePolicy(url, "gw-token"), env).Resolve(AiProvider.Anthropic);

        Assert.False(resolved.IsAvailable); // IT set a gateway: never fall back, even when it's misconfigured
        Assert.Null(resolved.ApiKey);
        Assert.Contains("isn't a valid address", resolved.Describe());
    }

    [Theory]
    [InlineData("http://localhost:4000/")]
    [InlineData("http://127.0.0.1:4000/")]
    [InlineData("http://[::1]:4000/")]
    public void Gateway_on_this_pc_may_use_http(string url) =>
        Assert.Equal(CredentialSource.Gateway, Resolver(new InMemoryCredentialStore(), new FakePolicy(url)).Resolve(AiProvider.OpenAI).Source);

    [Fact]
    public void OpenAI_storage_stays_off_even_with_a_caller_supplied_options_factory()
    {
#pragma warning disable OPENAI001
        using var client = new ScriptedChatClient([]);
        var fromCaller = ChatClientFactory.WithStorageOff(_ => new OpenAI.Responses.CreateResponseOptions { StoredOutputEnabled = true, Instructions = "from caller" });
        var raw = Assert.IsType<OpenAI.Responses.CreateResponseOptions>(fromCaller(client));
        Assert.False(raw.StoredOutputEnabled);
        Assert.Equal("from caller", raw.Instructions);
        Assert.Contains(OpenAI.Responses.IncludedResponseProperty.ReasoningEncryptedContent, raw.IncludedProperties);

        foreach (var other in new Func<IChatClient, object?>?[] { null, _ => null, _ => "not options" })
        {
            var made = Assert.IsType<OpenAI.Responses.CreateResponseOptions>(ChatClientFactory.WithStorageOff(other)(client));
            Assert.False(made.StoredOutputEnabled);
            Assert.Single(made.IncludedProperties);
        }
#pragma warning restore OPENAI001
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
            store.Save(new AiSettings
            {
                ChatEnabled = true,
                Provider = AiProvider.OpenAI,
                Models = { [AiProvider.OpenAI] = "gpt-x" },
                AgentsEnabled = true,
                McpServers = [new("github", "npx -y @modelcontextprotocol/server-github")],
            });
            var loaded = store.Load();
            Assert.True(loaded.ChatEnabled);
            Assert.True(loaded.AgentsEnabled);
            Assert.Equal(new McpServerSetting("github", "npx -y @modelcontextprotocol/server-github"), Assert.Single(loaded.McpServers));
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
    [InlineData("""{ "ChatEnabled": true, "Provider": 7 }""")]
    [InlineData("""{ "ChatEnabled": true, "Provider": "Gemini" }""")]
    [InlineData("""{ "ChatEnabled": true, "Models": null }""")]
    [InlineData("""{ "ChatEnabled": true, "Models": { "OpenAI": null } }""")]
    [InlineData("""{ "ChatEnabled": true, "SessionSpendCapUsd": -5 }""")]
    [InlineData("""{ "ChatEnabled": true, "McpServers": null }""")]
    [InlineData("""{ "ChatEnabled": true, "McpServers": [ null, { "Name": "github" } ] }""")]
    [InlineData("null")]
    public void Damaged_settings_load_as_something_usable(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json);
            var loaded = new AiSettingsStore(path).Load();

            Assert.Equal(ProviderInfo.For(loaded.Provider).DefaultModel, loaded.ModelFor(loaded.Provider)); // doesn't throw
            Assert.Equal("gpt-6-astra", loaded.ModelFor(AiProvider.OpenAI));
            Assert.True(loaded.SessionSpendCapUsd is null or > 0);
            Assert.Empty(loaded.McpServers);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("5", "5")]
    [InlineData("1.50", "1.50")]
    [InlineData(" $2.25 ", "2.25")]
    [InlineData("$ 3", "3")]
    [InlineData(".5", "0.5")]
    public void Spend_limit_accepts_dollar_amounts_and_only_empty_means_no_limit(string text, string? expected)
    {
        Assert.True(SpendLimitText.TryParse(text, out var limit));
        Assert.Equal(expected is null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), limit);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.00")]
    [InlineData("-1")]
    [InlineData("5 USD")]
    [InlineData("1,50")] // 1.5 in some regions, 150 in others: refused rather than guessed
    [InlineData("1,000")]
    [InlineData("1e3")]
    [InlineData("abc")]
    [InlineData("$")]
    [InlineData("$$5")]
    public void Spend_limit_refuses_anything_else(string text) =>
        Assert.False(SpendLimitText.TryParse(text, out _));

    [Fact]
    public void Spend_limit_reads_a_dot_as_the_decimal_point_in_any_region()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.True(SpendLimitText.TryParse("1.50", out var limit));
            Assert.Equal(1.50m, limit);
            Assert.Equal("1.50", SpendLimitText.Format(1.5m)); // and shows it the same way, so it reads back the same
            Assert.Equal("", SpendLimitText.Format(null));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Settings_file_that_cannot_be_read_loads_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "ChatEnabled": true }""");
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) // held open by something else
                Assert.False(new AiSettingsStore(path).Load().ChatEnabled);
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

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData("1", true)]   // REG_SZ instead of the documented DWORD: still a clear "yes"
    [InlineData("0", false)]
    [InlineData("yes", true)] // anything else that is present counts as "disabled": an unreadable policy fails closed
    [InlineData(null, false)] // not set
    public void A_DisableUserKeys_policy_of_the_wrong_registry_type_still_disables_personal_keys(object? value, bool disabled) =>
        Assert.Equal(disabled, RegistryMachinePolicy.IsSet(value));

    [Fact]
    public void A_gateway_address_of_the_wrong_registry_type_blocks_personal_keys_rather_than_being_ignored()
    {
        Assert.Null(RegistryMachinePolicy.AsText(null));
        Assert.Equal("https://gw.example", RegistryMachinePolicy.AsText("https://gw.example"));
        var multiString = RegistryMachinePolicy.AsText(new[] { "https://gw.example" });
        Assert.NotNull(multiString);

        var store = new InMemoryCredentialStore();
        store.Set(CredentialResolver.KeyName(AiProvider.Anthropic), "personal");
        var resolved = Resolver(store, new FakePolicy(gateway: multiString)).Resolve(AiProvider.Anthropic);
        Assert.False(resolved.IsAvailable);
        Assert.Contains("isn't a valid address", resolved.Problem);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData("1", true)]
    [InlineData(0, false)]
    [InlineData(null, false)]
    [InlineData(new byte[] { 1 }, false)] // trust loosens the rules, so a value of an odd type is a "no"
    public void Only_a_clear_yes_from_IT_marks_an_MCP_server_safe(object? value, bool trusted) =>
        Assert.Equal(trusted, RegistryMachinePolicy.McpServer("perforce", "p4mcp.exe", value).Trusted);

    [Fact]
    public void An_MCP_server_command_of_the_wrong_registry_type_is_kept_so_it_can_be_reported() =>
        Assert.Equal(new PolicyMcpServer("perforce", null, false), RegistryMachinePolicy.McpServer("perforce", new[] { "p4mcp.exe" }, null));

    [Fact]
    public void Without_policy_agents_and_personal_MCP_servers_are_allowed()
    {
        IMachinePolicy none = NoMachinePolicy.Instance;
        Assert.False(none.DisableAgentConnections);
        Assert.False(none.DisableUserMcpServers);
        Assert.Empty(none.McpServers);
    }

    [Fact]
    public void Factory_refuses_without_credentials() =>
        Assert.Throws<InvalidOperationException>(() => ChatClientFactory.Create(AiProvider.Anthropic, "m", ResolvedCredential.None));
}

[CollectionDefinition(nameof(ProcessEnvironment), DisableParallelization = true)]
public sealed class ProcessEnvironment;

/// <summary>Changes a process-wide environment variable, so it runs on its own: other tests must not see the value.</summary>
[Collection(nameof(ProcessEnvironment))]
public class EnvironmentVariableTests
{
    [Fact]
    public void Anthropic_address_cannot_be_redirected_by_an_environment_variable()
    {
        const string variable = "ANTHROPIC_BASE_URL";
        var saved = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "http://attacker.example");
            Assert.Equal("http://attacker.example", new Anthropic.AnthropicClient { ApiKey = "k" }.BaseUrl); // what the SDK does left to itself

            var personal = ChatClientFactory.CreateAnthropicClient(new ResolvedCredential(CredentialSource.UserKey, "k"));
            Assert.Equal("https://api.anthropic.com", personal.BaseUrl);
            var gateway = ChatClientFactory.CreateAnthropicClient(new ResolvedCredential(CredentialSource.Gateway, null, new Uri("https://gw.example/anthropic")));
            Assert.Equal("https://gw.example/anthropic", gateway.BaseUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, saved);
        }
    }
}
