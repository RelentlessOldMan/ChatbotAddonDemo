namespace Demo.Mcp.Tests;

/// <summary>Which outside servers the chat may use: the user's choices, IT's, and the rules between them.</summary>
public sealed class OutsideServerTests
{
    private static OutsideServer It(string name, bool trusted = false) => new(name, $"{name}.exe", trusted, ServerOrigin.Policy);
    private static OutsideServer Mine(string name, bool trusted = false) => new(name, $"my-{name}.exe", trusted, ServerOrigin.User);

    [Fact]
    public void IT_servers_come_first_and_the_users_follow()
    {
        var servers = OutsideServer.Combine([It("perforce")], [Mine("github")], userServersAllowed: true);

        Assert.Equal(["perforce", "github"], servers.Select(s => s.Name));
    }

    [Fact]
    public void Only_IT_can_mark_a_server_safe()
    {
        var servers = OutsideServer.Combine([It("perforce", trusted: true)], [Mine("github", trusted: true)], userServersAllowed: true);

        Assert.True(servers.Single(s => s.Name == "perforce").Trusted);
        Assert.False(servers.Single(s => s.Name == "github").Trusted);
    }

    [Fact]
    public void A_users_server_cant_replace_one_IT_set_up()
    {
        var servers = OutsideServer.Combine([It("github")], [Mine("GitHub")], userServersAllowed: true);

        Assert.Equal("github.exe", Assert.Single(servers).Target);
    }

    [Fact]
    public void IT_can_limit_the_list_to_its_own_servers()
    {
        var servers = OutsideServer.Combine([It("perforce")], [Mine("github")], userServersAllowed: false);

        Assert.Equal(ServerOrigin.Policy, Assert.Single(servers).Origin);
    }

    [Theory]
    [InlineData("github", "npx -y @modelcontextprotocol/server-github", null)]
    [InlineData("m365", "https://mcp.example.com/m365", null)]
    [InlineData("local", "http://localhost:8080/mcp", null)]
    [InlineData("remote", "http://mcp.example.com/mcp", "doesn't use https")]
    [InlineData("has space", "x.exe", "isn't a usable server name")]
    [InlineData("a-name-that-is-far-too-long", "x.exe", "isn't a usable server name")]
    [InlineData("empty", "  ", "has no command or address")]
    public void Each_entry_is_checked_before_it_is_used(string name, string target, string? problem)
    {
        var found = new OutsideServer(name, target, false, ServerOrigin.User).Problem();

        if (problem is null) Assert.Null(found);
        else Assert.Contains(problem, found);
    }

    [Theory]
    [InlineData("p4mcp.exe --port ssl:p4:1666", new[] { "p4mcp.exe", "--port", "ssl:p4:1666" })]
    [InlineData(@"""C:\Program Files\P4\p4mcp.exe"" --readonly", new[] { @"C:\Program Files\P4\p4mcp.exe", "--readonly" })]
    [InlineData("  tool   \"\"  x ", new[] { "tool", "", "x" })]
    public void Command_lines_split_like_Windows_does(string commandLine, string[] parts) =>
        Assert.Equal(parts, OutsideServer.SplitCommandLine(commandLine));

    [Theory]
    [InlineData("GitHub", "search_issues", "github_search_issues")]
    [InlineData("m365", "mail.send", "m365_mail_send")]
    public void Outside_tool_names_are_prefixed_with_the_server(string server, string tool, string expected) =>
        Assert.Equal(expected, McpHub.PrefixedName(Mine(server), tool));

    [Fact]
    public void Outside_tool_names_fit_every_providers_limit() =>
        Assert.Equal(64, McpHub.PrefixedName(Mine("github"), new string('x', 100)).Length);
}
