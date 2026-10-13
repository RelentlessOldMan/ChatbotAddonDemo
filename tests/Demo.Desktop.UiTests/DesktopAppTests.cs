using System.Net;
using System.Net.Sockets;
using Demo.Ai;
using Demo.Ai.Credentials;
using Demo.Screenshots;
using FlaUI.Core.AutomationElements;
using ModelContextProtocol.Client;

namespace Demo.Desktop.UiTests;

/// <summary>
/// Drives the real WPF app in --demo mode (scripted assistant, throwaway profile). The UI tests in
/// this class run one at a time because each one owns a desktop window.
/// </summary>
[Collection("Desktop")]
public sealed class DesktopAppTests : IDisposable
{
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "ChatbotAddonDemo-uitests", Guid.NewGuid().ToString("N"));

    private DesktopDriver Launch(AiSettings settings)
    {
        new AiSettingsStore(Path.Combine(_profile, "settings.json")).Save(settings);
        return DesktopDriver.Launch(DesktopExe, "--demo", "--profile", _profile);
    }

    [Fact]
    public void Without_opt_in_there_is_no_AI_anywhere()
    {
        using var d = Launch(new AiSettings());

        Assert.Null(d.Main.FindFirstDescendant(cf => cf.ByAutomationId("ChatPanel")));
        Assert.Null(d.Main.FindFirstDescendant(cf => cf.ByAutomationId("AssistantButton")));
        Assert.Equal("Agent access: off", d.Find("McpStatus").Name);
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
    public void Approved_change_is_applied_and_the_grid_updates()
    {
        using var d = Launch(new AiSettings { ChatEnabled = true });

        AskFirstQuestion(d);
        Assert.Equal("Normal", d.GridCell("OrdersGrid", "WO-1003", 4)); // nothing changes before approval
        d.Click("ApproveButton");

        d.WaitUntil(() => d.GridCell("OrdersGrid", "WO-1003", 4) == "High", "grid to show High");
        d.WaitUntil(() => d.FindAll("AssistantMessage").Length == 2, "final answer");
        Assert.Contains("$", d.Find("UsageSummary").Name);
    }

    [Fact]
    public void Denied_change_is_not_applied_and_the_assistant_acknowledges()
    {
        using var d = Launch(new AiSettings { ChatEnabled = true });

        AskFirstQuestion(d);
        d.Click("DenyButton");

        d.WaitUntil(() => d.FindAll("AssistantMessage").Length == 2, "final answer");
        Assert.Contains("left WO-1003 at Normal", d.FindAll("AssistantMessage")[1].AsTextBox().Text);
        Assert.Equal("Normal", d.GridCell("OrdersGrid", "WO-1003", 4));
    }

    [Fact]
    public async Task Agent_access_serves_live_app_state_and_shows_activity()
    {
        var port = FreePort();
        using var d = Launch(new AiSettings { McpEnabled = true, McpPort = port });
        d.WaitUntil(() => d.Find("McpStatus").Name.Contains("on (read-only)"), "MCP server to start");
        d.SelectRow("OrdersGrid", "WO-1006");

        var token = new DpapiCredentialStore(Path.Combine(_profile, "credentials")).Get("mcp-bearer-token")!;
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"http://127.0.0.1:{port}/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        }));
        var result = await client.CallToolAsync("get_current_view");

        Assert.Contains("WO-1006", string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(t => t.Text)));
        d.WaitUntil(() => d.Find("McpActivity").Name.Contains("get_current_view"), "status bar activity");
    }

    private static void AskFirstQuestion(DesktopDriver d)
    {
        d.Type("ChatInput", "Which work orders are overdue? Bump the Northwind sensor job to high priority.");
        d.Click("SendButton");
        d.Find("ApproveButton");
    }

    private static string DesktopExe
    {
        get
        {
            var configuration = AppContext.BaseDirectory.Contains(@"\Release\") ? "Release" : "Debug";
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ChatbotAddonDemo.sln"))) dir = dir.Parent;
            return Path.Combine(dir!.FullName, "src", "Demo.Desktop", "bin", configuration, "net10.0-windows", "Demo.Desktop.exe");
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        try { Directory.Delete(_profile, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
