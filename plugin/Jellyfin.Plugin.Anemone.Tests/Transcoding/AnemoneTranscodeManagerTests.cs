using Jellyfin.Plugin.Anemone.Contracts;
using Jellyfin.Plugin.Anemone.TestKit;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.Anemone.Tests.Transcoding;

/// <summary>
/// Unit tests for <see cref="AnemoneTranscodeManager"/> - the 1075-line fork of upstream's own
/// TranscodeManager and, per RESEARCH.md/DEPLOY.md, the piece that actually ran the live deploy. Uses
/// <see cref="AnemoneTranscodeManagerHarness"/> throughout: a real manager wired to TestKit fakes/real
/// Jellyfin model POCOs, with <see cref="FakeJobRouter"/> standing in for the routing decision (already
/// covered end to end by JobRouterTests/RoutePlannerTests/HwTranslatorTests) so these tests can drive
/// "the router said: go to this agent / stay local" directly.
/// </summary>
public class AnemoneTranscodeManagerTests
{
    private static RoutePlan BuildPlan(FakeAgentConnection agent, string jobId, string targetDirectory, string filePrefix, string token = "tok", string label = "label") =>
        new(agent, new RemoteJobSpec(jobId, ["-f", "hls", "-y", "out"], token, label), targetDirectory, filePrefix, "test plan");

    // --- StartFfMpeg routing ---

    [Fact]
    public async Task StartFfMpeg_RoutesToAgent_WhenRouterReturnsPlan()
    {
        using var harness = new AnemoneTranscodeManagerHarness();
        var outputPath = harness.OutputPath("remote.m3u8");
        var jobId = Guid.NewGuid().ToString("N");
        var agent = new FakeAgentConnection(new AgentInfoBuilder().WithName("trish").Build())
        {
            ExitCodeAfterStart = null, // stays "running" until the test says otherwise
        };
        agent.OnStartJobCalled = (_, _) => File.WriteAllText(outputPath, string.Empty);
        harness.Router.PlanToReturn = BuildPlan(agent, jobId, Path.GetDirectoryName(outputPath)!, "remote");

        var state = harness.NewState().Build();
        using var cts = new CancellationTokenSource();

        var job = await harness.Manager.StartFfMpeg(state, outputPath, "-f hls -y " + outputPath, Guid.Empty, TranscodingJobType.Hls, cts);

        Assert.NotNull(job);
        Assert.Equal(jobId, job.Id);
        Assert.False(job.HasExited);
        var started = Assert.Single(agent.StartedJobs);
        Assert.Equal(jobId, started.Spec.Id);

        // anemone: no throttler for remote jobs (TranscodingThrottler dereferences job.Process!); the
        // segment cleaner never touches Process, so it's attached exactly like a local job's.
        Assert.Null(job.TranscodingThrottler);
        Assert.NotNull(job.TranscodingSegmentCleaner);

        // Clean up without waiting out the 5s kill grace period (that's its own dedicated test below).
        var killTask = harness.Manager.KillTranscodingJobs(state.Request.DeviceId, state.Request.PlaySessionId, _ => false);
        await Waiting.UntilAsync(() => started.Job.StdinSent.Contains("q\n"));
        started.Job.CompleteExited(0);
        await killTask;
    }

    [Fact]
    public async Task StartFfMpeg_FallsBackToLocal_WhenRouterReturnsNull()
    {
        using var harness = new AnemoneTranscodeManagerHarness();
        var outputPath = harness.OutputPath("local.m3u8");
        harness.UseFakeFfmpeg(outputPath);

        var state = harness.NewState().Build();
        using var cts = new CancellationTokenSource();

        var job = await harness.Manager.StartFfMpeg(state, outputPath, "-f hls -y " + outputPath, Guid.Empty, TranscodingJobType.Hls, cts);

        Assert.NotNull(job);
        Assert.False(job.HasExited);
        Assert.True(File.Exists(outputPath));
        var call = Assert.Single(harness.Router.Calls);
        Assert.Equal(outputPath, call.OutputPath);
    }

    [Fact]
    public async Task StartFfMpeg_FallsBackToLocal_WhenStartJobAsyncThrows()
    {
        using var harness = new AnemoneTranscodeManagerHarness();
        var outputPath = harness.OutputPath("throws.m3u8");
        harness.UseFakeFfmpeg(outputPath);
        var jobId = Guid.NewGuid().ToString("N");
        var agent = new FakeAgentConnection(new AgentInfoBuilder().Build())
        {
            ThrowOnStart = new IOException("anemone-test: agent connection reset while starting"),
        };
        harness.Router.PlanToReturn = BuildPlan(agent, jobId, Path.GetDirectoryName(outputPath)!, "throws");

        var state = harness.NewState().Build();
        using var cts = new CancellationTokenSource();

        var job = await harness.Manager.StartFfMpeg(state, outputPath, "-f hls -y " + outputPath, Guid.Empty, TranscodingJobType.Hls, cts);

        Assert.NotNull(job);
        Assert.True(File.Exists(outputPath));
        Assert.Contains(jobId, harness.TokenStore.Revoked);
    }

    [Fact]
    public async Task StartFfMpeg_FallsBackToLocal_WhenStartJobAsyncTimesOut()
    {
        // Harness sets AgentStartTimeoutSeconds=1 specifically so this doesn't wait the production
        // default of 15s - see AnemoneTranscodeManagerHarness/PluginInstanceScope.
        using var harness = new AnemoneTranscodeManagerHarness();
        var outputPath = harness.OutputPath("timeout.m3u8");
        harness.UseFakeFfmpeg(outputPath);
        var jobId = Guid.NewGuid().ToString("N");
        var agent = new FakeAgentConnection(new AgentInfoBuilder().Build())
        {
            AckDelay = null, // never sends "started" - StartJobAsync hangs until the manager's own timeout fires
        };
        harness.Router.PlanToReturn = BuildPlan(agent, jobId, Path.GetDirectoryName(outputPath)!, "timeout");

        var state = harness.NewState().Build();
        using var cts = new CancellationTokenSource();

        var job = await harness.Manager.StartFfMpeg(state, outputPath, "-f hls -y " + outputPath, Guid.Empty, TranscodingJobType.Hls, cts);

        Assert.NotNull(job);
        Assert.True(File.Exists(outputPath));
        Assert.Contains(jobId, harness.TokenStore.Revoked);
    }

    [Fact]
    public async Task StartFfMpeg_FallsBackToLocal_WhenRemoteJobExitsBeforeOutputAppears()
    {
        using var harness = new AnemoneTranscodeManagerHarness();
        var outputPath = harness.OutputPath("raced.m3u8");
        harness.UseFakeFfmpeg(outputPath);
        var jobId = Guid.NewGuid().ToString("N");
        var agent = new FakeAgentConnection(new AgentInfoBuilder().Build());

        // Fire OnExited synchronously, before this hook - and therefore StartJobAsync itself - returns:
        // i.e. before AnemoneTranscodeManager has had a chance to record the job in its internal
        // _remoteJobs map. FinishRemoteJob's own TryRemove (scheduled from inside OnExited, see
        // RemoteJobSink/TryStartRemoteAsync) is then guaranteed to run first and find nothing to
        // remove yet - the "already handled" log line is the observable signal that's happened. Waiting
        // for it (rather than a fixed delay) is what makes the manager's own subsequent
        // insert-then-notice-HasExited-then-remove sequence land deterministically instead of racing
        // FinishRemoteJob for who tears the job down (see TryStartRemoteAsync's own comment on this).
        agent.OnStartJobCalled = (_, sink) =>
        {
            sink.OnExited(-1, "spawn failed");
            SpinWait.SpinUntil(
                () => harness.LoggerFactory.HasMessageContaining("exit already handled by the local-fallback path"),
                TimeSpan.FromSeconds(2));
        };

        harness.Router.PlanToReturn = BuildPlan(agent, jobId, Path.GetDirectoryName(outputPath)!, "raced");

        var state = harness.NewState().Build();
        using var cts = new CancellationTokenSource();

        var job = await harness.Manager.StartFfMpeg(state, outputPath, "-f hls -y " + outputPath, Guid.Empty, TranscodingJobType.Hls, cts);

        Assert.NotNull(job);
        Assert.NotEqual(jobId, job.Id); // a brand new local job, not the remote one that never produced output
        Assert.True(File.Exists(outputPath));
        Assert.Contains(jobId, harness.TokenStore.Revoked);
    }

    // --- DryRun ---

    [Fact]
    public async Task StartFfMpeg_DryRun_LogsAndDoesNotRoute_RevokesToken()
    {
        using var harness = new AnemoneTranscodeManagerHarness(cfg => cfg.DryRun = true);
        var outputPath = harness.OutputPath("dryrun.m3u8");
        harness.UseFakeFfmpeg(outputPath);
        var jobId = Guid.NewGuid().ToString("N");
        var agent = new FakeAgentConnection(new AgentInfoBuilder().Build());
        harness.Router.PlanToReturn = BuildPlan(agent, jobId, Path.GetDirectoryName(outputPath)!, "dryrun");

        var state = harness.NewState().Build();
        using var cts = new CancellationTokenSource();

        var job = await harness.Manager.StartFfMpeg(state, outputPath, "-f hls -y " + outputPath, Guid.Empty, TranscodingJobType.Hls, cts);

        Assert.NotNull(job);
        Assert.Empty(agent.StartedJobs);
        Assert.Contains(jobId, harness.TokenStore.Revoked);
        Assert.True(harness.LoggerFactory.HasMessageContaining("dry-run"));
        Assert.True(File.Exists(outputPath));
    }

    // --- Kill path ---

    [Fact]
    public async Task KillTranscodingJobs_LocalJob_UsesStop()
    {
        using var harness = new AnemoneTranscodeManagerHarness();
        var outputPath = harness.OutputPath("local-kill.m3u8");
        harness.UseFakeFfmpeg(outputPath); // waits on stdin for "q" by default

        var state = harness.NewState().Build();
        using var cts = new CancellationTokenSource();
        var job = await harness.Manager.StartFfMpeg(state, outputPath, "-f hls -y " + outputPath, Guid.Empty, TranscodingJobType.Hls, cts);
        Assert.False(job.HasExited);

        await harness.Manager.KillTranscodingJobs(state.Request.DeviceId, state.Request.PlaySessionId, _ => false);

        Assert.True(job.HasExited);
        Assert.Equal(0, job.ExitCode);
    }

    [Fact(Timeout = 20000)]
    public async Task KillTranscodingJobs_RemoteJob_SendsQuitThenKillsAfterGracePeriod()
    {
        // Genuinely slow (~5s): StopRemoteJobAsync's "q then wait 5s then kill" grace period is a plain
        // Task.Delay(5000) with no configuration seam (unlike AgentStartTimeoutSeconds) to shorten for
        // tests - anemone's own addition, mirroring TranscodingJob.Stop()'s local-process behaviour.
        using var harness = new AnemoneTranscodeManagerHarness();
        var outputPath = harness.OutputPath("remote-kill.m3u8");
        var jobId = Guid.NewGuid().ToString("N");
        var agent = new FakeAgentConnection(new AgentInfoBuilder().Build())
        {
            ExitCodeAfterStart = null, // never confirms exit - forces the grace period to elapse
        };
        agent.OnStartJobCalled = (_, _) => File.WriteAllText(outputPath, string.Empty);
        harness.Router.PlanToReturn = BuildPlan(agent, jobId, Path.GetDirectoryName(outputPath)!, "remote-kill");

        var state = harness.NewState().Build();
        using var cts = new CancellationTokenSource();
        var job = await harness.Manager.StartFfMpeg(state, outputPath, "-f hls -y " + outputPath, Guid.Empty, TranscodingJobType.Hls, cts);
        var started = Assert.Single(agent.StartedJobs);

        // Also exercises the "TranscodingJob.Process == null never NREs" surface along a couple more
        // paths a remote job takes before being killed.
        harness.Manager.PingTranscodingJob(state.Request.PlaySessionId, isUserPaused: false);
        harness.Manager.OnTranscodeEndRequest(job);

        await harness.Manager.KillTranscodingJobs(state.Request.DeviceId, state.Request.PlaySessionId, _ => false);

        Assert.Contains("q\n", started.Job.StdinSent);
        Assert.Equal(1, started.Job.KillCount);
    }

    // --- Progress ---

    [Fact]
    public async Task Progress_StderrLinesReachReportTranscodingProgress()
    {
        using var harness = new AnemoneTranscodeManagerHarness();
        var outputPath = harness.OutputPath("progress.m3u8");
        var jobId = Guid.NewGuid().ToString("N");
        const string ProgressLine = "frame=  120 fps= 60 q=-0.0 size=    1024KiB time=00:00:04.00 bitrate=2097.2kbits/s speed=2.0x";
        var agent = new FakeAgentConnection(new AgentInfoBuilder().Build())
        {
            StderrLines = [ProgressLine],
            ExitCodeAfterStart = null, // keep the job (and state) alive while the assertions run
        };
        agent.OnStartJobCalled = (_, _) => File.WriteAllText(outputPath, string.Empty);
        harness.Router.PlanToReturn = BuildPlan(agent, jobId, Path.GetDirectoryName(outputPath)!, "progress");

        var state = harness.NewState().Build();
        using var cts = new CancellationTokenSource();
        var job = await harness.Manager.StartFfMpeg(state, outputPath, "-f hls -y " + outputPath, Guid.Empty, TranscodingJobType.Hls, cts);

        // The stderr line travels: FakeAgentConnection -> RemoteJobSink.OnStderrLine -> Pipe -> the real
        // JobLogger.StartStreamingLog -> StreamState.ReportTranscodingProgress -> back into the manager
        // under test's own ReportTranscodingProgress, exactly like a real agent's frames would.
        await Waiting.UntilAsync(() => job.Framerate.HasValue, because: "fps=60 should have parsed through JobLogger by now");

        Assert.Equal(60f, job.Framerate);
        Assert.NotNull(job.CompletionPercentage);

        await Waiting.UntilAsync(() => harness.SessionManager.ReportedTranscodingInfo.Count > 0);
        var (deviceId, _) = harness.SessionManager.ReportedTranscodingInfo[^1];
        Assert.Equal(state.Request.DeviceId, deviceId);
    }

    // --- Token lifecycle ---

    [Fact]
    public async Task TokenLifecycle_RevokedWhenRemoteJobExits()
    {
        using var harness = new AnemoneTranscodeManagerHarness();
        var outputPath = harness.OutputPath("exit.m3u8");
        var jobId = Guid.NewGuid().ToString("N");
        var agent = new FakeAgentConnection(new AgentInfoBuilder().Build())
        {
            ExitCodeAfterStart = 0,
        };
        agent.OnStartJobCalled = (_, _) => File.WriteAllText(outputPath, string.Empty);
        harness.Router.PlanToReturn = BuildPlan(agent, jobId, Path.GetDirectoryName(outputPath)!, "exit");

        var state = harness.NewState().Build();
        using var cts = new CancellationTokenSource();

        await harness.Manager.StartFfMpeg(state, outputPath, "-f hls -y " + outputPath, Guid.Empty, TranscodingJobType.Hls, cts);

        await Waiting.UntilAsync(() => harness.TokenStore.Revoked.Contains(jobId));
    }

    [Fact]
    public async Task TokenLifecycle_RevokedOnFailedStart()
    {
        using var harness = new AnemoneTranscodeManagerHarness();
        var outputPath = harness.OutputPath("failed-start.m3u8");
        harness.UseFakeFfmpeg(outputPath);
        var jobId = Guid.NewGuid().ToString("N");
        var agent = new FakeAgentConnection(new AgentInfoBuilder().Build())
        {
            ThrowOnStart = new InvalidOperationException("anemone-test: refused"),
        };
        harness.Router.PlanToReturn = BuildPlan(agent, jobId, Path.GetDirectoryName(outputPath)!, "failed-start");

        var state = harness.NewState().Build();
        using var cts = new CancellationTokenSource();

        await harness.Manager.StartFfMpeg(state, outputPath, "-f hls -y " + outputPath, Guid.Empty, TranscodingJobType.Hls, cts);

        Assert.Contains(jobId, harness.TokenStore.Revoked);
    }
}
