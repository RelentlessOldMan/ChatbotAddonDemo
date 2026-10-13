using System.IO;
using Demo.Ai;
using Demo.Ai.Credentials;
using Demo.Capabilities;
using Demo.Capabilities.TestBench;
using Demo.Domain;
using Microsoft.Extensions.AI;

namespace Demo.Desktop;

/// <summary>Command-line options. <c>--demo</c> = fresh example workspace, scripted assistant, throwaway profile.</summary>
public sealed record LaunchOptions(bool Demo, string? ProfileDirectory, string? Workspace, string? SnapshotPipe)
{
    public static LaunchOptions Parse(string[] args)
    {
        string? Value(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        return new LaunchOptions(args.Contains("--demo"), Value("--profile"), Value("--workspace"), Value("--snapshot-pipe"));
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

        // The same workspace folder the console tool uses by default, so both apps see the same plans and runs.
        var workspace = options.Workspace ?? Environment.GetEnvironmentVariable("DEMO_WORKSPACE") ?? Path.Combine(ProfileDirectory, "workspace");
        Bench = new TestBenchService(workspace, BenchOptions.FromEnvironment());

        Settings = new AiSettingsStore(Path.Combine(ProfileDirectory, "settings.json"));
        CredentialStore = new DpapiCredentialStore(Path.Combine(ProfileDirectory, "credentials"));
        Credentials = new CredentialResolver(CredentialStore, new RegistryMachinePolicy());
        Selection = new SelectionContext();

        // What the assistant may do: the shared bench capabilities, plus one that only the running UI can answer.
        Catalog = CapabilityCatalog.FromObject(new TestBenchCapabilities(Bench))
            .Concat(CapabilityCatalog.FromObject(new AppContextCapabilities(Selection)));
        ChatOptions = ChatSessionOptions.For(TestBenchCapabilities.Instructions + "\n" + AppContextCapabilities.Instructions);
    }

    public LaunchOptions Options { get; }
    public bool IsDemo => Options.Demo;
    public string ProfileDirectory { get; }
    public TestBenchService Bench { get; }
    public AiSettingsStore Settings { get; }
    public ICredentialStore CredentialStore { get; }
    public CredentialResolver Credentials { get; }
    public SelectionContext Selection { get; }
    public CapabilityCatalog Catalog { get; }
    public ChatSessionOptions ChatOptions { get; }

    /// <summary>Where chat will connect, and who pays. In demo mode, nowhere.</summary>
    public ResolvedCredential ResolveCredential(AiProvider provider) =>
        IsDemo ? new ResolvedCredential(CredentialSource.None) : Credentials.Resolve(provider);

    public IChatClient CreateChatClient(AiSettings settings) =>
        IsDemo
            ? DemoScript.CreateClient()
            : ChatClientFactory.Create(settings.Provider, settings.ModelFor(settings.Provider), Credentials.Resolve(settings.Provider));
}
