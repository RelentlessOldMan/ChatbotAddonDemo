using Demo.Domain;

namespace Demo.Cli;

/// <summary>
/// When the process is about to exit (Ctrl+C, or the coding agent closing the MCP connection), a run it
/// started is cancelled so it ends as Cancelled, rather than left "Running" until it goes stale.
/// </summary>
public static class RunShutdown
{
    /// <summary>How long to wait for the bench to stop and the run to be saved as Cancelled.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);

    public static async Task CancelActiveRunAsync(TestBenchService bench, TimeSpan timeout)
    {
        try
        {
            if (bench.BenchBusyWith() is { Status: RunStatus.Running } run)
                await bench.CancelRunAsync(run.Id).WaitAsync(timeout);
        }
        catch (DomainException) { }  // finished meanwhile, or running in another program (not ours to stop)
        catch (TimeoutException) { } // exit anyway; the run will show as Interrupted
        catch (IOException) { }
    }
}
