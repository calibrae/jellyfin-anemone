using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Anemone.Agents;

/// <summary>
/// Handles <c>GET /Anemone/agents/ws</c>, the control channel a polyp opens.
/// </summary>
/// <remarks>
/// anemone: this is deliberately NOT an MVC controller. Jellyfin installs its own websocket handler
/// middleware (Jellyfin.Server/Startup.cs:221 in 10.11.0) which intercepts EVERY upgrade request
/// before endpoint routing and requires a Jellyfin API token, so an upgrade aimed at a plugin
/// controller is answered with 403 "Token is required" and never reaches the controller. We are
/// invoked from <see cref="AnemoneStartupFilter"/> instead, which runs ahead of Jellyfin's pipeline.
/// </remarks>
public sealed class AgentWebSocketEndpoint
{
    private readonly AgentHub _hub;
    private readonly ILogger<AgentWebSocketEndpoint> _logger;

    public AgentWebSocketEndpoint(AgentHub hub, ILogger<AgentWebSocketEndpoint> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    /// <summary>The request path this endpoint answers, matched as a suffix so a base URL prefix still works.</summary>
    public const string Path = "/Anemone/agents/ws";

    public async Task HandleAsync(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        var secret = Plugin.Instance?.Configuration.SharedSecret ?? string.Empty;

        if (string.IsNullOrEmpty(secret))
        {
            _logger.LogWarning("anemone: rejecting agent connection from {Remote}: SharedSecret not configured", remote);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        if (!TryGetBearerToken(context, out var provided) || !ConstantTimeEquals(provided, secret))
        {
            _logger.LogWarning("anemone: rejecting agent connection from {Remote}: bad or missing bearer token", remote);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        _logger.LogInformation("anemone: agent websocket accepted from {Remote}", remote);
        await _hub.RunConnectionAsync(socket, remote, context.Connection.LocalIpAddress, context.RequestAborted).ConfigureAwait(false);
    }

    private static bool TryGetBearerToken(HttpContext context, out string token)
    {
        token = string.Empty;
        var header = context.Request.Headers.Authorization.ToString();
        const string Prefix = "Bearer ";
        if (header.StartsWith(Prefix, StringComparison.Ordinal))
        {
            token = header[Prefix.Length..];
        }

        return !string.IsNullOrEmpty(token);
    }

    private static bool ConstantTimeEquals(string provided, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
}
