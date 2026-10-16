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
/// Command-line options. <c>--demo</c> = fresh example workspace, scripted assistant, throwaway profile, no IT policy.
/// <c>--agent-pipe</c> = the name coding agents connect under (tests use their own, so they never meet the user's app).
/// </summary>
public sealed record LaunchOptions(bool Demo, string? ProfileDirectory, string? Workspace, string? SnapshotPipe, string? AgentPipe = null)
{
    /// <exception cref="ArgumentException">An option is missing its value. Never guessed: falling back to the
    /// defaults would put a test or a demo in the user's real profile, or in front of their agents.</exception>
    public static LaunchOptions Parse(string[] args)
    {
        string? Value(string name)
        {
            var at = Array.IndexOf(args, name);
            if (at < 0) return null;
            if (at + 1 >= args.Length || args[at + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"{name} needs a value.");
            return args[at + 1];
        }
        return new LaunchOptions(args.Contains("--demo"), Value("--profile"), Value("--workspace"), Value("--snapshot-pipe"), Value("--agent-pipe"));
    }
}

/// <summary>Composition root: builds every service once and hands them to view models.</summary>
public sealed class AppServices
{
    /// <param name="policy">IT's settings for this PC. Default: the registry's, except in demo mode, which is a
    /// sandbox (and what the UI tests and screenshots run in), so it looks the same on every PC.</param>
    public AppServices(LaunchOptions options, IMachinePolicy? policy = null)
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
        Policy = policy ?? (options.Demo ? NoMachinePolicy.Instance : new RegistryMachinePolicy());
        Credentials = new CredentialResolver(CredentialStore, Policy);
        Selection = new SelectionContext();

        // What AI may do here: the bench capabilities, plus one that only the running UI can answer. Every AI reaches
        // them through the hub: the chat panel in-process, coding agents through the agent door.
        Hub = new McpHub(DesktopTools.Catalog(Bench, Selection), "testbench", DesktopTools.AgentInstructions);
        ChatOptions = ChatSessionOptions.For(DesktopTools.Instructions);
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
        Policy.McpServers.Select(s => new OutsideServer(s.Name, s.Command?.Trim() ?? "", s.Trusted)),
        settings.McpServers.Select(s => new OutsideServer(s.Name.Trim(), s.Command.Trim(), Trusted: false)),
        userServersAllowed: !Policy.DisableUserMcpServers);

    /// <summary>Where chat will connect, and who pays. In demo mode, nowhere.</summary>
    public ResolvedCredential ResolveCredential(AiProvider provider) =>
        IsDemo ? new ResolvedCredential(CredentialSource.None) : Credentials.Resolve(provider);

    public IChatClient CreateChatClient(AiSettings settings) =>
        IsDemo
            ? DemoScript.CreateClient()
            : ChatClientFactory.Create(settings.Provider, settings.ModelFor(settings.Provider), Credentials.Resolve(settings.Provider));
}
