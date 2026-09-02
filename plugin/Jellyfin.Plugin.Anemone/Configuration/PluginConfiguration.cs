using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Anemone.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Master switch. Off = every transcode runs locally, agents may still connect.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Log the routing decision but always transcode locally. For staging the plugin on a live server.</summary>
    public bool DryRun { get; set; } = false;

    /// <summary>Shared secret agents must present as a Bearer token when opening the control WebSocket.</summary>
    public string SharedSecret { get; set; } = string.Empty;

    /// <summary>
    /// Absolute base URL agents reach this server on for segment uploads (e.g. http://10.240.0.1:8097).
    /// Leave EMPTY (recommended): each agent is then told the address it actually reached us on, so a
    /// Thunderbolt-attached agent keeps using the Thunderbolt link while a LAN agent uses the LAN. Set it
    /// only when that address is not what agents can reach (NAT, reverse proxy) — it applies to ALL agents.
    /// </summary>
    public string IngestBaseUrl { get; set; } = string.Empty;

    /// <summary>Prefer a remote agent over local transcoding whenever one has capacity.</summary>
    public bool PreferRemote { get; set; } = true;

    /// <summary>How many concurrent transcodes this server keeps for itself before it prefers agents (when PreferRemote is off).</summary>
    public int LocalMaxSessions { get; set; } = 2;

    /// <summary>Seconds to wait for an agent's "started" before falling back to local.</summary>
    public int AgentStartTimeoutSeconds { get; set; } = 15;

    /// <summary>Seconds without a status frame before an agent is considered dead.</summary>
    public int AgentDeadAfterSeconds { get; set; } = 30;

    /// <summary>
    /// TCP port for the plugin's own listener (agent control websocket + segment ingest). 0 disables it.
    /// It cannot be served from Jellyfin's own port: Jellyfin intercepts every websocket upgrade and
    /// caps request bodies at 30 MB. <see cref="IngestBaseUrl"/> must point at this port.
    /// </summary>
    public int AgentListenPort { get; set; } = 8097;

    /// <summary>Require the agent's ffmpeg major.minor to match the server's.</summary>
    public bool RequireMatchingFfmpeg { get; set; } = true;

    /// <summary>
    /// Allow routing a job to an agent whose hwaccel profile differs from the source (translating the
    /// command line via <see cref="Jellyfin.Plugin.Anemone.Transcoding.HwTranslator"/>). When false, only
    /// agents whose profile already matches the source are eligible - today's (pre-translation) behaviour.
    /// </summary>
    public bool AllowHwProfileTranslation { get; set; } = true;
}
