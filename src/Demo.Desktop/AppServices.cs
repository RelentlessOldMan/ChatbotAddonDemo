using System.IO;
using Demo.Ai;
using Demo.Ai.Credentials;
using Demo.Capabilities;
using Demo.Capabilities.Workshop;
using Demo.Domain;
using Microsoft.Extensions.AI;

namespace Demo.Desktop;

/// <summary>Command-line options. <c>--demo</c> = seeded data, scripted assistant, throwaway profile.</summary>
public sealed record LaunchOptions(bool Demo, string? ProfileDirectory, string? DataPath, string? SnapshotPipe)
{
    public static LaunchOptions Parse(string[] args)
    {
        string? Value(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        return new LaunchOptions(args.Contains("--demo"), Value("--profile"), Value("--data"), Value("--snapshot-pipe"));
    }
}

/// <summary>Composition root: builds every service once and hands them to view models.</summary>
public sealed class AppServices
{
    public AppServices(LaunchOptions options)
    {
        Options = options;
        ProfileDirectory = options.ProfileDirectory
            ?? (options.Demo
                ? Path.Combine(Path.GetTempPath(), "ChatbotAddonDemo-demo", Guid.NewGuid().ToString("N"))
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatbotAddonDemo"));

        DataPath = options.DataPath ?? Environment.GetEnvironmentVariable("DEMO_DATA") ?? Path.Combine(ProfileDirectory, "workshop.json");
        Workshop = new WorkshopService(new JsonFileDataStore(DataPath));
        Settings = new AiSettingsStore(Path.Combine(ProfileDirectory, "settings.json"));
        CredentialStore = new DpapiCredentialStore(Path.Combine(ProfileDirectory, "credentials"));
        Credentials = new CredentialResolver(CredentialStore, new RegistryMachinePolicy());
        Selection = new SelectionContext();
        Catalog = CapabilityCatalog.FromObject(new WorkshopCapabilities(Workshop))
            .Concat(CapabilityCatalog.FromObject(new AppContextCapabilities(Selection)));
        Mcp = new McpController(this);
    }

    public LaunchOptions Options { get; }
    public bool IsDemo => Options.Demo;
    public string ProfileDirectory { get; }
    public string DataPath { get; }
    public WorkshopService Workshop { get; }
    public AiSettingsStore Settings { get; }
    public ICredentialStore CredentialStore { get; }
    public CredentialResolver Credentials { get; }
    public SelectionContext Selection { get; }
    public CapabilityCatalog Catalog { get; }
    public McpController Mcp { get; }

    /// <summary>Where chat will connect, and who pays. In demo mode, nowhere.</summary>
    public ResolvedCredential ResolveCredential(AiProvider provider) =>
        IsDemo ? new ResolvedCredential(CredentialSource.None) : Credentials.Resolve(provider);

    public IChatClient CreateChatClient(AiSettings settings) =>
        IsDemo
            ? DemoScript.CreateClient()
            : ChatClientFactory.Create(settings.Provider, settings.ModelFor(settings.Provider), Credentials.Resolve(settings.Provider));
}
