namespace Jellyfin.Plugin.Anemone.Agents.Protocol;

// Frames sent Agent -> Server. See PROTOCOL.md "## Agent -> Server".

/// <summary>The ffmpeg capability block inside a <see cref="HelloFrame"/>.</summary>
public sealed record FfmpegInfoFrame(
    string Path,
    string Version,
    IReadOnlyList<string>? Hwaccels = null,
    IReadOnlyList<string>? Encoders = null,
    IReadOnlyList<string>? Decoders = null,
    IReadOnlyList<string>? Filters = null);

/// <summary>
/// One mount entry inside <see cref="HelloFrame"/> / <see cref="StatusFrame"/>. <paramref name="ServerPath"/>
/// is what the Jellyfin server calls the same tree; absent (null) when identical to <paramref name="Path"/>.
/// See PROTOCOL.md "Path mapping". <paramref name="Local"/> is <c>true</c> when the tree is on storage
/// attached to the agent itself; optional, see PROTOCOL.md "Placement inputs (v2.1)".
/// </summary>
public sealed record AgentMountFrame(string Path, bool Ok, string? ServerPath = null, bool? Local = null);

/// <summary>
/// First frame an agent sends after connecting. Server answers <see cref="WelcomeFrame"/> or <see cref="RejectFrame"/>.
/// <paramref name="Hwaccel"/> is the hardware-acceleration profile the agent wants its jobs built for
/// (<c>videotoolbox|nvenc|qsv|vaapi|amf|rkmpp|none</c>); when absent the server infers it from
/// <c>ffmpeg.hwaccels</c> + <paramref name="Platform"/> (see <see cref="Transcoding.HwTranslator.InferProfile"/>).
/// <paramref name="HwaccelDevice"/> is the device the profile needs (e.g. <c>/dev/dri/renderD128</c> for VAAPI/QSV).
/// </summary>
public sealed record HelloFrame(
    string Name,
    string Version,
    string Platform,
    FfmpegInfoFrame Ffmpeg,
    IReadOnlyList<AgentMountFrame>? Mounts,
    int MaxSessions,
    string? Hwaccel = null,
    string? HwaccelDevice = null) : Frame
{
    public string Type => "hello";
}

/// <summary>Sent on change and at least every 10s; doubles as a heartbeat.</summary>
public sealed record StatusFrame(
    int Active,
    double? Load = null,
    IReadOnlyList<AgentMountFrame>? Mounts = null) : Frame
{
    public string Type => "status";
}

/// <summary>ffmpeg spawned for job <paramref name="Id"/>. Acknowledges a <see cref="JobFrame"/>.</summary>
public sealed record StartedFrame(string Id, int Pid) : Frame
{
    public string Type => "started";
}

/// <summary>One ffmpeg stderr line, verbatim, no trailing newline.</summary>
public sealed record StderrFrame(string Id, string Line) : Frame
{
    public string Type => "stderr";
}

/// <summary>Terminal for job <paramref name="Id"/>. <paramref name="Code"/> is -1 if killed by signal.</summary>
public sealed record ExitFrame(string Id, int Code, string? Error = null) : Frame
{
    public string Type => "exit";
}

/// <summary>Non-fatal agent-side problem. <paramref name="Id"/> is present for a job-scoped error.</summary>
public sealed record ErrorFrame(string Message, string? Id = null) : Frame
{
    public string Type => "error";
}

/// <summary>Reply to a server-initiated <see cref="PingFrame"/>.</summary>
public sealed record PongFrame : Frame
{
    public string Type => "pong";
}
