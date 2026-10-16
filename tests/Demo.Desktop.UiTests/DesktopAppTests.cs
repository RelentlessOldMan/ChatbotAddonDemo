using System.Diagnostics;
using Demo.Ai;
using Demo.Screenshots;
using FlaUI.Core.AutomationElements;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Demo.Desktop.UiTests;

/// <summary>
/// Drives the real WPF app in --demo mode (scripted assistant, throwaway profile and workspace).
/// The tests in this class run one at a time because each one owns a desktop window.
/// </summary>
[Collection("Desktop")]
public sealed class DesktopAppTests : IDisposable
{
    // Any wording works: the scripted assistant ignores it. Kept in step with DemoScript.FirstQuestion and the screenshot tool for realism.
    private const string FirstQuestion =
        "Make a hot/cold plan for the LDO-3300 from its datasheet: -40, 25, 85 and 125 °C at 3.6 V and 5 V, 3 units. Then run it and tell me what you find.";

    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(40);
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "ChatbotAddonDemo-uitests", Guid.NewGuid().ToString("N"));

    // Each test's app lets agents in under its own name, so they never meet the user's app or each other.
    private readonly string _agentPipe = "ChatbotAddonDemo.UiTests." + Guid.NewGuid().ToString("N");

    private string Workspace => Path.Combine(_profile, "workspace");
    private static string DesktopExe => RepoFile(@"src\Demo.Desktop\bin\{0}\net10.0-windows\demo-desktop.exe");

    private DesktopDriver Launch(AiSettings settings)
    {
        new AiSettingsStore(Path.Combine(_profile, "settings.json")).Save(settings);
        return DesktopDriver.Launch(DesktopExe, "--demo", "--profile", _profile, "--workspace", Workspace, "--agent-pipe", _agentPipe);
    }

    [Fact]
    public void Without_opt_in_there_is_no_AI_anywhere()
    {
        using var d = Launch(new AiSettings());

        Assert.Null(d.Main.FindFirstDescendant(cf => cf.ByAutomationId("ChatPanel")));
        Assert.Null(d.Main.FindFirstDescendant(cf => cf.ByAutomationId("AssistantButton")));
        Assert.Equal("Bench idle", d.Find("BenchStatus").Name);
    }

    [Fact]
    public void Opting_in_through_settings_shows_the_assistant()
    {
        using var d = Launch(new AiSettings());

        d.Click("SettingsButton");
        var settings = d.WaitForModal("SettingsWindow");
        d.SetChecked("ChatEnabledCheckBox", true, settings);
        d.Click("SaveSettingsButton", settings);

        Assert.Contains("no key needed", d.Find("ChatConnection").Name);
        Assert.True(new AiSettingsStore(Path.Combine(_profile, "settings.json")).Load().ChatEnabled);
    }

    [Fact]
    public void Settings_let_agents_in_and_add_other_mcp_servers()
    {
        using var d = Launch(new AiSettings());

        d.Click("SettingsButton");
        var settings = d.WaitForModal("SettingsWindow");
        d.SetChecked("AgentsEnabledCheckBox", true, settings);
        d.Click("AddServerButton", settings);
        d.Type("ServerNameBox", "my github", settings);
        d.Type("ServerCommandBox", "http://example.com/mcp", settings);
        d.WaitUntil(() => d.Find("McpServersError", settings).Name.Contains("letters"), "the name to be refused");
        Assert.False(d.Find("SaveSettingsButton", settings).IsEnabled);
        d.Type("ServerNameBox", "github", settings);
        d.WaitUntil(() => d.Find("McpServersError", settings).Name.Contains("https"), "the plain-http address to be refused");
        d.Type("ServerCommandBox", "https://example.com/mcp", settings);
        d.WaitUntil(() => d.Find("SaveSettingsButton", settings).IsEnabled, "Save to be enabled");
        d.Click("SaveSettingsButton", settings);

        d.WaitUntil(() => d.Find("AgentStatus").Name == "Waiting for a coding agent", "the agent panel");
        var saved = new AiSettingsStore(Path.Combine(_profile, "settings.json")).Load();
        Assert.True(saved.AgentsEnabled);
        Assert.Equal([new McpServerSetting("github", "https://example.com/mcp")], saved.McpServers);
    }

    [Fact]
    public void A_second_window_says_agents_are_taken_and_can_try_again_once_the_first_closes()
    {
        var first = Launch(new AiSettings { AgentsEnabled = true });
        var firstOpen = true;
        first.WaitUntil(() => first.Find("AgentStatus").Name == "Waiting for a coding agent", "the first window to let agents in");
        var secondProfile = _profile + "-second";
        new AiSettingsStore(Path.Combine(secondProfile, "settings.json")).Save(new AiSettings { AgentsEnabled = true });
        try
        {
            using var second = DesktopDriver.Launch(DesktopExe, "--demo", "--profile", secondProfile, "--workspace", Path.Combine(secondProfile, "workspace"), "--agent-pipe", _agentPipe);
            second.WaitUntil(() => second.Find("AgentStatus").Name.StartsWith("Another TestBench window"), "the second window to say agents are taken");

            first.Dispose(); // the user closes the first window
            firstOpen = false;
            second.Click("AgentTryAgain");

            second.WaitUntil(() => second.Find("AgentStatus").Name == "Waiting for a coding agent", "the second window to let agents in");
        }
        finally
        {
            if (firstOpen) first.Dispose();
            try { Directory.Delete(secondProfile, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void The_legacy_UI_still_runs_plans_without_any_AI()
    {
        using var d = Launch(new AiSettings());

        d.SelectRow("PlansGrid", "TP-1001");
        d.Click("RunPlanButton");

        d.WaitUntil(() => d.GridCell("RunsGrid", "R-1002", 2) == "Completed", "run to complete", RunTimeout);
        d.WaitUntil(() => d.Find("RunVerdict").Name == "PASS", "verdict");
    }

    [Fact]
    public void Approved_plan_and_run_execute_and_the_findings_appear()
    {
        using var d = Launch(new AiSettings { ChatEnabled = true });

        Ask(d, FirstQuestion);
        d.Find("ApproveButton");
        Assert.Single(d.Find("PlansGrid").AsDataGridView().Rows); // nothing is created before approval
        d.Click("ApproveButton");

        // ApprovalResult ("✓ Approved by you") only exists once a card is decided, so this is the second, still-pending card.
        d.WaitUntil(() => d.FindAll("ApprovalResult").Length == 1 && d.FindAll("ApproveButton").Length == 1, "the bench approval card");
        Assert.Single(Directory.GetDirectories(Path.Combine(Workspace, "runs"))); // the bench isn't used before approval
        d.Click("ApproveButton");

        d.WaitUntil(() => d.FindAll("AssistantMessage").Any(m => m.AsTextBox().Text.Contains("Key findings")), "findings", RunTimeout);
        // The grid refreshes a moment after the bench reports a change, so it can lag the chat.
        d.WaitUntil(() => d.GridCell("RunsGrid", "R-1002", 2) == "Completed", "the run shown as Completed");
        d.WaitUntil(() => d.Find("RunVerdict").Name == "FAIL", "verdict");
        Assert.Contains("$", d.Find("UsageSummary").Name);
    }

    [Fact]
    public void Denied_plan_is_not_created_and_the_assistant_acknowledges()
    {
        using var d = Launch(new AiSettings { ChatEnabled = true });

        Ask(d, FirstQuestion);
        d.Click("DenyButton");

        d.WaitUntil(() => d.FindAll("AssistantMessage").Any(m => m.AsTextBox().Text.Contains("didn't create the plan")), "acknowledgement");
        Assert.Single(d.Find("PlansGrid").AsDataGridView().Rows);
        Assert.Empty(Directory.GetFiles(Path.Combine(Workspace, "plans"), "TP-1002.xml"));
    }

    [Fact]
    public async Task A_coding_agent_works_with_the_open_window_and_asks_there_first()
    {
        using var d = Launch(new AiSettings { AgentsEnabled = true });
        await using var agent = await ConnectAgentAsync();

        d.WaitUntil(() => d.Find("AgentStatus").Name == "1 coding agent connected", "the agent to show as connected");
        Assert.Null(d.Main.FindFirstDescendant(cf => cf.ByAutomationId("ChatPanel"))); // agents don't need the assistant turned on

        var plans = await agent.CallToolAsync("list_test_plans", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(true, plans.IsError); // reads don't ask

        var creating = agent.CallToolAsync("create_test_plan_from_spec", NewPlan, cancellationToken: TestContext.Current.CancellationToken).AsTask();
        var panel = d.Find("AgentPanel");
        d.Find("ApproveButton", panel);
        Assert.False(creating.IsCompleted);
        Assert.Single(d.Find("PlansGrid").AsDataGridView().Rows); // nothing is created before approval
        d.Click("ApproveButton", panel);

        var created = await creating.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.NotEqual(true, created.IsError);
        d.WaitUntil(() => d.Find("PlansGrid").AsDataGridView().Rows.Any(r => r.Cells[0].Value == "TP-1002"), "the agent's plan to appear");
    }

    [Fact]
    public async Task A_coding_agent_is_told_when_the_user_declines()
    {
        using var d = Launch(new AiSettings { AgentsEnabled = true });
        await using var agent = await ConnectAgentAsync();

        var creating = agent.CallToolAsync("create_test_plan_from_spec", NewPlan, cancellationToken: TestContext.Current.CancellationToken).AsTask();
        d.Click("DenyButton", d.Find("AgentPanel"));

        var result = await creating.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Contains("declined", string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text)));
        Assert.Empty(Directory.GetFiles(Path.Combine(Workspace, "plans"), "TP-1002.xml"));
    }

    [Fact]
    public void Without_opt_in_coding_agents_cannot_connect()
    {
        using var d = Launch(new AiSettings());

        Assert.Null(d.Main.FindFirstDescendant(cf => cf.ByAutomationId("AgentPanel")));
        var (exitCode, error) = RunRelay();
        Assert.Equal(1, exitCode);
        Assert.Contains("isn't letting coding agents connect", error);
    }

    private static readonly Dictionary<string, object?> NewPlan = new() { ["specFile"] = "LDO-3300_datasheet.csv", ["samples"] = 2 };

    /// <summary>Connects exactly as a coding agent does: it starts <c>demo-desktop.exe mcp</c>, which relays to the open window.</summary>
    private Task<McpClient> ConnectAgentAsync() =>
        McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "testbench",
            Command = DesktopExe,
            Arguments = ["mcp", "--agent-pipe", _agentPipe],
        }), new McpClientOptions { ClientInfo = new Implementation { Name = "Test agent", Version = "1" } },
            cancellationToken: TestContext.Current.CancellationToken);

    private (int ExitCode, string Error) RunRelay()
    {
        using var relay = Process.Start(new ProcessStartInfo(DesktopExe, $"mcp --agent-pipe {_agentPipe}")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true,
        })!;
        var error = relay.StandardError.ReadToEndAsync();
        if (!relay.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            relay.Kill(entireProcessTree: true);
            Assert.Fail("demo-desktop mcp did not exit within 30 s.");
        }
        return (relay.ExitCode, error.Result);
    }

    private static void Ask(DesktopDriver d, string question)
    {
        d.Type("ChatInput", question);
        d.Click("SendButton");
    }

    private static string RepoFile(string relativeFormat)
    {
        var configuration = AppContext.BaseDirectory.Contains(@"\Release\") ? "Release" : "Debug";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ChatbotAddonDemo.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, string.Format(relativeFormat, configuration));
    }

    public void Dispose()
    {
        try { Directory.Delete(_profile, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        // The shared parent too, once the last test has emptied it.
        try { Directory.Delete(Path.GetDirectoryName(_profile)!); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
