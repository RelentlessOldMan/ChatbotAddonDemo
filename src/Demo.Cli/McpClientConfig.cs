namespace Demo.Cli;

/// <summary>Ready-to-paste snippets for connecting popular coding agents to <c>demo-cli mcp</c>.</summary>
public static class McpClientConfig
{
    public const string ServerName = "testbench";

    public static string ClaudeCode(string exePath, McpExposurePolicy policy) =>
        $"claude mcp add {ServerName} -- \"{exePath}\" {string.Join(' ', Arguments(policy))}";

    public static string Codex(string exePath, McpExposurePolicy policy) =>
        $"""
        [mcp_servers.{ServerName}]
        command = '{exePath}'
        args = [{string.Join(", ", Arguments(policy).Select(a => $"\"{a}\""))}]
        """;

    public static IEnumerable<string> Arguments(McpExposurePolicy policy)
    {
        yield return "mcp";
        if (policy.AllowWrites) yield return "--allow-writes";
        if (policy.AllowRuns) yield return "--allow-runs";
    }
}
