using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Demo.Ai;
using Demo.Mcp;
using Microsoft.Extensions.AI;

namespace Demo.Desktop.ViewModels;

/// <summary>Settings → AI assistant and Settings → Agent access. Secrets go to the credential store, never to settings.json.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly AiSettings _original;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        _original = services.Settings.Load();
        _chatEnabled = _original.ChatEnabled;
        _useOpenAI = _original.Provider == AiProvider.OpenAI;
        _model = _original.ModelFor(_original.Provider);
        _spendCap = _original.SessionSpendCapUsd?.ToString("0.00") ?? "";
        _mcpEnabled = _original.McpEnabled;
        _mcpAllowWrites = _original.McpAllowWrites;
        _mcpPort = _original.McpPort;
        _maskedToken = Mask(services.Mcp.Token);
        RefreshCredential();
    }

    public bool IsDemo => _services.IsDemo;
    public bool UserKeysAllowed => _services.Credentials.UserKeysAllowed && !IsDemo;

    // ---- AI assistant -------------------------------------------------------------

    [ObservableProperty] private bool _chatEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Provider), nameof(ProviderName), nameof(KeyHelpText))]
    private bool _useOpenAI;

    [ObservableProperty] private string _model;
    [ObservableProperty] private string _spendCap;
    [ObservableProperty] private string _credentialDescription = "";
    [ObservableProperty] private bool _hasCredential;
    [ObservableProperty] private string _testResult = "";

    public AiProvider Provider => UseOpenAI ? AiProvider.OpenAI : AiProvider.Anthropic;
    public string ProviderName => ProviderInfo.For(Provider).DisplayName;
    public string KeyHelpText => $"Get a key from {ProviderInfo.For(Provider).ConsoleUrl}";

    partial void OnUseOpenAIChanged(bool value)
    {
        Model = _original.ModelFor(Provider);
        RefreshCredential();
    }

    /// <summary>Called from the view with the PasswordBox contents (PasswordBox can't data-bind, by design).</summary>
    public void SaveKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        _services.Credentials.SaveUserKey(Provider, key);
        RefreshCredential();
        TestResult = "Key saved (encrypted for your Windows account).";
    }

    [RelayCommand]
    private void RemoveKey()
    {
        _services.Credentials.RemoveUserKey(Provider);
        RefreshCredential();
        TestResult = "Key removed.";
    }

    [RelayCommand]
    private void OpenKeyPage() => Process.Start(new ProcessStartInfo(ProviderInfo.For(Provider).ConsoleUrl) { UseShellExecute = true });

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (IsDemo) { TestResult = "Demo mode: nothing to test."; return; }
        TestResult = "Testing…";
        try
        {
            using var client = ChatClientFactory.Create(Provider, Model, _services.Credentials.Resolve(Provider));
            var response = await client.GetResponseAsync("Reply with the single word OK.", new ChatOptions { MaxOutputTokens = 2000 });
            TestResult = $"Connected ✓  ({response.ModelId ?? Model}, {response.Usage?.TotalTokenCount ?? 0} tokens)";
        }
        catch (Exception ex)
        {
            TestResult = $"Failed: {ex.GetBaseException().Message}";
        }
    }

    private void RefreshCredential()
    {
        var credential = _services.ResolveCredential(Provider);
        HasCredential = credential.IsAvailable;
        CredentialDescription = IsDemo ? "Demo mode: scripted replies, no key needed." : credential.Describe();
    }

    // ---- Agent access (MCP) --------------------------------------------------------

    [ObservableProperty] private bool _mcpEnabled;
    [ObservableProperty] private bool _mcpAllowWrites;
    [ObservableProperty] private int _mcpPort;
    [ObservableProperty] private string _maskedToken;
    [ObservableProperty] private string _copyResult = "";

    private Uri Endpoint => new($"http://127.0.0.1:{McpPort}/mcp");

    public string CliPath => Path.Combine(AppContext.BaseDirectory, "demo-cli.exe");

    [RelayCommand]
    private void CopyClaudeCode() => Copy(McpClientConfig.ClaudeCodeHttp(Endpoint, _services.Mcp.Token), "Claude Code command");

    [RelayCommand]
    private void CopyCodex() => Copy(McpClientConfig.CodexHttp(Endpoint, _services.Mcp.Token), "Codex setup");

    [RelayCommand]
    private void CopyCliClaudeCode() => Copy(McpClientConfig.ClaudeCodeStdio(CliPath, allowWrites: false), "console-tool command");

    [RelayCommand]
    private void RegenerateToken()
    {
        MaskedToken = Mask(_services.Mcp.RegenerateToken());
        CopyResult = "New token created. Agents using the old one are disconnected after you save.";
    }

    private void Copy(string text, string what)
    {
        Clipboard.SetText(text);
        CopyResult = $"Copied {what} to the clipboard.";
    }

    private static string Mask(string token) => $"{token[..4]}••••••••••••{token[^4..]}";

    // ---- Save ----------------------------------------------------------------------

    public void Save()
    {
        var models = new Dictionary<AiProvider, string>(_original.Models) { [Provider] = Model.Trim() };
        _services.Settings.Save(_original with
        {
            ChatEnabled = ChatEnabled,
            Provider = Provider,
            Models = models,
            SessionSpendCapUsd = decimal.TryParse(SpendCap, out var cap) && cap > 0 ? cap : null,
            McpEnabled = McpEnabled,
            McpAllowWrites = McpAllowWrites,
            McpPort = McpPort,
        });
    }
}
