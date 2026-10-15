using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Demo.Paths.Tests;

public sealed record CliResult(int ExitCode, string Output, string Error);

/// <summary>Starts the real demo-cli.exe for path B (a person or script) and path D (a coding agent's MCP connection).</summary>
public static class Paths
{
    /// <summary>
    /// demo-cli.exe in its own build folder, as shipped. The copy next to the tests can't be used: this project
    /// also loads ASP.NET Core (for the fake gateway), so libraries demo-cli needs aren't copied here.
    /// </summary>
    public static string CliExe { get; } = FindCli();

    private static string FindCli()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)); // tests/Demo.Paths.Tests/bin/<config>/<tfm>
        var configuration = output.Parent!.Name;
        var repo = output.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        var exe = Path.Combine(repo, "src", "Demo.Cli", "bin", configuration, "net10.0", "demo-cli.exe");
        return File.Exists(exe) ? exe : throw new FileNotFoundException($"Build Demo.Cli first: {exe} is missing.", exe);
    }

    /// <summary>Path B: run one console command, as a person or a script would.</summary>
    public static CliResult Cli(string workspace, params string[] arguments)
    {
        var info = new ProcessStartInfo(CliExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add("--workspace");
        info.ArgumentList.Add(workspace);
        foreach (var a in arguments) info.ArgumentList.Add(a);
        info.Environment["DEMO_BENCH_DELAY_MS"] = "0";

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"demo-cli {string.Join(' ', arguments)} did not exit within 60 s.");
        }
        return new CliResult(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }

    /// <summary>The MCP server command line a coding agent is configured with.</summary>
    public static string[] McpArguments(string workspace) => ["--workspace", workspace, "mcp", "--allow-writes", "--allow-runs"];

    /// <summary>Path D without a model: connect to `demo-cli mcp` exactly as a coding agent does.</summary>
    public static Task<McpClient> Mcp(string workspace, CancellationToken cancellationToken) =>
        McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "demo-cli",
            Command = CliExe,
            Arguments = McpArguments(workspace),
            EnvironmentVariables = new Dictionary<string, string?> { ["DEMO_BENCH_DELAY_MS"] = "0" },
        }), cancellationToken: cancellationToken);

    /// <summary>Calls an MCP tool and returns its JSON result; fails the test if the tool reported an error.</summary>
    public static async Task<JsonElement> Call(this McpClient client, string tool, Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: cancellationToken);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.False(result.IsError == true, $"{tool} failed: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
