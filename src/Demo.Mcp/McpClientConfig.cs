namespace Demo.Mcp;

/// <summary>Ready-to-paste snippets for connecting popular coding agents. Shown behind "Copy config" buttons.</summary>
public static class McpClientConfig
{
    public const string DesktopServerName = "demo-desktop";
    public const string CliServerName = "demo-cli";
    public const string TokenEnvironmentVariable = "DEMO_DESKTOP_MCP_TOKEN";

    public static string ClaudeCodeHttp(Uri endpoint, string token) =>
        $"claude mcp add --transport http {DesktopServerName} {endpoint} --header \"Authorization: Bearer {token}\"";

    /// <summary>Codex reads the token from an environment variable, which keeps it out of config.toml.</summary>
    public static string CodexHttp(Uri endpoint, string token) =>
        $"""
        # 1) Store the token for your user (once):
        setx {TokenEnvironmentVariable} {token}

        # 2) Add to %USERPROFILE%\.codex\config.toml:
        [mcp_servers.{DesktopServerName.Replace('-', '_')}]
        url = "{endpoint}"
        bearer_token_env_var = "{TokenEnvironmentVariable}"
        """;

    public static string ClaudeCodeStdio(string exePath, bool allowWrites) =>
        $"claude mcp add {CliServerName} -- \"{exePath}\" mcp{(allowWrites ? " --allow-writes" : "")}";

    public static string CodexStdio(string exePath, bool allowWrites) =>
        $"""
        [mcp_servers.{CliServerName.Replace('-', '_')}]
        command = '{exePath}'
        args = [{(allowWrites ? "\"mcp\", \"--allow-writes\"" : "\"mcp\"")}]
        """;
}
