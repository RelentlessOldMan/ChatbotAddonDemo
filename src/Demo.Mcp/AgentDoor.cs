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
    /// <summary>
    /// One name per Windows account, so the relay finds the app that account has open. The account's SID, not its user
    /// name: another account on a shared PC can't take the name first, and two accounts with the same name don't clash.
    /// </summary>
    public static string DefaultPipeName => $"ChatbotAddonDemo.TestBench.{WindowsIdentity.GetCurrent().User!.Value}";

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
        // The relay only connects to a pipe its own user owns (AgentRelay.ConnectToAppAsync). Without this, an app run
        // as administrator would be owned by the Administrators group, and a relay that isn't would refuse it.
        security.SetOwner(me);
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
            catch (IOException)
            {
                // An agent connected and hung up before it was accepted: wait for the next one on a fresh pipe.
                await pipe.DisposeAsync();
                pipe = CreatePipe(_pipeName, first: false);
                continue;
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
        // Ends when the app closes the door or the agent hangs up. The MCP server waits for running calls before it
        // notices a hang-up, so this is what withdraws an approval card the agent is no longer waiting for.
        using var session = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        try
        {
            McpServer? server = null;
            var agent = () => server?.ClientInfo is { } info ? info.Title ?? info.Name : "A coding agent";
            var gated = new CapabilityCatalog(_hub.Catalog.Capabilities.Select(c => c with { Function = new ApprovalGate(c, _supervisor, agent, session.Token) }));
            var stream = new HangUpStream(pipe, session);
            server = McpServer.Create(new StreamServerTransport(stream, stream), _hub.CreateServerOptions(McpToolAdapter.ToMcpTools(gated, McpExposurePolicy.All)));
            await using (server)
                await server.RunAsync(session.Token);
        }
        catch (Exception) when (session.IsCancellationRequested || !pipe.IsConnected)
        {
            // The app is closing, or the agent hung up: either way this conversation is over.
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
        try { await _accepting; }
        catch (Exception) { } // the door stopped accepting earlier; the conversations below still need ending
        Task[] sessions;
        lock (_sessions) sessions = [.. _sessions];
        await Task.WhenAll(sessions);
        _closing.Dispose();
    }

    /// <summary>Asks the user before a change runs; reports every call so the app can show what the agent did.</summary>
    private sealed class ApprovalGate(Capability capability, IAgentSupervisor supervisor, Func<string> agent, CancellationToken session) : DelegatingAIFunction(capability.Function)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var call = new AgentCall(Guid.NewGuid().ToString("N"), agent(), capability, new Dictionary<string, object?>(arguments));
            if (capability.Safety != SafetyClass.Read)
            {
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session);
                if (!await supervisor.ApproveAsync(call, waiting.Token)) return new CapabilityError(Declined);
                // Approved just as the agent hung up: nobody is waiting for the result, so don't make the change.
                session.ThrowIfCancellationRequested();
            }
            var result = await base.InvokeCoreAsync(arguments, cancellationToken);
            supervisor.Ran(call);
            return result;
        }
    }

    /// <summary>The pipe, as the MCP server reads it, ending the session as soon as the agent hangs up.</summary>
    private sealed class HangUpStream(Stream inner, CancellationTokenSource session) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                var read = await inner.ReadAsync(buffer, cancellationToken);
                if (read == 0 && buffer.Length > 0) HungUp();
                return read;
            }
            catch (IOException)
            {
                HungUp();
                throw;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        private void HungUp()
        {
            try { session.Cancel(); }
            catch (ObjectDisposedException) { } // the session already ended
        }
    }
}
