using System.Diagnostics;
using System.Text;

namespace Demo.Cli.Tests;

public sealed record CliResult(int ExitCode, string Output, string Error);

/// <summary>Runs the real demo-cli.exe, as a script or a person would.</summary>
public static class CliProcess
{
    public static string Exe => Path.Combine(AppContext.BaseDirectory, "demo-cli.exe");

    /// <param name="workspace">
    /// Also set as DEMO_WORKSPACE, so a run that ends up without <c>--workspace</c> still never touches the user's real workspace.
    /// </param>
    public static CliResult Run(string workspace, params string[] arguments)
    {
        var info = new ProcessStartInfo(Exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        info.Environment["DEMO_BENCH_DELAY_MS"] = "0";
        info.Environment["DEMO_WORKSPACE"] = workspace;

        using var process = Process.Start(info)!;
        process.StandardInput.Close(); // `mcp` reads stdin; end of input makes it exit instead of waiting forever
        // Read both streams at once: reading one to the end first can deadlock when the other fills its pipe.
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"demo-cli {string.Join(' ', arguments)} did not exit within 60 s.");
        }
        return new CliResult(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }
}
