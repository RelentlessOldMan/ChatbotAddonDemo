namespace Demo.Cli.Tests;

public class ClientConfigTests
{
    private const string Exe = @"C:\Tools\demo-cli.exe";

    [Fact]
    public void Claude_Code_snippet_starts_the_console_tool_in_mcp_mode() =>
        Assert.Equal("claude mcp add testbench -- \"C:\\Tools\\demo-cli.exe\" mcp", McpClientConfig.ClaudeCode(Exe, McpExposurePolicy.ReadOnly));

    [Fact]
    public void Permissions_become_flags_only_when_granted()
    {
        Assert.EndsWith("mcp --allow-writes --allow-runs", McpClientConfig.ClaudeCode(Exe, new McpExposurePolicy(true, true)));
        Assert.Contains("args = [\"mcp\", \"--allow-runs\"]", McpClientConfig.Codex(Exe, new McpExposurePolicy(false, true)));
        Assert.Contains("command = 'C:\\Tools\\demo-cli.exe'", McpClientConfig.Codex(Exe, McpExposurePolicy.ReadOnly));
    }
}
