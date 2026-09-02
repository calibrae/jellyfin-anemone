using Jellyfin.Plugin.Anemone.Agents.Protocol;
using Jellyfin.Plugin.Anemone.Contracts;

namespace Jellyfin.Plugin.Anemone.TestKit;

/// <summary>Builds an <see cref="AgentInfo"/> (server-side view of a connected agent). Defaults describe a healthy macOS/VideoToolbox agent, mirroring the fleet in DEPLOY.md.</summary>
public sealed class AgentInfoBuilder
{
    private string _name = "trish";
    private string _version = "0.1.0";
    private string _platform = "macos-arm64";
    private string _ffmpegPath = "/opt/anemone/ffmpeg";
    private string _ffmpegVersion = "7.1.4-Jellyfin";
    private IReadOnlyList<string> _hwaccels = ["videotoolbox"];
    private IReadOnlyList<string> _encoders = ["h264_videotoolbox", "hevc_videotoolbox", "aac_at", "libx264"];
    private IReadOnlyList<string> _decoders = ["h264", "hevc"];
    private IReadOnlyList<string> _filters = ["scale_vt", "scale", "overlay"];
    private IReadOnlyList<AgentMount> _mounts = [new AgentMount("/Volumes/data", true)];
    private int _maxSessions = 3;
    private DateTimeOffset _connectedAt = DateTimeOffset.UtcNow;
    private string _hwaccel = "videotoolbox";
    private string? _hwaccelDevice;

    public AgentInfoBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    public AgentInfoBuilder WithPlatform(string platform)
    {
        _platform = platform;
        return this;
    }

    public AgentInfoBuilder WithFfmpegVersion(string version)
    {
        _ffmpegVersion = version;
        return this;
    }

    public AgentInfoBuilder WithHwaccels(params string[] hwaccels)
    {
        _hwaccels = hwaccels;
        return this;
    }

    public AgentInfoBuilder WithEncoders(params string[] encoders)
    {
        _encoders = encoders;
        return this;
    }

    public AgentInfoBuilder WithDecoders(params string[] decoders)
    {
        _decoders = decoders;
        return this;
    }

    public AgentInfoBuilder WithFilters(params string[] filters)
    {
        _filters = filters;
        return this;
    }

    public AgentInfoBuilder WithMounts(params AgentMount[] mounts)
    {
        _mounts = mounts;
        return this;
    }

    /// <summary>
    /// Adds one mount (path on the agent, ok, optional server-visible path, optional locality - see
    /// PROTOCOL.md "Path mapping" and "Placement inputs (v2.1)").
    /// </summary>
    public AgentInfoBuilder WithMount(string path, bool ok = true, string? serverPath = null, bool? local = null)
    {
        _mounts = [.. _mounts, new AgentMount(path, ok, serverPath, local)];
        return this;
    }

    public AgentInfoBuilder WithMaxSessions(int maxSessions)
    {
        _maxSessions = maxSessions;
        return this;
    }

    public AgentInfoBuilder WithConnectedAt(DateTimeOffset connectedAt)
    {
        _connectedAt = connectedAt;
        return this;
    }

    public AgentInfoBuilder WithHwaccel(string hwaccel, string? hwaccelDevice = null)
    {
        _hwaccel = hwaccel;
        _hwaccelDevice = hwaccelDevice;
        return this;
    }

    public AgentInfo Build() => new(
        _name,
        _version,
        _platform,
        _ffmpegPath,
        _ffmpegVersion,
        _hwaccels,
        _encoders,
        _decoders,
        _filters,
        _mounts,
        _maxSessions,
        _connectedAt,
        _hwaccel,
        _hwaccelDevice);
}

/// <summary>
/// Builds a <see cref="HelloFrame"/> - the wire message a real polyp sends as its first frame. Useful for
/// integration tests driving <see cref="Agents.AgentHub"/>/<see cref="Agents.AnemoneListener"/> over a real
/// or fake <see cref="System.Net.WebSockets.WebSocket"/>. Defaults mirror <see cref="AgentInfoBuilder"/>'s.
/// </summary>
public sealed class HelloFrameBuilder
{
    private string _name = "trish";
    private string _version = "0.1.0";
    private string _platform = "macos-arm64";
    private string _ffmpegPath = "/opt/anemone/ffmpeg";
    private string _ffmpegVersion = "7.1.4-Jellyfin";
    private IReadOnlyList<string>? _hwaccels = ["videotoolbox"];
    private IReadOnlyList<string>? _encoders = ["h264_videotoolbox", "hevc_videotoolbox", "aac_at", "libx264"];
    private IReadOnlyList<string>? _decoders = ["h264", "hevc"];
    private IReadOnlyList<string>? _filters = ["scale_vt", "scale", "overlay"];
    private IReadOnlyList<AgentMountFrame>? _mounts = [new AgentMountFrame("/Volumes/data", true)];
    private int _maxSessions = 3;
    private string? _hwaccel;
    private string? _hwaccelDevice;

    public HelloFrameBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    public HelloFrameBuilder WithPlatform(string platform)
    {
        _platform = platform;
        return this;
    }

    public HelloFrameBuilder WithFfmpeg(string path, string version)
    {
        _ffmpegPath = path;
        _ffmpegVersion = version;
        return this;
    }

    public HelloFrameBuilder WithHwaccels(params string[] hwaccels)
    {
        _hwaccels = hwaccels;
        return this;
    }

    public HelloFrameBuilder WithEncoders(params string[] encoders)
    {
        _encoders = encoders;
        return this;
    }

    public HelloFrameBuilder WithMounts(params AgentMountFrame[] mounts)
    {
        _mounts = mounts;
        return this;
    }

    /// <summary>Adds one mount (path on the agent, ok, optional server-visible path, optional locality).</summary>
    public HelloFrameBuilder WithMount(string path, bool ok = true, string? serverPath = null, bool? local = null)
    {
        _mounts = [.. _mounts ?? [], new AgentMountFrame(path, ok, serverPath, local)];
        return this;
    }

    public HelloFrameBuilder WithMaxSessions(int maxSessions)
    {
        _maxSessions = maxSessions;
        return this;
    }

    public HelloFrameBuilder WithHwaccel(string? hwaccel, string? hwaccelDevice = null)
    {
        _hwaccel = hwaccel;
        _hwaccelDevice = hwaccelDevice;
        return this;
    }

    public HelloFrame Build() => new(
        _name,
        _version,
        _platform,
        new FfmpegInfoFrame(_ffmpegPath, _ffmpegVersion, _hwaccels, _encoders, _decoders, _filters),
        _mounts,
        _maxSessions,
        _hwaccel,
        _hwaccelDevice);
}
