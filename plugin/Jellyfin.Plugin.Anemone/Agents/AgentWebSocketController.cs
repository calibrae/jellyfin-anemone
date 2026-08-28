using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Anemone.Agents;

/// <summary>The control-channel WebSocket endpoint a polyp opens: <c>GET /Anemone/agents/ws</c>.</summary>
[ApiController]
[Route("Anemone")]
[AllowAnonymous]
public sealed class AgentWebSocketController : ControllerBase
{
    private readonly AgentHub _hub;
    private readonly ILogger<AgentWebSocketController> _logger;

    public AgentWebSocketController(AgentHub hub, ILogger<AgentWebSocketController> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    [HttpGet("agents/ws")]
    public async Task<IActionResult> AgentWebSocket()
    {
        var secret = Plugin.Instance?.Configuration.SharedSecret ?? string.Empty;
        if (string.IsNullOrEmpty(secret))
        {
            _logger.LogWarning("anemone: rejecting agent connection from {Remote}: SharedSecret not configured", HttpContext.Connection.RemoteIpAddress);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Cluster: SharedSecret not configured");
        }

        if (!TryGetBearerToken(out var provided) || !ConstantTimeEquals(provided, secret))
        {
            _logger.LogWarning("anemone: rejecting agent connection from {Remote}: bad or missing bearer token", HttpContext.Connection.RemoteIpAddress);
            return Unauthorized();
        }

        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            return BadRequest();
        }

        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        await _hub.RunConnectionAsync(socket, HttpContext.Connection.RemoteIpAddress, HttpContext.RequestAborted).ConfigureAwait(false);
        return new EmptyResult();
    }

    private bool TryGetBearerToken(out string token)
    {
        token = string.Empty;
        var header = Request.Headers.Authorization.ToString();
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
