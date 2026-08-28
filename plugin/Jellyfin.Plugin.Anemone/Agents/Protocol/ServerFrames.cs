namespace Jellyfin.Plugin.Anemone.Agents.Protocol;

// Frames sent Server -> Agent. See PROTOCOL.md "## Server -> Agent".

/// <summary>The <c>server</c> block inside a <see cref="WelcomeFrame"/>.</summary>
public sealed record ServerInfo(string Version, string FfmpegVersion);

/// <summary>Accepts a <see cref="HelloFrame"/>.</summary>
public sealed record WelcomeFrame(ServerInfo Server, string IngestBase, int PingIntervalS) : Frame
{
    public string Type => "welcome";
}

/// <summary>Rejects a <see cref="HelloFrame"/> (or anything else sent as the first frame); the server closes after this.</summary>
public sealed record RejectFrame(string Reason) : Frame
{
    public string Type => "reject";
}

/// <summary>Assigns a job to the agent. The agent must reply <see cref="StartedFrame"/> or <see cref="ExitFrame"/>.</summary>
public sealed record JobFrame(
    string Id,
    IReadOnlyList<string> Argv,
    string Token,
    string Label,
    IReadOnlyDictionary<string, string>? Env = null) : Frame
{
    public string Type => "job";
}

/// <summary>Raw bytes to write to ffmpeg's stdin on the agent, unbuffered, immediately. No newline is implied.</summary>
public sealed record StdinFrame(string Id, string Data) : Frame
{
    public string Type => "stdin";
}

/// <summary>SIGKILL the ffmpeg process for job <paramref name="Id"/> now; an <see cref="ExitFrame"/> must still follow.</summary>
public sealed record KillFrame(string Id) : Frame
{
    public string Type => "kill";
}

/// <summary>Either side may send this; the peer answers with a pong frame (<see cref="PongFrame"/> from the agent).</summary>
public sealed record PingFrame : Frame
{
    public string Type => "ping";
}
