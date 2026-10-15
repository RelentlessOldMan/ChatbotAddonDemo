using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Demo.Ai;
using Demo.Ai.Credentials;
using Demo.Capabilities;
using Demo.Capabilities.TestBench;
using Demo.Domain;

namespace Demo.Paths.Tests;

/// <summary>
/// 💲 The same job with real models: path C with real Claude and OpenAI, and path D2 (demo-cli mcp) with real Claude Code and Codex.
/// Every test spends real money (a few cents each), so all are skipped unless DEMO_LIVE_TESTS=1 is set. Path C
/// needs ANTHROPIC_API_KEY / OPENAI_API_KEY; path D2 needs the agent installed and signed in, and bills its user.
/// Each result is compared with the same job done on the command line, which costs nothing.
/// Run: $env:DEMO_LIVE_TESTS='1'; dotnet test tests/Demo.Paths.Tests --filter Category=Live
/// </summary>
[Trait("Category", "Live")]
public sealed class LivePathTests : IDisposable
{
    private const decimal ChatLimitUsd = 0.50m;
    private const string AgentBudgetUsd = "0.50";
    private static readonly TimeSpan AgentTimeout = TimeSpan.FromMinutes(5);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "paths-live", Guid.NewGuid().ToString("N"));
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipUnlessLive() =>
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DEMO_LIVE_TESTS") == "1", "set DEMO_LIVE_TESTS=1 to run paid tests");

    private static void Log(string text) => TestContext.Current.TestOutputHelper?.WriteLine(text);

    /// <summary>The job done for free on path B, to compare a model's work with.</summary>
    private RunOutcome Expected()
    {
        var workspace = Path.Combine(_root, "expected");
        var plan = Paths.Cli(workspace, "plan", "from-spec", Scenario.SpecFile,
            "--temps", string.Join(',', Scenario.Temperatures.Select(t => t.ToString(CultureInfo.InvariantCulture))),
            "--supplies", string.Join(',', Scenario.Supplies.Select(v => v.ToString(CultureInfo.InvariantCulture))),
            "--samples", Scenario.Units.ToString(CultureInfo.InvariantCulture));
        var run = Paths.Cli(workspace, "run", Scenario.PlanId().Match(plan.Output).Value);
        return RunOutcome.Read(workspace, Scenario.RunId().Match(run.Output).Value);
    }

    /// <summary>The run the model made: the one run besides the example R-1001.</summary>
    private static RunOutcome Made(string workspace)
    {
        var runs = new TestBenchService(workspace, BenchOptions.Instant).ListRuns().Where(r => r.Id != "R-1001").ToList();
        Assert.True(runs.Count == 1, $"Expected the model to start exactly one run, but found {runs.Count}.");
        return RunOutcome.Read(workspace, runs[0].Id);
    }

    [Theory]
    [InlineData(AiProvider.Anthropic)]
    [InlineData(AiProvider.OpenAI)]
    public async Task Path_C_the_chat_does_the_job_with_a_real_model(AiProvider provider)
    {
        SkipUnlessLive();
        var info = ProviderInfo.For(provider);
        var key = Environment.GetEnvironmentVariable(info.ApiKeyEnvironmentVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(key), $"{info.ApiKeyEnvironmentVariable} not set");

        var workspace = Path.Combine(_root, "c");
        using var client = ChatClientFactory.Create(provider, info.DefaultModel, new ResolvedCredential(CredentialSource.EnvironmentVariable, key));
        var meter = new UsageMeter(info.DefaultModel, ChatLimitUsd, tokenLimitWhenUnpriced: 200_000);
        await using var tools = await Paths.DesktopHub(workspace).ConnectChatAsync([], TestContext.Current.CancellationToken); // through the hub, as in the app
        var session = new ChatSession(client, tools.Catalog, meter, ChatSessionOptions.For(TestBenchCapabilities.Instructions));
        var asked = new List<string>();

        var turn = await session.SendAsync(Scenario.Question, (request, _) => { asked.Add(request.Capability.Name); return Task.FromResult(true); }, Ct);
        Log($"{info.DisplayName} {info.DefaultModel}: {meter.Summary}\n{turn.Text}");

        Assert.Contains(asked, name => name.StartsWith("create_test_plan", StringComparison.Ordinal));
        Assert.Contains("start_test_run", asked);
        Assert.Equal(Expected(), Made(workspace));
        Assert.Contains(new TestBenchService(workspace, BenchOptions.Instant).ListRuns().Last().Id, turn.Text); // the answer names the run
    }

    /// <summary>
    /// The conversation continues after an approved change: the second request carries the first turn's thinking,
    /// tool calls and approval, and the real API must accept it (a scripted model can't check that).
    /// </summary>
    [Fact]
    public async Task Path_C_a_follow_up_question_after_an_approval_is_answered_from_the_conversation()
    {
        SkipUnlessLive();
        var info = ProviderInfo.For(AiProvider.Anthropic);
        var key = Environment.GetEnvironmentVariable(info.ApiKeyEnvironmentVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(key), $"{info.ApiKeyEnvironmentVariable} not set");

        var workspace = Path.Combine(_root, "c-follow-up");
        using var client = ChatClientFactory.Create(AiProvider.Anthropic, info.DefaultModel, new ResolvedCredential(CredentialSource.EnvironmentVariable, key));
        var meter = new UsageMeter(info.DefaultModel, ChatLimitUsd);
        await using var tools = await Paths.DesktopHub(workspace).ConnectChatAsync([], TestContext.Current.CancellationToken); // through the hub, as in the app
        var session = new ChatSession(client, tools.Catalog, meter, ChatSessionOptions.For(TestBenchCapabilities.Instructions));

        var first = await session.SendAsync("Create a test plan from LDO-3300_datasheet.csv: 25 °C only, 5 V supply only, 1 unit, IQ only. Don't run it.",
            (_, _) => Task.FromResult(true), Ct);
        var created = new TestBenchService(workspace, BenchOptions.Instant).ListPlans().Where(p => p.Id != "TP-1001").ToList();
        Assert.True(created.Count == 1, $"Expected one new plan, found {created.Count}: {first.Text}");

        var second = await session.SendAsync("What is the ID of the plan you just created? Reply with just the ID.", (_, _) => Task.FromResult(false), Ct);
        Log($"{info.DisplayName} {info.DefaultModel}: {meter.Summary}\n{first.Text}\n---\n{second.Text}");

        Assert.Contains(created[0].Id, second.Text);
    }

    [Fact]
    public async Task Path_D2_Claude_Code_does_the_job_over_MCP()
    {
        SkipUnlessLive();
        var claude = Agent("DEMO_CLAUDE_EXE", "claude.exe", Path.Combine("node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"));
        Assert.SkipWhen(claude is null, "Claude Code is not installed");

        var workspace = Path.Combine(_root, "d-claude");
        var folder = Directory.CreateDirectory(Path.Combine(_root, "claude-home")).FullName;
        var config = Path.Combine(folder, "mcp.json");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
        {
            mcpServers = new
            {
                testbench = new { command = Paths.CliExe, args = Paths.McpArguments(workspace), env = new { DEMO_BENCH_DELAY_MS = "0" } },
            },
        }), Ct);

        // Only our MCP server, only its tools, a spending cap, and nothing saved to the user's session history.
        var output = await RunAgent(claude!, folder,
            "-p", AgentPrompt,
            "--mcp-config", config, "--strict-mcp-config",
            "--allowedTools", "mcp__testbench", "--permission-mode", "dontAsk",
            "--max-budget-usd", AgentBudgetUsd, "--model", "sonnet",
            "--no-session-persistence", "--output-format", "json");
        var result = JsonDocument.Parse(output).RootElement;
        Log($"Claude Code: ${result.GetProperty("total_cost_usd").GetDecimal():0.000}, {result.GetProperty("num_turns").GetInt32()} turns\n{result.GetProperty("result").GetString()}");

        Assert.Equal(Expected(), Made(workspace));
    }

    [Fact]
    public async Task Path_D2_Codex_does_the_job_over_MCP()
    {
        SkipUnlessLive();
        var codex = Agent("DEMO_CODEX_EXE", "codex.exe",
            Path.Combine("node_modules", "@openai", "codex", "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe"));
        Assert.SkipWhen(codex is null, "Codex is not installed");

        var workspace = Path.Combine(_root, "d-codex");
        var folder = Directory.CreateDirectory(Path.Combine(_root, "codex-home")).FullName;
        static string Toml(string text) => $"'{text}'"; // a literal string: backslashes as they are
        var args = string.Join(", ", Paths.McpArguments(workspace).Select(Toml));

        // Our MCP server with its tools allowed, a read-only sandbox for anything else, nothing saved to history.
        var output = await RunAgent(codex!, folder,
            "exec", "--skip-git-repo-check", "--ephemeral", "--json", "-C", folder, "-s", "read-only",
            "-c", $"mcp_servers.testbench.command={Toml(Paths.CliExe)}",
            "-c", $"mcp_servers.testbench.args=[{args}]",
            "-c", "mcp_servers.testbench.env={DEMO_BENCH_DELAY_MS='0'}",
            "-c", "mcp_servers.testbench.default_tools_approval_mode='approve'",
            "-c", "approval_policy='never'",
            AgentPrompt);
        var usage = output.Split('\n').Where(l => l.Contains("\"usage\"", StringComparison.Ordinal)).LastOrDefault();
        Log($"Codex usage: {usage}");

        Assert.Equal(Expected(), Made(workspace));
    }

    private const string AgentPrompt =
        Scenario.Question + " Use only the testbench MCP tools; don't write any files yourself.";

    /// <summary>An agent's native exe: an override, or on PATH, or inside the npm package next to its npm shim.</summary>
    private static string? Agent(string overrideVariable, string exeName, string insideNpmPackage)
    {
        if (Environment.GetEnvironmentVariable(overrideVariable) is { Length: > 0 } configured) return configured;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(dir, exeName))) return Path.Combine(dir, exeName);
            if (File.Exists(Path.Combine(dir, insideNpmPackage))) return Path.Combine(dir, insideNpmPackage);
        }
        return null;
    }

    private async Task<string> RunAgent(string exe, string workingDirectory, params string[] arguments)
    {
        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments) info.ArgumentList.Add(a);

        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(AgentTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{Path.GetFileName(exe)} did not finish within {AgentTimeout.TotalMinutes} minutes.");
        }
        var stdout = await output;
        var stderr = await error;
        Assert.True(process.ExitCode == 0, $"{Path.GetFileName(exe)} exited with {process.ExitCode}.\n{stderr}\n{stdout}");
        return stdout;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
