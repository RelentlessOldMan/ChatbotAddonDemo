using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Demo.Mcp;

/// <summary>
/// <c>demo-desktop.exe mcp</c>: what a coding agent is configured to start. It finds the TestBench window this user
/// has open and passes MCP messages both ways between the agent (stdin/stdout) and that app's <see cref="AgentDoor"/>.
/// It has no tools of its own: everything the agent does happens in the open app, where the user sees it.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AgentRelay
{
    public const string NotRunning =
        "TestBench isn't open, or it isn't letting coding agents connect. Open TestBench, turn on " +
        "\"Let coding agents connect\" in Settings, then reconnect (in Claude Code: /mcp).";

    /// <returns>The process exit code: 0 when the agent or the app ended the conversation, 1 when the app wasn't there.</returns>
    public static async Task<int> RunAsync(string pipeName, Stream agentInput, Stream agentOutput, TextWriter errors, TimeSpan connectTimeout, CancellationToken cancellationToken = default)
    {
        NamedPipeClientStream app;
        try
        {
            app = await ConnectToAppAsync(pipeName, connectTimeout, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            await errors.WriteLineAsync(NotRunning);
            return 1;
        }
        await using var _ = app;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var toApp = CopyAsync(agentInput, app, stop.Token);
        var toAgent = CopyAsync(app, agentOutput, stop.Token);
        // Whichever side hangs up first (the agent closes stdin, or the app closes) ends the conversation.
        await Task.WhenAny(toApp, toAgent);
        await stop.CancelAsync();
        if (toAgent.IsCompleted && !toApp.IsCompleted && !cancellationToken.IsCancellationRequested)
            await errors.WriteLineAsync("TestBench closed the connection (the app was closed, or coding agents were turned off).");
        return 0;
    }

    /// <summary>
    /// Connects to the open app's pipe, but only one this same Windows user created, never one another user set up
    /// under that name to listen in. Not <see cref="PipeOptions.CurrentUserOnly"/>: that compares the pipe's owner with
    /// the token's default owner, which for a program run as administrator is the Administrators group, so it would
    /// refuse whenever the app and the agent differ in elevation.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">Someone else owns the pipe.</exception>
    internal static async Task<NamedPipeClientStream> ConnectToAppAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var app = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await app.ConnectAsync(timeout, cancellationToken);
            using var me = WindowsIdentity.GetCurrent();
            var owner = app.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            if (owner != me.User && owner != me.Owner)
                throw new UnauthorizedAccessException("The TestBench pipe belongs to another Windows user.");
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static async Task CopyAsync(Stream from, Stream to, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                await to.FlushAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The other side went away; the caller ends the relay.
        }
    }
}
