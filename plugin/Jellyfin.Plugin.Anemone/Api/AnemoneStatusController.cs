using Jellyfin.Plugin.Anemone.Agents;
using Jellyfin.Plugin.Anemone.Contracts;
using Jellyfin.Plugin.Anemone.Transcoding;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Anemone.Api;

/// <summary>One agent's row in the <see cref="AnemoneStatusController"/> response.</summary>
/// <param name="PauseKeySupport">anemone (v2.2 throttling): whether this agent reported <c>ffmpeg.pause_keys</c> - see PROTOCOL.md "Throttling (v2.2)".</param>
/// <param name="PausedJobs">anemone (v2.2 throttling): how many of this agent's currently-throttled jobs are paused right now.</param>
public sealed record AgentStatusEntry(
    string Name,
    string Platform,
    string Version,
    string FfmpegVersion,
    IReadOnlyList<string> Hwaccels,
    int Encoders,
    IReadOnlyList<MountStatusEntry> Mounts,
    int ActiveJobs,
    int PausedJobs,
    int MaxSessions,
    DateTimeOffset ConnectedAt,
    DateTimeOffset LastSeen,
    bool Connected,
    string Hwaccel,
    string? HwaccelDevice,
    bool PauseKeySupport,
    double? MeasuredSpeed,
    double? Load,
    string RankReason);

/// <summary>One mount entry in an <see cref="AgentStatusEntry"/>.</summary>
public sealed record MountStatusEntry(string Path, bool Ok, string ServerPath, bool? Local);

/// <summary>Response body of <c>GET Anemone/status</c>.</summary>
public sealed record AnemoneStatusResponse(
    bool Enabled,
    bool DryRun,
    bool PreferRemote,
    int LocalMaxSessions,
    string IngestBase,
    string ServerFfmpeg,
    IReadOnlyList<AgentStatusEntry> Agents);

/// <summary>Dashboard-facing snapshot of plugin config + connected agents. Requires elevation.</summary>
[ApiController]
[Route("Anemone")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class AnemoneStatusController : ControllerBase
{
    private readonly AgentHub _hub;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly AnemoneTranscodeManager _manager;

    public AnemoneStatusController(AgentHub hub, IMediaEncoder mediaEncoder, AnemoneTranscodeManager manager)
    {
        _hub = hub;
        _mediaEncoder = mediaEncoder;
        _manager = manager;
    }

    [HttpGet("status")]
    public ActionResult<AnemoneStatusResponse> GetStatus()
    {
        var config = Plugin.Instance?.Configuration;

        // anemone (v2.2 throttling): one query, then aggregated per agent below - GetThrottleStatus's own
        // remarks explain why job-level throttle state can't live on AgentHub/IAgentConnection.
        var throttleStatus = _manager.GetThrottleStatus();

        var agents = _hub.Agents
            .Select(a => new AgentStatusEntry(
                a.Info.Name,
                a.Info.Platform,
                a.Info.Version,
                a.Info.FfmpegVersion,
                a.Info.Hwaccels,
                a.Info.Encoders.Count,
                a.Info.Mounts.Select(m => new MountStatusEntry(m.Path, m.Ok, m.EffectiveServerPath, m.Local)).ToList(),
                a.ActiveJobs,
                throttleStatus.Count(t => t.Paused && string.Equals(t.AgentName, a.Info.Name, StringComparison.Ordinal)),
                a.Info.MaxSessions,
                a.Info.ConnectedAt,
                a.LastSeen,
                a.IsConnected,
                a.Info.Hwaccel,
                a.Info.HwaccelDevice,
                a.Info.PauseKeysSupported,
                a.MeasuredSpeed,
                a.Load,
                RankReason(a)))
            .ToList();

        var response = new AnemoneStatusResponse(
            config?.Enabled ?? false,
            config?.DryRun ?? false,
            config?.PreferRemote ?? true,
            config?.LocalMaxSessions ?? 0,
            _hub.ResolveIngestBase(),
            _mediaEncoder.EncoderVersion?.ToString() ?? string.Empty,
            agents);

        return Ok(response);
    }

    /// <summary>
    /// General (not-job-specific) ranking breakdown for the dashboard: locality is shown as "unknown"
    /// since there's no particular input path to check a mount against here - see each mount's own
    /// <c>Local</c> flag in <see cref="MountStatusEntry"/> for that. Everything else (measured throughput,
    /// spare capacity, reported load) is the same signal <see cref="AgentHub.CandidatesFrom"/> uses for a
    /// real placement decision, so this is a fair "why is this agent favoured right now" summary.
    /// </summary>
    private static string RankReason(IAgentConnection agent)
    {
        var spareCapacityFraction = agent.Info.MaxSessions > 0
            ? 1.0 - ((double)agent.ActiveJobs / agent.Info.MaxSessions)
            : 0.0;

        var input = new AgentRankingInput(agent.Info.Name, Local: null, agent.MeasuredSpeed, spareCapacityFraction, agent.Load);
        return AgentRanker.Score(input).Reason;
    }
}
