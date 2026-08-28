namespace Jellyfin.Plugin.Cluster.Agents.Protocol;

// Frames sent Agent -> Server. See PROTOCOL.md "## Agent -> Server".

/// <summary>The ffmpeg capability block inside a <see cref="HelloFrame"/>.</summary>
public sealed record FfmpegInfoFrame(
    string Path,
    string Version,
    IReadOnlyList<string>? Hwaccels = null,
    IReadOnlyList<string>? Encoders = null,
    IReadOnlyList<string>? Decoders = null,
    IReadOnlyList<string>? Filters = null);

/// <summary>One mount entry inside <see cref="HelloFrame"/> / <see cref="StatusFrame"/>.</summary>
public sealed record AgentMountFrame(string Path, bool Ok);

/// <summary>First frame an agent sends after connecting. Server answers <see cref="WelcomeFrame"/> or <see cref="RejectFrame"/>.</summary>
public sealed record HelloFrame(
    string Name,
    string Version,
    string Platform,
    FfmpegInfoFrame Ffmpeg,
    IReadOnlyList<AgentMountFrame>? Mounts,
    int MaxSessions) : Frame
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
