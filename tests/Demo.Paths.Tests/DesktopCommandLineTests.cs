using System.Diagnostics;

namespace Demo.Paths.Tests;

/// <summary>The desktop exe as a coding agent starts it (path D1), given a command line it can't use.</summary>
public sealed class DesktopCommandLineTests
{
    [Theory]
    [InlineData("--agent-pipe")]
    [InlineData("--agent-pipe", "--demo")]
    public async Task An_option_without_its_value_is_refused_not_guessed(params string[] options)
    {
        // Guessing would connect the agent to the user's own app instead of the one it was pointed at.
        using var process = Process.Start(new ProcessStartInfo(Paths.DesktopExe, ["mcp", .. options])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(2, process.ExitCode);
        Assert.Equal("--agent-pipe needs a value.", (await stderr).Trim());
    }
}
