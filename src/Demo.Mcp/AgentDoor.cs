using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Demo.Capabilities;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace Demo.Mcp;

/// <summary>One tool call from a coding agent connected to the app.</summary>
public sealed record AgentCall(string Id, string Agent, Capability Capability, IReadOnlyDictionary<string, object?> Arguments);

/// <summary>The app's side of the agent door: shows what agents do, and asks the user before any change.</summary>
public interface IAgentSupervisor
{
    void AgentConnected();

    void AgentDisconnected();

    /// <summary>A change or bench action an agent wants. Nothing runs until this returns true; cancelled if the agent gives up.</summary>
    Task<bool> ApproveAsync(AgentCall call, CancellationToken cancellationToken);

    /// <summary>A call finished (a read that ran without asking, or a change after it was approved).</summary>
    void Ran(AgentCall call);
}

/// <summary>
/// Path D1: lets a coding agent work with the open app. The agent starts <c>demo-desktop.exe mcp</c>, which relays its
/// stdin/stdout to this named pipe (see <see cref="AgentRelay"/>). There is no network port: the pipe refuses
/// connections from other computers and from other Windows users. Reads run straight away; every change or bench
/// action waits for the user to approve it in the app, the same card the chat panel shows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentDoor : IAsyncDisposable
{
    /// <summary>One name per Windows user, so the relay finds the app that user has open.</summary>
    public static string DefaultPipeName => $"ChatbotAddonDemo.TestBench.{Environment.UserName}";

    /// <summary>What the agent is told when the user says no.</summary>
    public const string Declined = "The user declined this in TestBench, so nothing was changed. Don't try again unless they ask you to.";

    private readonly McpHub _hub;
    private readonly string _pipeName;
    private readonly IAgentSupervisor _supervisor;
    private readonly CancellationTokenSource _closing = new();
    private readonly List<Task> _sessions = [];
    private readonly Task _accepting;

    private AgentDoor(McpHub hub, string pipeName, IAgentSupervisor supervisor, NamedPipeServerStream first)
    {
        _hub = hub;
        _pipeName = pipeName;
        _supervisor = supervisor;
        _accepting = AcceptAsync(first);
    }

    /// <summary>Opens the door. Throws <see cref="IOException"/> when another TestBench window already has it open.</summary>
    internal static AgentDoor Open(McpHub hub, string pipeName, IAgentSupervisor supervisor)
    {
        NamedPipeServerStream first;
        try
        {
            first = CreatePipe(pipeName, first: true);
        }
        catch (UnauthorizedAccessException ex) // what Windows reports when the name is taken
        {
            throw new IOException("Another TestBench window already lets coding agents connect.", ex);
        }
        return new(hub, pipeName, supervisor, first);
    }

    private static NamedPipeServerStream CreatePipe(string name, bool first)
    {
        var security = new PipeSecurity();
        var me = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None), 0, 0, security);
    }

    private async Task AcceptAsync(NamedPipeServerStream pipe)
    {
        while (true)
        {
            try
            {
                await pipe.WaitForConnectionAsync(_closing.Token);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                return;
            }
            lock (_sessions)
            {
                _sessions.RemoveAll(s => s.IsCompleted);
                _sessions.Add(ServeAsync(pipe));
            }
            pipe = CreatePipe(_pipeName, first: false);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await Task.Yield(); // let the door go back to accepting the next agent
        _supervisor.AgentConnected();
        try
        {
            McpServer? server = null;
            var agent = () => server?.ClientInfo is { } info ? info.Title ?? info.Name : "A coding agent";
            var gated = new CapabilityCatalog(_hub.Catalog.Capabilities.Select(c => c with { Function = new ApprovalGate(c, _supervisor, agent) }));
            server = McpServer.Create(new StreamServerTransport(pipe, pipe), _hub.CreateServerOptions(McpToolAdapter.ToMcpTools(gated, McpExposurePolicy.All)));
            await using (server)
                await server.RunAsync(_closing.Token);
        }
        catch (Exception) when (_closing.IsCancellationRequested || !pipe.IsConnected)
        {
            // The app is closing, or the agent hung up mid-message: either way this conversation is over.
        }
        finally
        {
            await pipe.DisposeAsync();
            _supervisor.AgentDisconnected();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _closing.CancelAsync();
        await _accepting;
        Task[] sessions;
        lock (_sessions) sessions = [.. _sessions];
        await Task.WhenAll(sessions);
        _closing.Dispose();
    }

    /// <summary>Asks the user before a change runs; reports every call so the app can show what the agent did.</summary>
    private sealed class ApprovalGate(Capability capability, IAgentSupervisor supervisor, Func<string> agent) : DelegatingAIFunction(capability.Function)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var call = new AgentCall(Guid.NewGuid().ToString("N"), agent(), capability, new Dictionary<string, object?>(arguments));
            if (capability.Safety != SafetyClass.Read && !await supervisor.ApproveAsync(call, cancellationToken))
                return new CapabilityError(Declined);
            var result = await base.InvokeCoreAsync(arguments, cancellationToken);
            supervisor.Ran(call);
            return result;
        }
    }
}
