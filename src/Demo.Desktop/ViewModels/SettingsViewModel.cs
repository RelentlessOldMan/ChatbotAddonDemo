using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Demo.Ai;
using Demo.Mcp;
using Microsoft.Extensions.AI;

namespace Demo.Desktop.ViewModels;

/// <summary>Settings for the AI assistant. Secrets go to the credential store, never to settings.json.</summary>
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
        _spendCap = SpendLimitText.Format(_original.SessionSpendCapUsd);
        // Shown off while IT has it off; the user's own choice is kept for when IT allows it again.
        _agentsEnabled = _original.AgentsEnabled && !AgentsBlockedByPolicy;
        McpServers.CollectionChanged += (_, e) =>
        {
            foreach (McpServerRow row in e.NewItems ?? Array.Empty<McpServerRow>()) row.PropertyChanged += OnServerRowChanged;
            OnServerRowChanged(null, null);
        };
        foreach (var server in _original.McpServers) AddServerRow(new McpServerRow { Name = server.Name, Command = server.Command });
        PolicyServers = services.Policy.McpServers
            .Select(p => $"{p.Name}: {p.Command ?? "(not usable)"}" + (p.Trusted ? " · runs without asking" : ""))
            .ToList();
        RefreshCredential();
    }

    public bool IsDemo => _services.IsDemo;
    /// <summary>Whether this PC lets people use their own key at all (IT can turn it off).</summary>
    public bool UserKeysAllowed => _services.Credentials.UserKeysAllowed;

    /// <summary>The key box is shown in demo mode too, so the dialog looks as it will, but nothing can be typed into it.</summary>
    public bool CanEditKey => UserKeysAllowed && !IsDemo;

    // ---- AI assistant -------------------------------------------------------------

    [ObservableProperty] private bool _chatEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Provider), nameof(ProviderName), nameof(KeyHelpText))]
    private bool _useOpenAI;

    [ObservableProperty] private string _model;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpendCapError), nameof(CanSave))]
    private string _spendCap;

    /// <summary>Shown under the limit box; empty while what's typed is a valid limit (or empty, for none).</summary>
    public string SpendCapError => SpendLimitText.TryParse(SpendCap, out _) ? "" : SpendLimitText.Problem;

    /// <summary>
    /// Save stays disabled while the limit is invalid, so a typo can never turn into "no limit", and while an MCP
    /// server entry can't be used, so it isn't silently skipped later.
    /// </summary>
    public bool CanSave => SpendLimitText.TryParse(SpendCap, out _) && McpServersError.Length == 0;

    // ---- Coding agents and other MCP servers ---------------------------------------------

    [ObservableProperty] private bool _agentsEnabled;

    /// <summary>IT has turned agent connections off for this PC: the box is shown, but switched off.</summary>
    public bool AgentsBlockedByPolicy => _services.Policy.DisableAgentConnections;
    public bool CanChangeAgents => !AgentsBlockedByPolicy;

    /// <summary>The servers the user added. A row left with no name and no command is dropped on Save.</summary>
    public ObservableCollection<McpServerRow> McpServers { get; } = [];

    /// <summary>The servers IT set up, one line each; the user can't change them.</summary>
    public IReadOnlyList<string> PolicyServers { get; }
    public bool HasPolicyServers => PolicyServers.Count > 0;

    /// <summary>IT can limit the assistant to its own servers.</summary>
    public bool UserServersAllowed => !_services.Policy.DisableUserMcpServers;
    public bool UserServersBlocked => !UserServersAllowed;

    [RelayCommand]
    private void AddServer() => AddServerRow(new McpServerRow());

    [RelayCommand]
    private void RemoveServer(McpServerRow row) => McpServers.Remove(row);

    /// <summary>What's wrong with the first unusable entry; empty when all can be used.</summary>
    public string McpServersError
    {
        get
        {
            var rows = FilledServerRows().ToList();
            var duplicate = rows.GroupBy(r => r.Name.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null) return $"Two servers are called '{duplicate.Key}': each needs its own name.";
            return rows.Select(r => new OutsideServer(r.Name.Trim(), r.Command.Trim(), false, ServerOrigin.User).Problem()).FirstOrDefault(p => p is not null) ?? "";
        }
    }

    private IEnumerable<McpServerRow> FilledServerRows() =>
        McpServers.Where(r => !string.IsNullOrWhiteSpace(r.Name) || !string.IsNullOrWhiteSpace(r.Command));

    private void AddServerRow(McpServerRow row) => McpServers.Add(row);

    private void OnServerRowChanged(object? sender, PropertyChangedEventArgs? e)
    {
        OnPropertyChanged(nameof(McpServersError));
        OnPropertyChanged(nameof(CanSave));
    }

    /// <summary>
    /// Saving or removing a key takes effect at once, not on Save, so the open chat must be rebuilt
    /// when the dialog closes even if it was cancelled.
    /// </summary>
    public bool CredentialsChanged { get; private set; }
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
        CredentialsChanged = true;
        RefreshCredential();
        TestResult = "Key saved (encrypted for your Windows account).";
    }

    [RelayCommand]
    private void RemoveKey()
    {
        _services.Credentials.RemoveUserKey(Provider);
        CredentialsChanged = true;
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
        CredentialDescription = IsDemo ? "Demo mode: scripted replies, no key needed (the key box below is switched off)." : credential.Describe();
    }

    // ---- Save ----------------------------------------------------------------------

    /// <summary>Returns false (and saves nothing) while the spending limit or a server entry is invalid.</summary>
    public bool Save()
    {
        if (!SpendLimitText.TryParse(SpendCap, out var cap) || !CanSave) return false;
        var models = new Dictionary<AiProvider, string>(_original.Models) { [Provider] = Model.Trim() };
        _services.Settings.Save(_original with
        {
            ChatEnabled = ChatEnabled,
            Provider = Provider,
            Models = models,
            SessionSpendCapUsd = cap,
            AgentsEnabled = AgentsBlockedByPolicy ? _original.AgentsEnabled : AgentsEnabled,
            McpServers = FilledServerRows().Select(r => new McpServerSetting(r.Name.Trim(), r.Command.Trim())).ToList(),
        });
        return true;
    }
}

/// <summary>One editable line in the list of other MCP servers.</summary>
public sealed partial class McpServerRow : ObservableObject
{
    [ObservableProperty] private string _name = "";

    /// <summary>The command that starts the server, or its https address.</summary>
    [ObservableProperty] private string _command = "";
}
