using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Demo.Ai;
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpendCapError), nameof(CanSave))]
    private string _spendCap;

    /// <summary>Shown under the limit box; empty while what's typed is a valid limit (or empty, for none).</summary>
    public string SpendCapError => SpendLimitText.TryParse(SpendCap, out _) ? "" : SpendLimitText.Problem;

    /// <summary>Save stays disabled while the limit is invalid, so a typo can never turn into "no limit".</summary>
    public bool CanSave => SpendLimitText.TryParse(SpendCap, out _);

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
        CredentialDescription = IsDemo ? "Demo mode: scripted replies, no key needed." : credential.Describe();
    }

    // ---- Save ----------------------------------------------------------------------

    /// <summary>Returns false (and saves nothing) while the spending limit is invalid.</summary>
    public bool Save()
    {
        if (!SpendLimitText.TryParse(SpendCap, out var cap)) return false;
        var models = new Dictionary<AiProvider, string>(_original.Models) { [Provider] = Model.Trim() };
        _services.Settings.Save(_original with
        {
            ChatEnabled = ChatEnabled,
            Provider = Provider,
            Models = models,
            SessionSpendCapUsd = cap,
        });
        return true;
    }
}
