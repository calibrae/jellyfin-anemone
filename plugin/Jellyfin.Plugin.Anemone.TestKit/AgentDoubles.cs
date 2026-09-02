using Jellyfin.Plugin.Anemone.Contracts;

namespace Jellyfin.Plugin.Anemone.TestKit;

/// <summary>
/// <see cref="IRemoteJob"/> handed back by <see cref="FakeAgentConnection.StartJobAsync"/>. Records every
/// stdin write and kill so a test can assert on them (e.g. "the manager sent q\n, then killed after the
/// grace period"), and exposes <see cref="CompleteExited"/> for a test driving the job's lifecycle by hand.
/// </summary>
public sealed class FakeRemoteJob : IRemoteJob
{
    private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeRemoteJob(string id, string agentName)
    {
        Id = id;
        AgentName = agentName;
    }

    public string Id { get; }

    public string AgentName { get; }

    public List<string> StdinSent { get; } = [];

    public int KillCount { get; private set; }

    public Task<int> Completion => _completion.Task;

    public Task SendStdinAsync(string data, CancellationToken cancellationToken = default)
    {
        StdinSent.Add(data);
        return Task.CompletedTask;
    }

    public Task KillAsync(CancellationToken cancellationToken = default)
    {
        KillCount++;
        return Task.CompletedTask;
    }

    /// <summary>Resolves <see cref="Completion"/>, mirroring what a real <c>exit</c> frame does.</summary>
    public void CompleteExited(int exitCode) => _completion.TrySetResult(exitCode);
}

/// <summary>
/// Records every callback <see cref="AnemoneTranscodeManager"/> wires up via <see cref="RemoteJobSink"/>
/// (started pid, stderr lines, exit code/error) without needing a real <see cref="System.IO.Pipelines.Pipe"/>
/// or <c>JobLogger</c> in the loop - useful when a test only cares "did the sink get told X", as opposed to
/// "did X reach Jellyfin's own progress-reporting path" (for that, use the real
/// <see cref="RemoteJobSink"/>/pipe/<c>JobLogger</c> combination the manager itself builds, and assert on
/// the resulting <see cref="MediaBrowser.Controller.MediaEncoding.TranscodingJob"/>/session-manager calls instead).
/// </summary>
public sealed class RecordingRemoteJobSink : IRemoteJobSink
{
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<int> StartedPids { get; } = [];

    public List<string> StderrLines { get; } = [];

    public (int Code, string? Error)? Exited { get; private set; }

    public void OnStarted(int pid) => StartedPids.Add(pid);

    public void OnStderrLine(string line) => StderrLines.Add(line);

    public void OnExited(int exitCode, string? error)
    {
        Exited = (exitCode, error);
        _exited.TrySetResult();
    }

    /// <summary>Completes once <see cref="OnExited"/> has fired.</summary>
    public Task WaitForExitAsync(CancellationToken cancellationToken = default) => _exited.Task.WaitAsync(cancellationToken);
}

/// <summary>
/// Scriptable <see cref="IAgentConnection"/> fake. Configure the "script" properties before the job
/// starts, then call <see cref="StartJobAsync"/> (directly, or indirectly by handing this to a
/// <see cref="RoutePlan"/> the <see cref="AnemoneTranscodeManager"/> under test routes to). Every started
/// job is recorded in <see cref="StartedJobs"/>.
/// </summary>
public sealed class FakeAgentConnection : IAgentConnection
{
    /// <summary>One job this connection was asked to start, with the sink the caller supplied and the resulting handle.</summary>
    public sealed record StartedJob(RemoteJobSpec Spec, IRemoteJobSink Sink, FakeRemoteJob Job);

    public FakeAgentConnection(AgentInfo? info = null)
    {
        Info = info ?? new AgentInfoBuilder().Build();
    }

    public AgentInfo Info { get; set; }

    public string IngestBase { get; set; } = "http://10.10.0.2:8097";

    public int ActiveJobs { get; set; }

    public bool IsConnected { get; set; } = true;

    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;

    public List<StartedJob> StartedJobs { get; } = [];

    /// <summary>When set, <see cref="StartJobAsync"/> throws this instead of starting anything (models a transport-level start failure).</summary>
    public Exception? ThrowOnStart { get; set; }

    /// <summary>
    /// When true, <see cref="StartJobAsync"/> immediately reports the job exited with code -1 (the "agent
    /// connection was already gone" case), then throws - mirroring <c>AgentConnection.FailAllPendingJobs</c>.
    /// </summary>
    public bool DropConnectionOnStart { get; set; }

    /// <summary>
    /// Delay before <see cref="OnStarted"/> fires, or <c>null</c> to never fire it at all (models an
    /// agent that never acknowledges - combine with a short-lived <see cref="CancellationToken"/> from the
    /// caller, since this fake does not itself enforce <c>AgentStartTimeoutSeconds</c>).
    /// </summary>
    public TimeSpan? AckDelay { get; set; } = TimeSpan.Zero;

    public int StartedPid { get; set; } = 4242;

    /// <summary>Stderr lines delivered to the sink shortly after start.</summary>
    public IReadOnlyList<string> StderrLines { get; set; } = [];

    /// <summary>
    /// Exit code delivered to the sink after start (and after <see cref="StderrLines"/>), or <c>null</c> to
    /// leave the job running - a test can then drive it to completion itself via the returned
    /// <see cref="FakeRemoteJob"/>/<see cref="StartedJob"/>.
    /// </summary>
    public int? ExitCodeAfterStart { get; set; } = 0;

    public string? ExitErrorAfterStart { get; set; }

    /// <summary>Extra delay between the ack and the scripted exit, on top of <see cref="AckDelay"/>.</summary>
    public TimeSpan ExitDelay { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Optional hook invoked synchronously inside <see cref="StartJobAsync"/> before any scripted
    /// behaviour runs - e.g. to create the job's output file so a test can assert the manager stops
    /// polling once it appears, without a real ffmpeg process in the loop.
    /// </summary>
    public Action<RemoteJobSpec, IRemoteJobSink>? OnStartJobCalled { get; set; }

    public async Task<IRemoteJob> StartJobAsync(RemoteJobSpec spec, IRemoteJobSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(sink);

        OnStartJobCalled?.Invoke(spec, sink);

        if (ThrowOnStart is not null)
        {
            throw ThrowOnStart;
        }

        var job = new FakeRemoteJob(spec.Id, Info.Name);
        StartedJobs.Add(new StartedJob(spec, sink, job));

        if (DropConnectionOnStart)
        {
            sink.OnExited(-1, "connection lost");
            job.CompleteExited(-1);
            throw new InvalidOperationException($"anemone-testkit: connection to agent {Info.Name} lost while starting job {spec.Id}");
        }

        if (AckDelay is null)
        {
            // Never acks: wait on the caller's own token/timeout forever (bounded by whatever cancellationToken
            // AnemoneTranscodeManager's own AgentStartTimeoutSeconds produces - see PluginInstanceScope).
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new TimeoutException("anemone-testkit: unreachable - Task.Delay(Infinite) always throws or is cancelled");
        }

        if (AckDelay > TimeSpan.Zero)
        {
            await Task.Delay(AckDelay.Value, cancellationToken).ConfigureAwait(false);
        }

        sink.OnStarted(StartedPid);

        // Guaranteed, unconditional: AnemoneTranscodeManager attaches the real JobLogger to this sink's
        // pipe (TryStartRemoteAsync's RemoteJobSink) via `new JobLogger(...).StartStreamingLog(...)`,
        // fire-and-forget, exactly like it does for a local process's stderr. JobLogger's read loop is
        // guarded by `while (!reader.EndOfStream ...)`, and StreamReader.EndOfStream performs a
        // *synchronous, blocking* peek-read on its very first evaluation - before the loop body's first
        // real await - so it runs on whichever thread is running JobLogger's synchronous prologue, which
        // in practice is AnemoneTranscodeManager's own caller thread (see the identical remark on
        // FakeFfmpegScript for the local-path version of this). A job that never gets any stderr and
        // never exits (both are legitimate things to script here) would otherwise block that thread
        // forever. One guaranteed line unblocks it without depending on what the test itself scripted.
        sink.OnStderrLine("anemone-testkit: fake agent job started");

        if (StderrLines.Count > 0 || ExitCodeAfterStart.HasValue)
        {
            _ = Task.Run(
                async () =>
                {
                    foreach (var line in StderrLines)
                    {
                        sink.OnStderrLine(line);
                    }

                    if (ExitCodeAfterStart is { } code)
                    {
                        if (ExitDelay > TimeSpan.Zero)
                        {
                            await Task.Delay(ExitDelay).ConfigureAwait(false);
                        }

                        sink.OnExited(code, ExitErrorAfterStart);
                        job.CompleteExited(code);
                    }
                },
                CancellationToken.None);
        }

        return job;
    }
}

/// <summary>
/// <see cref="IAgentRegistry"/> fake. <see cref="Candidates"/> just returns whatever was configured and
/// records the last <see cref="JobRequirements"/> it was asked about, mirroring
/// <c>Jellyfin.Plugin.Anemone.Tests.Transcoding.FakeAgentRegistry</c> (which this supersedes).
/// </summary>
public sealed class FakeAgentRegistry : IAgentRegistry
{
    /// <summary>Convenience single-candidate setter/getter for tests that only care about one agent.</summary>
    public IAgentConnection? AgentToReturn
    {
        get => CandidatesToReturn.Count > 0 ? CandidatesToReturn[0] : null;
        set => CandidatesToReturn = value is null ? [] : [value];
    }

    public IReadOnlyList<IAgentConnection> CandidatesToReturn { get; set; } = [];

    public JobRequirements? LastRequirements { get; private set; }

    public IReadOnlyList<IAgentConnection> Agents => CandidatesToReturn;

    public IReadOnlyList<IAgentConnection> Candidates(JobRequirements requirements)
    {
        LastRequirements = requirements;
        return CandidatesToReturn;
    }
}

/// <summary>
/// <see cref="IJobRouter"/> fake that returns a canned <see cref="RoutePlan"/> (or null, for "stay local")
/// regardless of input - lets <see cref="AnemoneTranscodeManager"/> tests control routing directly instead
/// of going through real command-line analysis/placement (already covered by RoutePlannerTests/
/// JobRouterTests/HwTranslatorTests). Every call is recorded.
/// </summary>
public sealed class FakeJobRouter : IJobRouter
{
    public sealed record Call(MediaBrowser.Controller.Streaming.StreamState State, string OutputPath, string CommandLineArguments, MediaBrowser.Controller.MediaEncoding.TranscodingJobType JobType);

    /// <summary>The plan <see cref="TryPlan"/> returns. Null (the default) means "stay local".</summary>
    public RoutePlan? PlanToReturn { get; set; }

    public List<Call> Calls { get; } = [];

    public RoutePlan? TryPlan(MediaBrowser.Controller.Streaming.StreamState state, string outputPath, string commandLineArguments, MediaBrowser.Controller.MediaEncoding.TranscodingJobType jobType)
    {
        Calls.Add(new Call(state, outputPath, commandLineArguments, jobType));
        return PlanToReturn;
    }
}

/// <summary>
/// <see cref="IIngestTokenStore"/> fake. Records every issue/revoke so a test can assert on the token
/// lifecycle ("issued on plan, revoked on exit, revoked on failed start") without a real bearer-token
/// store in the loop.
/// </summary>
public sealed class FakeIngestTokenStore : IIngestTokenStore
{
    public List<(string JobId, string TargetDirectory, string FilePrefix)> Issued { get; } = [];

    public List<string> Revoked { get; } = [];

    public string TokenToReturn { get; set; } = "test-token";

    public string Issue(string jobId, string targetDirectory, string filePrefix)
    {
        Issued.Add((jobId, targetDirectory, filePrefix));
        return TokenToReturn;
    }

    public bool TryValidate(string jobId, string bearerToken, out IngestGrant grant)
    {
        grant = new IngestGrant(jobId, "/tmp", "prefix");
        return true;
    }

    public void Revoke(string jobId) => Revoked.Add(jobId);
}
