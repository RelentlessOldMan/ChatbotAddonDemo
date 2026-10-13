namespace Demo.Mcp.Tests;

public class ClientConfigTests
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:47821/mcp");

    [Fact]
    public void Claude_Code_http_snippet_includes_transport_url_and_header() =>
        Assert.Equal(
            "claude mcp add --transport http demo-desktop http://127.0.0.1:47821/mcp --header \"Authorization: Bearer abc\"",
            McpClientConfig.ClaudeCodeHttp(Endpoint, "abc"));

    [Fact]
    public void Codex_http_snippet_keeps_token_out_of_config_toml()
    {
        var snippet = McpClientConfig.CodexHttp(Endpoint, "abc");
        var toml = snippet[snippet.IndexOf("[mcp_servers", StringComparison.Ordinal)..];
        Assert.DoesNotContain("abc", toml);
        Assert.Contains("bearer_token_env_var = \"DEMO_DESKTOP_MCP_TOKEN\"", toml);
        Assert.Contains("setx DEMO_DESKTOP_MCP_TOKEN abc", snippet);
    }

    [Fact]
    public void Stdio_snippets_pass_allow_writes_only_when_asked()
    {
        Assert.EndsWith("mcp", McpClientConfig.ClaudeCodeStdio(@"C:\t\demo-cli.exe", allowWrites: false));
        Assert.EndsWith("mcp --allow-writes", McpClientConfig.ClaudeCodeStdio(@"C:\t\demo-cli.exe", allowWrites: true));
        Assert.Contains("args = [\"mcp\", \"--allow-writes\"]", McpClientConfig.CodexStdio(@"C:\t\demo-cli.exe", allowWrites: true));
    }
}
