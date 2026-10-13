using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Demo.Mcp;
using ModelContextProtocol.Protocol;

namespace Demo.Desktop;

/// <summary>Starts/stops the in-app MCP server to match settings, and reports status for the status bar.</summary>
public sealed partial class McpController(AppServices services) : ObservableObject, IAsyncDisposable
{
    private const string TokenName = "mcp-bearer-token";
    private HttpMcpHost? _host;

    [ObservableProperty] private string _status = "Agent access: off";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _lastActivity;

    public Uri? Endpoint => _host?.Options.Endpoint;

    /// <summary>The per-install token agents must present. Created on first use, DPAPI-protected at rest.</summary>
    public string Token
    {
        get
        {
            var token = services.CredentialStore.Get(TokenName);
            if (token is null)
            {
                token = HttpMcpHost.NewToken();
                services.CredentialStore.Set(TokenName, token);
            }
            return token;
        }
    }

    public string RegenerateToken()
    {
        services.CredentialStore.Remove(TokenName);
        return Token;
    }

    public async Task ApplyAsync()
    {
        var settings = services.Settings.Load();
        await StopAsync();
        if (!settings.McpEnabled) return;

        try
        {
            var policy = new McpExposurePolicy(settings.McpAllowWrites);
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
            _host = await HttpMcpHost.StartAsync(services.Catalog,
                new HttpMcpHostOptions(settings.McpPort, Token, policy, new Implementation { Name = McpClientConfig.DesktopServerName, Version = version }));
            _host.ToolInvoked += OnToolInvoked;
            IsRunning = true;
            Status = $"Agent access: on ({(policy.AllowWrites ? "read & write" : "read-only")}) · {_host.Options.Endpoint}";
        }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            Status = $"Agent access: failed to start on port {settings.McpPort} ({ex.GetBaseException().Message})";
        }
    }

    private void OnToolInvoked(ToolInvocation invocation) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            LastActivity = $"Agent called {invocation.ToolName} at {invocation.At:HH:mm:ss}");

    private async Task StopAsync()
    {
        if (_host is null) return;
        _host.ToolInvoked -= OnToolInvoked;
        await _host.DisposeAsync();
        _host = null;
        IsRunning = false;
        LastActivity = null;
        Status = "Agent access: off";
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
