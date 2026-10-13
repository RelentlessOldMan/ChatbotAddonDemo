using System.Diagnostics;
using Demo.Ai;
using Demo.Screenshots;
using FlaUI.Core.AutomationElements;

namespace Demo.Desktop.UiTests;

/// <summary>
/// Drives the real WPF app in --demo mode (scripted assistant, throwaway profile and workspace).
/// The tests in this class run one at a time because each one owns a desktop window.
/// </summary>
[Collection("Desktop")]
public sealed class DesktopAppTests : IDisposable
{
    // Must match FirstQuestion in the app (the scripted assistant ignores the wording, but screenshots show it).
    private const string FirstQuestion =
        "Make a hot/cold plan for the LDO-3300 from its datasheet: -40, 25, 85 and 125 °C at 3.6 V and 5 V, 3 units. Then run it and tell me what you find.";

    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(40);
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "ChatbotAddonDemo-uitests", Guid.NewGuid().ToString("N"));

    private string Workspace => Path.Combine(_profile, "workspace");

    private DesktopDriver Launch(AiSettings settings)
    {
        new AiSettingsStore(Path.Combine(_profile, "settings.json")).Save(settings);
        return DesktopDriver.Launch(RepoFile(@"src\Demo.Desktop\bin\{0}\net10.0-windows\Demo.Desktop.exe"), "--demo", "--profile", _profile, "--workspace", Workspace);
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
        Assert.Equal("Completed", d.GridCell("RunsGrid", "R-1002", 2));
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
    public void Changes_made_by_the_console_tool_show_up_in_the_open_app()
    {
        using var d = Launch(new AiSettings());

        var cli = Process.Start(new ProcessStartInfo(RepoFile(@"src\Demo.Cli\bin\{0}\net10.0\demo-cli.exe"),
            $"--workspace \"{Workspace}\" plan from-spec LDO-3300_datasheet.csv --temps 25,125 --samples 2") { UseShellExecute = false, CreateNoWindow = true })!;
        cli.WaitForExit();

        d.WaitUntil(() => d.Find("PlansGrid").AsDataGridView().Rows.Any(r => r.Cells[0].Value == "TP-1002"), "the new plan to appear");
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
    }
}
