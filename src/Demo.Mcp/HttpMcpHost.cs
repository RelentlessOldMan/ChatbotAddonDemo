using System.Net;
using System.Security.Cryptography;
using System.Text;
using Demo.Capabilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Demo.Mcp;

public sealed record HttpMcpHostOptions(int Port, string BearerToken, McpExposurePolicy Policy, Implementation ServerInfo)
{
    public Uri Endpoint => new($"http://127.0.0.1:{Port}/mcp");
}

/// <summary>
/// MCP over Streamable HTTP, hosted inside the running desktop app so agents act on
/// the live session. Hardening (docs/adr/004-mcp-transports.md):
/// loopback-only bind, bearer token, Host/Origin checks against DNS rebinding.
/// </summary>
public sealed class HttpMcpHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private HttpMcpHost(WebApplication app, HttpMcpHostOptions options)
    {
        _app = app;
        Options = options;
    }

    public HttpMcpHostOptions Options { get; }

    /// <summary>Raised on a thread-pool thread whenever an agent calls a tool.</summary>
    public event Action<ToolInvocation>? ToolInvoked;

    public static async Task<HttpMcpHost> StartAsync(CapabilityCatalog catalog, HttpMcpHostOptions options, CancellationToken cancellationToken = default)
    {
        if (options.BearerToken.Length < 32)
            throw new ArgumentException("Bearer token must be at least 32 characters.", nameof(options));

        HttpMcpHost? host = null;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, options.Port));
        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = options.ServerInfo;
                o.ServerInstructions = ServerInstructions.For(options.Policy);
            })
            .WithHttpTransport(t => t.Stateless = true)
            .WithTools(McpToolAdapter.ToMcpTools(catalog, options.Policy, invocation => host?.ToolInvoked?.Invoke(invocation)));

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (Reject(context, options) is { } status)
            {
                context.Response.StatusCode = status;
                return;
            }
            await next(context);
        });
        app.MapMcp("/mcp");

        host = new HttpMcpHost(app, options);
        await app.StartAsync(cancellationToken);
        return host;
    }

    /// <summary>Returns an HTTP status to reject with, or null to allow.</summary>
    internal static int? Reject(HttpContext context, HttpMcpHostOptions options)
    {
        // DNS rebinding: a web page can resolve evil.example to 127.0.0.1, but its Host header still says evil.example.
        var host = context.Request.Host.Host;
        if (host is not ("127.0.0.1" or "localhost" or "[::1]")) return StatusCodes.Status403Forbidden;

        // Browsers send Origin on cross-site requests; agents don't. Only loopback origins are acceptable.
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !(Uri.TryCreate(origin, UriKind.Absolute, out var o) && o.IsLoopback))
            return StatusCodes.Status403Forbidden;

        var auth = context.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (!auth.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(auth[scheme.Length..].Trim()), Encoding.UTF8.GetBytes(options.BearerToken)))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return StatusCodes.Status401Unauthorized;
        }
        return null;
    }

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
