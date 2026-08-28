using Jellyfin.Plugin.Cluster.Agents;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Cluster.Api;

/// <summary>One agent's row in the <see cref="ClusterStatusController"/> response.</summary>
public sealed record AgentStatusEntry(
    string Name,
    string Platform,
    string Version,
    string FfmpegVersion,
    IReadOnlyList<string> Hwaccels,
    int Encoders,
    IReadOnlyList<MountStatusEntry> Mounts,
    int ActiveJobs,
    int MaxSessions,
    DateTimeOffset ConnectedAt,
    DateTimeOffset LastSeen,
    bool Connected);

/// <summary>One mount entry in an <see cref="AgentStatusEntry"/>.</summary>
public sealed record MountStatusEntry(string Path, bool Ok);

/// <summary>Response body of <c>GET Cluster/status</c>.</summary>
public sealed record ClusterStatusResponse(
    bool Enabled,
    bool DryRun,
    string IngestBase,
    string ServerFfmpeg,
    IReadOnlyList<AgentStatusEntry> Agents);

/// <summary>Dashboard-facing snapshot of plugin config + connected agents. Requires elevation.</summary>
[ApiController]
[Route("Cluster")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class ClusterStatusController : ControllerBase
{
    private readonly AgentHub _hub;
    private readonly IMediaEncoder _mediaEncoder;

    public ClusterStatusController(AgentHub hub, IMediaEncoder mediaEncoder)
    {
        _hub = hub;
        _mediaEncoder = mediaEncoder;
    }

    [HttpGet("status")]
    public ActionResult<ClusterStatusResponse> GetStatus()
    {
        var config = Plugin.Instance?.Configuration;

        var agents = _hub.Agents
            .Select(a => new AgentStatusEntry(
                a.Info.Name,
                a.Info.Platform,
                a.Info.Version,
                a.Info.FfmpegVersion,
                a.Info.Hwaccels,
                a.Info.Encoders.Count,
                a.Info.Mounts.Select(m => new MountStatusEntry(m.Path, m.Ok)).ToList(),
                a.ActiveJobs,
                a.Info.MaxSessions,
                a.Info.ConnectedAt,
                a.LastSeen,
                a.IsConnected))
            .ToList();

        var response = new ClusterStatusResponse(
            config?.Enabled ?? false,
            config?.DryRun ?? false,
            _hub.ResolveIngestBase(),
            _mediaEncoder.EncoderVersion?.ToString() ?? string.Empty,
            agents);

        return Ok(response);
    }
}
