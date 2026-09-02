using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;

namespace Jellyfin.Plugin.Anemone.Contracts;

/// <summary>What an agent announced in its <c>hello</c> frame (see PROTOCOL.md).</summary>
public sealed record AgentInfo(
    string Name,
    string Version,
    string Platform,
    string FfmpegPath,
    string FfmpegVersion,
    IReadOnlyList<string> Hwaccels,
    IReadOnlyList<string> Encoders,
    IReadOnlyList<string> Decoders,
    IReadOnlyList<string> Filters,
    IReadOnlyList<AgentMount> Mounts,
    int MaxSessions,
    DateTimeOffset ConnectedAt,
    string Hwaccel = "none",
    string? HwaccelDevice = null);

/// <summary>
/// One of an agent's mount points. <paramref name="ServerPath"/> is what the Jellyfin server calls the
/// same tree (optional on the wire; defaults to <paramref name="Path"/> when the agent's layout is
/// identical to the server's) - see PROTOCOL.md "Path mapping". Use <see cref="EffectiveServerPath"/>
/// for placement/rewriting, never <see cref="ServerPath"/> directly, so the default is applied uniformly.
/// </summary>
public sealed record AgentMount(string Path, bool Ok, string? ServerPath = null)
{
    /// <summary>What the server calls this tree: <see cref="ServerPath"/> when announced, otherwise <see cref="Path"/>.</summary>
    public string EffectiveServerPath => string.IsNullOrEmpty(ServerPath) ? Path : ServerPath;
}

/// <summary>What a job needs from an agent, derived from the ffmpeg argv.</summary>
public sealed record JobRequirements(
    IReadOnlyList<string> Hwaccels,
    IReadOnlyList<string> Encoders,
    IReadOnlyList<string> Decoders,
    IReadOnlyList<string> Filters,
    IReadOnlyList<string> InputPaths);

/// <summary>A fully rewritten job ready to be sent to an agent.</summary>
public sealed record RemoteJobSpec(
    string Id,
    IReadOnlyList<string> Argv,
    string IngestToken,
    string Label,
    IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>Callbacks from the agent connection for one job. Called from the socket read loop — must not block.</summary>
public interface IRemoteJobSink
{
    void OnStarted(int pid);

    /// <summary>One ffmpeg stderr line, no trailing newline.</summary>
    void OnStderrLine(string line);

    /// <summary>Terminal. <paramref name="exitCode"/> is -1 when killed or when the connection was lost.</summary>
    void OnExited(int exitCode, string? error);
}

/// <summary>Handle on a job running on an agent.</summary>
public interface IRemoteJob
{
    string Id { get; }

    string AgentName { get; }

    /// <summary>Forward raw bytes to ffmpeg's stdin on the agent (Jellyfin writes "q\n", "p", "u").</summary>
    Task SendStdinAsync(string data, CancellationToken cancellationToken = default);

    /// <summary>SIGKILL on the agent side.</summary>
    Task KillAsync(CancellationToken cancellationToken = default);

    /// <summary>Completes with the exit code once <see cref="IRemoteJobSink.OnExited"/> has fired.</summary>
    Task<int> Completion { get; }
}

/// <summary>One connected agent.</summary>
public interface IAgentConnection
{
    AgentInfo Info { get; }

    int ActiveJobs { get; }

    bool IsConnected { get; }

    DateTimeOffset LastSeen { get; }

    /// <summary>Send a job; resolves when the agent acknowledged with <c>started</c> (or faults on <c>exit</c>/timeout).</summary>
    Task<IRemoteJob> StartJobAsync(RemoteJobSpec spec, IRemoteJobSink sink, CancellationToken cancellationToken);
}

/// <summary>Registry of live agents + placement.</summary>
public interface IAgentRegistry
{
    IReadOnlyList<IAgentConnection> Agents { get; }

    /// <summary>
    /// Connected, alive, capacity-available agents whose mounts cover <paramref name="requirements"/>'s
    /// input paths (server-side) and whose ffmpeg version satisfies policy, ordered least-loaded-first.
    /// Does NOT filter on hwaccel/encoders/decoders/filters - a different-hardware agent can still be a
    /// candidate because <see cref="Transcoding.HwTranslator"/> may be able to translate the job for it.
    /// It's the caller's (JobRouter's) job to try each candidate in order and take the first that works.
    /// </summary>
    IReadOnlyList<IAgentConnection> Candidates(JobRequirements requirements);
}

/// <summary>What an ingest bearer token grants: writing files with a given prefix into a given directory.</summary>
public sealed record IngestGrant(string JobId, string TargetDirectory, string FilePrefix);

public interface IIngestTokenStore
{
    /// <summary>Mint a token for a job. Returns the bearer string to embed in the ffmpeg -headers option.</summary>
    string Issue(string jobId, string targetDirectory, string filePrefix);

    bool TryValidate(string jobId, string bearerToken, out IngestGrant grant);

    void Revoke(string jobId);
}

/// <summary>Result of routing: which agent, the rewritten spec, and where segments will land.</summary>
public sealed record RoutePlan(
    IAgentConnection Agent,
    RemoteJobSpec Spec,
    string TargetDirectory,
    string FilePrefix,
    string Reason);

/// <summary>Decides local vs remote and rewrites the command line. Pure except for token issuance.</summary>
public interface IJobRouter
{
    /// <summary>
    /// Returns a plan when the job can and should go remote, otherwise null (and logs why at Debug).
    /// Never throws for a malformed command line — returns null.
    /// </summary>
    RoutePlan? TryPlan(StreamState state, string outputPath, string commandLineArguments, TranscodingJobType jobType);
}
