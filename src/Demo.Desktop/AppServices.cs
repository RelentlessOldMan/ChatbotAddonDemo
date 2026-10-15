using System.IO;
using Demo.Ai;
using Demo.Ai.Credentials;
using Demo.Capabilities;
using Demo.Capabilities.TestBench;
using Demo.Domain;
using Demo.Mcp;
using Microsoft.Extensions.AI;

namespace Demo.Desktop;

/// <summary>
/// Command-line options. <c>--demo</c> = fresh example workspace, scripted assistant, throwaway profile.
/// <c>--agent-pipe</c> = the name coding agents connect under (tests use their own, so they never meet the user's app).
/// </summary>
public sealed record LaunchOptions(bool Demo, string? ProfileDirectory, string? Workspace, string? SnapshotPipe, string? AgentPipe = null)
{
    public static LaunchOptions Parse(string[] args)
    {
        string? Value(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        return new LaunchOptions(args.Contains("--demo"), Value("--profile"), Value("--workspace"), Value("--snapshot-pipe"), Value("--agent-pipe"));
    }
}

/// <summary>Composition root: builds every service once and hands them to view models.</summary>
public sealed class AppServices
{
    /// <summary>What a coding agent is told about working with the open app, on top of the domain instructions.</summary>
    private const string AgentInstructions =
        "- You are working with the TestBench window the user has open. Creating plans or reports and using the bench wait for the user to approve them in that window; if they decline, accept it and don't retry unless they ask.";

    public AppServices(LaunchOptions options)
    {
        Options = options;
        ProfileDirectory = options.ProfileDirectory
            ?? (options.Demo
                ? Path.Combine(Path.GetTempPath(), "ChatbotAddonDemo-demo", Guid.NewGuid().ToString("N"))
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatbotAddonDemo"));

        // This app's own workspace. The console tool is a separate program with a workspace of its own.
        var workspace = options.Workspace ?? Path.Combine(ProfileDirectory, "workspace");
        Bench = new TestBenchService(workspace, BenchOptions.FromEnvironment());

        Settings = new AiSettingsStore(Path.Combine(ProfileDirectory, "settings.json"));
        CredentialStore = new DpapiCredentialStore(Path.Combine(ProfileDirectory, "credentials"));
        Policy = new RegistryMachinePolicy();
        Credentials = new CredentialResolver(CredentialStore, Policy);
        Selection = new SelectionContext();

        // What AI may do here: the bench capabilities, plus one that only the running UI can answer. Every AI reaches
        // them through the hub: the chat panel in-process, coding agents through the agent door.
        var domainInstructions = TestBenchCapabilities.Instructions + "\n" + AppContextCapabilities.Instructions;
        var catalog = CapabilityCatalog.FromObject(new TestBenchCapabilities(Bench))
            .Concat(CapabilityCatalog.FromObject(new AppContextCapabilities(Selection)));
        Hub = new McpHub(catalog, "testbench", domainInstructions + "\n" + AgentInstructions);
        ChatOptions = ChatSessionOptions.For(domainInstructions);
        AgentPipeName = options.AgentPipe ?? AgentDoor.DefaultPipeName;
    }

    public LaunchOptions Options { get; }
    public bool IsDemo => Options.Demo;
    public string ProfileDirectory { get; }
    public TestBenchService Bench { get; }
    public AiSettingsStore Settings { get; }
    public ICredentialStore CredentialStore { get; }
    public IMachinePolicy Policy { get; }
    public CredentialResolver Credentials { get; }
    public SelectionContext Selection { get; }
    public McpHub Hub { get; }
    public ChatSessionOptions ChatOptions { get; }

    /// <summary>The named pipe coding agents reach this app through (via <c>demo-desktop.exe mcp</c>).</summary>
    public string AgentPipeName { get; }

    /// <summary>Whether coding agents may connect: the user turned it on, and IT hasn't turned it off.</summary>
    public bool AgentsAllowed(AiSettings settings) => settings.AgentsEnabled && !Policy.DisableAgentConnections;

    /// <summary>The outside MCP servers the assistant connects to: IT's, then the user's own unless IT allows only its list.</summary>
    public IReadOnlyList<OutsideServer> OutsideServers(AiSettings settings) => OutsideServer.Combine(
        Policy.McpServers.Select(s => new OutsideServer(s.Name, s.Command ?? "", s.Trusted, ServerOrigin.Policy)),
        settings.McpServers.Select(s => new OutsideServer(s.Name.Trim(), s.Command.Trim(), Trusted: false, ServerOrigin.User)),
        userServersAllowed: !Policy.DisableUserMcpServers);

    /// <summary>Where chat will connect, and who pays. In demo mode, nowhere.</summary>
    public ResolvedCredential ResolveCredential(AiProvider provider) =>
        IsDemo ? new ResolvedCredential(CredentialSource.None) : Credentials.Resolve(provider);

    public IChatClient CreateChatClient(AiSettings settings) =>
        IsDemo
            ? DemoScript.CreateClient()
            : ChatClientFactory.Create(settings.Provider, settings.ModelFor(settings.Provider), Credentials.Resolve(settings.Provider));
}
