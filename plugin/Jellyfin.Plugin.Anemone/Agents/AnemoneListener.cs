using System.Net;
using Jellyfin.Plugin.Anemone.Ingest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Anemone.Agents;

/// <summary>
/// The plugin's own HTTP listener: agent control websockets and segment ingest.
/// </summary>
/// <remarks>
/// anemone: this does NOT run inside Jellyfin's web pipeline, deliberately. Jellyfin installs a
/// websocket handler for every upgrade request before endpoint routing (Jellyfin.Server/Startup.cs:221
/// in 10.11.0) and answers anything without a Jellyfin API token with 403 "Token is required", so a
/// plugin-hosted controller can never accept an agent upgrade — and an IStartupFilter can't help
/// because plugin services are registered too late to affect the pipeline. Running our own Kestrel
/// also removes Jellyfin's 30 MB request-body cap and auth middleware from the segment upload path.
/// </remarks>
public sealed class AnemoneListener : IHostedService, IAsyncDisposable
{
    private const string WebSocketSuffix = "/agents/ws";
    private const string IngestSegment = "/ingest/";

    private readonly AgentWebSocketEndpoint _webSocketEndpoint;
    private readonly IngestHandler _ingest;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AnemoneListener> _logger;

    private IWebHost? _host;

    public AnemoneListener(
        AgentWebSocketEndpoint webSocketEndpoint,
        IngestHandler ingest,
        ILoggerFactory loggerFactory,
        ILogger<AnemoneListener> logger)
    {
        _webSocketEndpoint = webSocketEndpoint;
        _ingest = ingest;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var port = Plugin.Instance?.Configuration.AgentListenPort ?? 0;
        if (port <= 0)
        {
            _logger.LogWarning("anemone: agent listener disabled (AgentListenPort={Port}) — no agent can connect", port);
            return;
        }

        try
        {
            _host = new WebHostBuilder()
                .UseContentRoot(AppContext.BaseDirectory)

                // Without this the host tries to Assembly.Load(applicationName) to scan for hosting
                // startups; the plugin assembly isn't resolvable by name from the default context and
                // it logs a noisy "Startup assembly ... failed to execute".
                .UseSetting(WebHostDefaults.PreventHostingStartupKey, "true")
                .UseKestrel(options =>
                {
                    options.Listen(IPAddress.Any, port);

                    // Segments arrive chunked and can be arbitrarily large.
                    options.Limits.MaxRequestBodySize = null;
                })
                .ConfigureServices(services => services.AddSingleton(_loggerFactory))
                .Configure(app =>
                {
                    app.UseWebSockets();
                    app.Run(HandleAsync);
                })
                .Build();

            await _host.StartAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("anemone: agent listener started on port {Port} (control {Ws}, ingest {Ingest})", port, WebSocketSuffix, IngestSegment);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "anemone: failed to start the agent listener on port {Port}", port);
            _host = null;
        }
    }

    private async Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";

        if (path.TrimEnd('/').EndsWith(WebSocketSuffix, StringComparison.OrdinalIgnoreCase))
        {
            await _webSocketEndpoint.HandleAsync(context).ConfigureAwait(false);
            return;
        }

        var index = path.IndexOf(IngestSegment, StringComparison.OrdinalIgnoreCase);
        if (index >= 0 && HttpMethods.IsPut(context.Request.Method))
        {
            var parts = path[(index + IngestSegment.Length)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                await _ingest.HandleAsync(context, parts[0], parts[1]).ConfigureAwait(false);
                return;
            }
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_host is not null)
        {
            await _host.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            _host.Dispose();
            _host = null;
        }
    }
}
