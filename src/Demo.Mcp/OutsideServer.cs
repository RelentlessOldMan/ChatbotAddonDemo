using System.Text;
using ModelContextProtocol.Client;

namespace Demo.Mcp;

/// <summary>Who added an outside server: the user in Settings, or IT by policy.</summary>
public enum ServerOrigin { User, Policy }

/// <summary>
/// Another MCP server the hub connects the chat to, e.g. Perforce, GitHub or Microsoft 365.
/// <paramref name="Target"/> is either a command line that starts the server (talked to over its stdin/stdout)
/// or the https address of a remote one.
/// </summary>
public sealed record OutsideServer(string Name, string Target, bool Trusted, ServerOrigin Origin)
{
    /// <summary>Names prefix every tool from the server (<c>github_search_issues</c>), so they're kept short and plain.</summary>
    public const int MaxNameLength = 20;

    public bool IsRemote => Target.TrimStart().StartsWith("http://", StringComparison.OrdinalIgnoreCase) || Target.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What it is, without the details: the host of an address, or the program of a command line. A command line can
    /// carry a token as an argument, so this is what's shown to users, not the whole thing.
    /// </summary>
    public string Summary =>
        IsRemote ? (Uri.TryCreate(Target.Trim(), UriKind.Absolute, out var url) ? url.Host : Target.Trim())
        : SplitCommandLine(Target) is [var program, ..] ? Path.GetFileName(program) : "";

    /// <summary>Why this entry can't be used, or null when it can.</summary>
    public string? Problem()
    {
        if (Name.Length is 0 or > MaxNameLength || !Name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            return $"'{Name}' isn't a usable server name: use up to {MaxNameLength} letters, digits or dashes.";
        if (string.IsNullOrWhiteSpace(Target))
            return $"{Name} has no command or address.";
        if (IsRemote)
        {
            // The connection can carry the user's work and credentials, so plain http is only allowed to this PC.
            if (!Uri.TryCreate(Target.Trim(), UriKind.Absolute, out var url))
                return $"{Name}'s address isn't a valid address.";
            if (url.Scheme != Uri.UriSchemeHttps && !url.IsLoopback)
                return $"{Name}'s address doesn't use https, so it isn't used.";
        }
        else if (SplitCommandLine(Target).Count == 0)
        {
            return $"{Name} has no command.";
        }
        return null;
    }

    internal IClientTransport CreateTransport()
    {
        if (IsRemote)
            return new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(Target.Trim()), Name = Name });
        var parts = SplitCommandLine(Target);
        // A short shutdown: when the app closes, a server that doesn't exit by itself is ended before the app is gone.
        return new StdioClientTransport(new StdioClientTransportOptions { Name = Name, Command = parts[0], Arguments = parts.Skip(1).ToList(), ShutdownTimeout = TimeSpan.FromSeconds(2) });
    }

    /// <summary>
    /// Every server that may be used: IT's first, then the user's own unless IT has turned those off. A user's entry
    /// can't take an IT entry's name, and is never trusted: only IT can let a server's tools run without asking.
    /// </summary>
    public static IReadOnlyList<OutsideServer> Combine(IEnumerable<OutsideServer> policy, IEnumerable<OutsideServer> user, bool userServersAllowed)
    {
        var servers = policy.Select(s => s with { Origin = ServerOrigin.Policy }).ToList();
        if (userServersAllowed)
        {
            servers.AddRange(user
                .Where(u => !servers.Any(s => string.Equals(s.Name, u.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(u => u with { Trusted = false, Origin = ServerOrigin.User }));
        }
        return servers;
    }

    /// <summary>Splits like a Windows command line: spaces separate, double quotes group (<c>"C:\Program Files\x.exe" --flag</c>).</summary>
    internal static List<string> SplitCommandLine(string commandLine)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) parts.Add(current.ToString());
                current.Clear();
                any = false;
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }
        if (any) parts.Add(current.ToString());
        return parts;
    }
}
