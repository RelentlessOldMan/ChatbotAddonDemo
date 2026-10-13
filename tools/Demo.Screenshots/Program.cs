using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Demo.Ai;
using Demo.Ai.Credentials;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Demo.Screenshots;

/// <summary>
/// Regenerates every image in docs/images by driving the real apps. Usage:
///   Demo.Screenshots --desktop path\Demo.Desktop.exe --cli path\demo-cli.exe --out docs\images
/// Normally run via scripts/capture-screenshots.ps1.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault()
            ?? throw new ArgumentException($"missing {name}");
        var desktopExe = Path.GetFullPath(Arg("--desktop"));
        var cliExe = Path.GetFullPath(Arg("--cli"));
        var outDir = Path.GetFullPath(Arg("--out"));

        // Async work runs on the thread pool; this thread stays STA for WPF rendering.
        Task.Run(() => CaptureDesktopAsync(desktopExe, outDir)).GetAwaiter().GetResult();
        CaptureConsole(cliExe, outDir);
        Task.Run(() => CaptureCliMcpAsync(cliExe, outDir)).GetAwaiter().GetResult();

        Console.WriteLine($"Screenshots written to {outDir}");
        return 0;
    }

    private static async Task CaptureDesktopAsync(string exe, string outDir)
    {
        var profile = Path.Combine(Path.GetTempPath(), "ChatbotAddonDemo-shots", Guid.NewGuid().ToString("N"));
        var port = FreePort();
        new AiSettingsStore(Path.Combine(profile, "settings.json")).Save(new AiSettings { McpPort = port });

        using var d = DesktopDriver.Launch(exe, "--demo", "--profile", profile);
        string Shot(string name) => Path.Combine(outDir, name);

        // 1. The legacy app as customers know it: no AI anywhere.
        d.SelectRow("OrdersGrid", "WO-1003");
        d.Capture(d.Main, Shot("01-legacy-app.png"));

        // 2. Opt in to the assistant.
        d.Click("SettingsButton");
        var settings = d.WaitForModal("SettingsWindow");
        d.SetChecked("ChatEnabledCheckBox", true, settings);
        d.Capture(settings, Shot("02-settings-ai-assistant.png"));

        // 3. Opt in to agent access (read-only).
        d.SelectTab("McpTab", settings);
        d.SetChecked("McpEnabledCheckBox", true, settings);
        d.Capture(settings, Shot("03-settings-agent-access.png"));
        d.Click("SaveSettingsButton", settings);

        // 4. Ask a question that needs a read and a write: the write waits for approval.
        d.Type("ChatInput", "Which work orders are overdue? Bump the Northwind sensor job to high priority.");
        d.Click("SendButton");
        d.Find("ApproveButton");
        d.Capture(d.Main, Shot("04-chat-approval.png"));

        // 5. Approve; the change goes through the same business logic as the UI and the grid updates.
        d.Click("ApproveButton");
        d.WaitUntil(() => d.FindAll("AssistantMessage").Length >= 2, "final answer");
        d.WaitUntil(() => d.GridCell("OrdersGrid", "WO-1003", 4) == "High", "grid refresh");
        d.Capture(d.Main, Shot("05-chat-result.png"));

        // 6. A coding agent connects over MCP; the status bar shows its activity.
        var token = new DpapiCredentialStore(Path.Combine(profile, "credentials")).Get("mcp-bearer-token")
            ?? throw new InvalidOperationException("desktop app did not create an MCP token");
        var transcript = new StringBuilder();
        await using (var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"http://127.0.0.1:{port}/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        })))
        {
            await DescribeSessionAsync(client, transcript, $"http://127.0.0.1:{port}/mcp  (Authorization: Bearer {token[..4]}…)",
                "get_current_view", new Dictionary<string, object?>());
        }
        d.WaitUntil(() => d.Find("McpActivity").Name.Contains("get_current_view"), "status bar activity");
        d.Capture(d.Main, Shot("06-agent-connected.png"));
        RunOnSta(() => TerminalImage.Save("MCP client → Workshop Manager (Streamable HTTP)", transcript.ToString(), Shot("07-mcp-http-session.png")));

        TryDelete(profile);
    }

    private static void CaptureConsole(string cliExe, string outDir)
    {
        var data = Path.Combine(Path.GetTempPath(), $"shots-{Guid.NewGuid():N}.json");
        var text = new StringBuilder();
        foreach (var command in new[] { "overdue", "parts --low", "adjust FLT-031 -5 used on WO-1006" })
        {
            text.AppendLine($"> demo-cli {command}");
            text.AppendLine(Run(cliExe, $"--data \"{data}\" {command}"));
        }
        TerminalImage.Save("demo-cli: the legacy console tool, unchanged", text.ToString(), Path.Combine(outDir, "08-console-tool.png"));
        File.Delete(data);
    }

    private static async Task CaptureCliMcpAsync(string cliExe, string outDir)
    {
        var data = Path.Combine(Path.GetTempPath(), $"shots-{Guid.NewGuid():N}.json");
        var transcript = new StringBuilder();
        await using (var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "demo-cli",
            Command = cliExe,
            Arguments = ["--data", data, "mcp"],
        })))
        {
            await DescribeSessionAsync(client, transcript, $"spawned: demo-cli mcp  (stdio, read-only)", "list_low_stock_parts", new Dictionary<string, object?>());
        }
        RunOnSta(() => TerminalImage.Save("MCP client → demo-cli (stdio)", transcript.ToString(), Path.Combine(outDir, "09-mcp-stdio-session.png")));
        File.Delete(data);
    }

    /// <summary>Writes a readable transcript of what an agent sees: server info, tools with hints, one call.</summary>
    private static async Task DescribeSessionAsync(McpClient client, StringBuilder text, string connection, string tool, Dictionary<string, object?> args)
    {
        text.AppendLine($"connect  {connection}");
        text.AppendLine($"server   {client.ServerInfo.Name} {client.ServerInfo.Version}");
        text.AppendLine();
        text.AppendLine("tools/list");
        foreach (var t in await client.ListToolsAsync())
        {
            var hint = t.ProtocolTool.Annotations?.DestructiveHint == true ? "writes " : "read   ";
            text.AppendLine($"  {hint} {t.Name,-26} {t.ProtocolTool.Title}");
        }
        text.AppendLine();
        text.AppendLine($"tools/call {tool} {JsonSerializer.Serialize(args)}");
        var result = await client.CallToolAsync(tool, args);
        foreach (var line in string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text)).Split('\n').Take(16))
            text.AppendLine("  " + line.TrimEnd('\r'));
    }

    private static string Run(string exe, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(exe, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return output;
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

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }
}
