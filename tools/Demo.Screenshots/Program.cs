using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Demo.Ai;
using FlaUI.Core.AutomationElements;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Demo.Screenshots;

/// <summary>
/// Regenerates every image in docs/images by driving the real apps. Usage:
///   Demo.Screenshots --desktop path\demo-desktop.exe --cli path\demo-cli.exe --out docs\images
/// Normally run via scripts/capture-screenshots.ps1.
/// </summary>
public static class Program
{
    private const string FirstQuestion =
        "Make a hot/cold plan for the LDO-3300 from its datasheet: -40, 25, 85 and 125 °C at 3.6 V and 5 V, 3 units. Then run it and tell me what you find.";
    private const string SecondQuestion = "Which units failed at 125 °C?";

    [STAThread]
    public static int Main(string[] args)
    {
        string Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault()
            ?? throw new ArgumentException($"missing {name}");
        var desktopExe = Path.GetFullPath(Arg("--desktop"));
        var cliExe = Path.GetFullPath(Arg("--cli"));
        var outDir = Path.GetFullPath(Arg("--out"));

        // Capture into a staging folder and replace the published images only when every capture worked,
        // so a failure halfway leaves docs/images as it was rather than with images missing.
        var staging = Path.Combine(Path.GetTempPath(), "ChatbotAddonDemo-shots", "images-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            // Async work runs on the thread pool; this thread stays STA for WPF rendering.
            CaptureDesktop(desktopExe, staging);
            CaptureConsole(cliExe, staging);
            Task.Run(() => CaptureCliMcpAsync(cliExe, staging)).GetAwaiter().GetResult();

            Directory.CreateDirectory(outDir);
            foreach (var old in Directory.GetFiles(outDir, "*.png")) File.Delete(old);
            foreach (var shot in Directory.GetFiles(staging, "*.png")) File.Copy(shot, Path.Combine(outDir, Path.GetFileName(shot)));
        }
        finally
        {
            TryDelete(staging);
        }

        Console.WriteLine($"Screenshots written to {outDir}");
        return 0;
    }

    private static void CaptureDesktop(string exe, string outDir)
    {
        var profile = Path.Combine(Path.GetTempPath(), "ChatbotAddonDemo-shots", Guid.NewGuid().ToString("N"));
        // The app shows the workspace path on screen, so use a neutral one rather than a path under the user profile.
        var root = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "TestBench");
        var workspace = Path.Combine(root, "workspace");
        if (Directory.Exists(root))
            throw new InvalidOperationException($"{root} already exists; move it aside so the screenshots start from a fresh example workspace.");
        try
        {
            new AiSettingsStore(Path.Combine(profile, "settings.json")).Save(new AiSettings());
            CaptureDesktop(exe, profile, workspace, outDir);
        }
        finally
        {
            // Also after a failure, or the next run stops at the check above.
            TryDelete(profile);
            TryDelete(root); // created by this run (checked above)
        }
    }

    private static void CaptureDesktop(string exe, string profile, string workspace, string outDir)
    {
        using var d = DesktopDriver.Launch(exe, "--demo", "--profile", profile, "--workspace", workspace);
        string Shot(string name) => Path.Combine(outDir, name);
        var runTimeout = TimeSpan.FromSeconds(40);

        // 1-3. The legacy app as customers know it: plans, results, spec sheets. No AI anywhere.
        d.SelectRow("PlansGrid", "TP-1001");
        d.Capture(d.Main, Shot("01-test-plans.png"));
        d.SelectTab("RunsTab");
        d.SelectRow("RunsGrid", "R-1001");
        d.WaitUntil(() => d.Find("RunVerdict").Name == "PASS", "seed run verdict");
        d.Capture(d.Main, Shot("02-run-results.png"));
        d.SelectTab("SpecsTab");
        d.SelectRow("SpecsGrid", "LDO-3300_datasheet.csv");
        d.Capture(d.Main, Shot("03-spec-sheets.png"));
        d.SelectTab("PlansTab");

        // 4. Opt in to the assistant.
        d.Click("SettingsButton");
        var settings = d.WaitForModal("SettingsWindow");
        d.SetChecked("ChatEnabledCheckBox", true, settings);
        d.Capture(settings, Shot("04-settings-ai-assistant.png"));
        d.Click("SaveSettingsButton", settings);

        // 5. Ask for a plan from the datasheet: creating the file waits for approval.
        d.Type("ChatInput", FirstQuestion);
        d.Click("SendButton");
        d.Find("ApproveButton");
        d.Capture(d.Main, Shot("05-chat-plan-approval.png"));
        d.Click("ApproveButton");

        // 6. The plan is saved (it appears in the grid); using the bench needs a second approval.
        d.WaitUntil(() => d.FindAll("ApprovalResult").Length == 1 && d.FindAll("ApproveButton").Length == 1, "bench approval");
        d.SelectRow("PlansGrid", "TP-1002");
        d.Capture(d.Main, Shot("06-chat-run-approval.png"));
        d.Click("ApproveButton");

        // 7. The run executes on the bench; the app switches to it and shows the findings and plots.
        d.WaitUntil(() => d.FindAll("AssistantMessage").Any(m => m.AsTextBox().Text.Contains("Key findings")), "findings", runTimeout);
        d.WaitUntil(() => d.Find("RunVerdict").Name == "FAIL", "verdict");
        d.Capture(d.Main, Shot("07-chat-findings.png"));

        // 8. A follow-up question about specific conditions.
        d.Type("ChatInput", SecondQuestion);
        d.Click("SendButton");
        d.WaitUntil(() => d.FindAll("AssistantMessage").Any(m => m.AsTextBox().Text.Contains("failed at 125")), "follow-up answer");
        d.Capture(d.Main, Shot("08-chat-follow-up.png"));

        // 9. One of the real output files, as the bench wrote it.
        File.Copy(Path.Combine(workspace, "runs", "R-1002", "plots", "IQ.png"), Shot("09-output-plot-iq.png"), overwrite: true);
    }

    private static void CaptureConsole(string cliExe, string outDir)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "ChatbotAddonDemo-shots", Guid.NewGuid().ToString("N"));
        var text = new StringBuilder();
        try
        {
            foreach (var command in new[]
            {
                "plans",
                "plan from-spec LDO-3300_rev-B_targets.json --temps -40,25,85,125 --supplies 3.6,5 --samples 3",
                "run TP-1002",
            })
            {
                text.AppendLine($"> demo-cli {command}");
                text.AppendLine(Run(cliExe, $"--workspace \"{workspace}\" {command}"));
            }
        }
        finally
        {
            TryDelete(workspace);
        }
        RunOnSta(() => TerminalImage.Save("demo-cli: the console tool, no AI involved", text.ToString(), Path.Combine(outDir, "10-console-tool.png"), width: 1180));
    }

    private static async Task CaptureCliMcpAsync(string cliExe, string outDir)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "ChatbotAddonDemo-shots", Guid.NewGuid().ToString("N"));
        var text = new StringBuilder();
        try
        {
            await using (var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "demo-cli",
                Command = cliExe,
                Arguments = ["--workspace", workspace, "mcp", "--allow-writes", "--allow-runs"],
                EnvironmentVariables = new Dictionary<string, string?> { ["DEMO_BENCH_DELAY_MS"] = "0" },
            })))
            {
                text.AppendLine("agent starts:  demo-cli mcp --allow-writes --allow-runs   (stdio; no port, no key)");
                text.AppendLine($"server:        {client.ServerInfo.Name} {client.ServerInfo.Version}");
                text.AppendLine();
                text.AppendLine("tools/list");
                foreach (var t in await client.ListToolsAsync())
                {
                    var a = t.ProtocolTool.Annotations;
                    var kind = a?.ReadOnlyHint == true ? "read " : a?.OpenWorldHint == true ? "bench" : "write";
                    text.AppendLine($"  [{kind}] {t.Name,-27} {t.ProtocolTool.Title}");
                }
                text.AppendLine();
    
                async Task<JsonElement> Call(string tool, Dictionary<string, object?> args)
                {
                    text.AppendLine($"tools/call {tool} {JsonSerializer.Serialize(args)}");
                    var result = await client.CallToolAsync(tool, args);
                    return JsonDocument.Parse(string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text))).RootElement;
                }
    
                var plan = await Call("create_test_plan_from_spec", new() { ["specFile"] = "LDO-3300_datasheet.csv", ["samples"] = 3, ["temperaturesC"] = new[] { -40, 25, 85, 125 }, ["supplyVoltages"] = new[] { 3.6, 5.0 } });
                var planId = plan.GetProperty("plan").GetProperty("id").GetString();
                text.AppendLine($"  → plan {planId}, {plan.GetProperty("plan").GetProperty("measurementCount")} measurements, saved to {Path.GetFileName(plan.GetProperty("xmlPath").GetString())}");
                var run = await Call("start_test_run", new() { ["planId"] = planId });
                var runId = run.GetProperty("run").GetProperty("id").GetString();
                text.AppendLine($"  → run {runId} started");
                var done = await Call("wait_for_test_run", new() { ["runId"] = runId });
                text.AppendLine($"  → {done.GetProperty("status")}, {done.GetProperty("completed")} measurements, {done.GetProperty("failed")} failed");
                var summary = await Call("get_run_summary", new() { ["runId"] = runId });
                var s = summary.GetProperty("summary");
                text.AppendLine($"  → verdict {s.GetProperty("verdict")}; findings:");
                foreach (var f in s.GetProperty("findings").EnumerateArray().Take(3))
                    text.AppendLine($"      {f.GetProperty("severity")}: {f.GetProperty("text")}");
                text.AppendLine($"  → files: {string.Join(", ", new[] { "results.csv", "summary.csv" }.Concat(summary.GetProperty("files").GetProperty("plots").EnumerateArray().Select(p => "plots/" + Path.GetFileName(p.GetString()))))}");
            }
        }
        finally
        {
            TryDelete(workspace);
        }
        RunOnSta(() => TerminalImage.Save("A coding agent's view: MCP session with demo-cli (results shortened)", text.ToString(), Path.Combine(outDir, "11-mcp-session.png"), width: 1180));
    }

    private static string Run(string exe, string arguments)
    {
        var info = new ProcessStartInfo(exe, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.Environment["DEMO_BENCH_DELAY_MS"] = "0";
        using var process = Process.Start(info)!;
        // Read both streams at once: reading one to the end first can deadlock when the other fills its pipe.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"demo-cli {arguments} did not exit within 60 s.");
        }
        var output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new InvalidOperationException($"demo-cli {arguments} failed ({process.ExitCode}): {output}");
        // Long temp paths make the image unreadable; show the workspace as a short placeholder.
        var workspace = arguments.Split('"')[1];
        return output.Replace(workspace, @"%LOCALAPPDATA%\ChatbotAddonDemo\workspace");
    }

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw error;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
