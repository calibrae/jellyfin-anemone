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

        // anemone: job.TranscodingThrottler is never populated any more, for either a local or a remote
        // job - our AnemoneTranscodingThrottler fork lives in the manager's own map instead (see
        // GetThrottleStatus and the Throttling_* tests below). This agent didn't report
        // ffmpeg.pause_keys support (AgentInfoBuilder's default), so it gets no throttler at all either.
        Assert.Null(job.TranscodingThrottler);
        Assert.Empty(harness.Manager.GetThrottleStatus());
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

    // --- PreferRemote / LocalMaxSessions (RemotePlacementPolicy) ---

    [Fact]
    public async Task StartFfMpeg_PreferRemoteFalse_BelowLocalMaxSessions_NeverConsultsRouter()
    {
        using var harness = new AnemoneTranscodeManagerHarness(cfg =>
        {
            cfg.PreferRemote = false;
            cfg.LocalMaxSessions = 2;
        });
        var outputPath = harness.OutputPath("prefer-local.m3u8");
        harness.UseFakeFfmpeg(outputPath);

        // The router would happily route this if asked - PreferRemote=false with 0 active local jobs
        // (well below the cap of 2) must mean it's never even asked.
        harness.Router.PlanToReturn = BuildPlan(new FakeAgentConnection(new AgentInfoBuilder().Build()), "unused", "/tmp", "prefer-local");

        var state = harness.NewState().Build();
        using var cts = new CancellationTokenSource();

        var job = await harness.Manager.StartFfMpeg(state, outputPath, "-f hls -y " + outputPath, Guid.Empty, TranscodingJobType.Hls, cts);

        Assert.NotNull(job);
        Assert.True(File.Exists(outputPath));
        Assert.Empty(harness.Router.Calls);
    }

    [Fact]
    public async Task StartFfMpeg_PreferRemoteFalse_AtLocalMaxSessions_RoutesTheNextJob()
    {
        using var harness = new AnemoneTranscodeManagerHarness(cfg =>
        {
            cfg.PreferRemote = false;
            cfg.LocalMaxSessions = 2;
        });

        // Two local jobs, kept running (FakeFfmpegScript's default: waits on stdin for "q") so they count
        // as active local jobs for the third StartFfMpeg call below. Distinct device/play-session ids so
        // KillTranscodingJobs below can target each one individually - NewState()'s default is the same
        // fixed "device-1"/"play-session-1" for every build.
        var outputPath1 = harness.OutputPath("local-1.m3u8");
        harness.UseFakeFfmpeg(outputPath1);
        var state1 = harness.NewState().WithDeviceId("device-1").WithPlaySessionId("session-1").Build();
        using var cts1 = new CancellationTokenSource();
        var job1 = await harness.Manager.StartFfMpeg(state1, outputPath1, "-f hls -y " + outputPath1, Guid.Empty, TranscodingJobType.Hls, cts1);
        Assert.False(job1.HasExited);

        var outputPath2 = harness.OutputPath("local-2.m3u8");
        harness.UseFakeFfmpeg(outputPath2);
        var state2 = harness.NewState().WithDeviceId("device-2").WithPlaySessionId("session-2").Build();
        using var cts2 = new CancellationTokenSource();
        var job2 = await harness.Manager.StartFfMpeg(state2, outputPath2, "-f hls -y " + outputPath2, Guid.Empty, TranscodingJobType.Hls, cts2);
        Assert.False(job2.HasExited);

        // The decision for job2 was made while only job1 was active (1 < 2) - the router still hasn't
        // been asked at all.
        Assert.Empty(harness.Router.Calls);

        var outputPath3 = harness.OutputPath("routed-3.m3u8");
        var jobId3 = Guid.NewGuid().ToString("N");
        var agent = new FakeAgentConnection(new AgentInfoBuilder().Build()) { ExitCodeAfterStart = null };
        agent.OnStartJobCalled = (_, _) => File.WriteAllText(outputPath3, string.Empty);
        harness.Router.PlanToReturn = BuildPlan(agent, jobId3, Path.GetDirectoryName(outputPath3)!, "routed-3");

        var state3 = harness.NewState().WithDeviceId("device-3").WithPlaySessionId("session-3").Build();
        using var cts3 = new CancellationTokenSource();
        var job3 = await harness.Manager.StartFfMpeg(state3, outputPath3, "-f hls -y " + outputPath3, Guid.Empty, TranscodingJobType.Hls, cts3);

        Assert.Single(harness.Router.Calls);
        Assert.Equal(jobId3, job3.Id);

        // Clean up the two local jobs so they don't outlive the test.
        await harness.Manager.KillTranscodingJobs(state1.Request.DeviceId, state1.Request.PlaySessionId, _ => false);
        await harness.Manager.KillTranscodingJobs(state2.Request.DeviceId, state2.Request.PlaySessionId, _ => false);

        var killTask = harness.Manager.KillTranscodingJobs(state3.Request.DeviceId, state3.Request.PlaySessionId, _ => false);
        var started = Assert.Single(agent.StartedJobs);
        await Waiting.UntilAsync(() => started.Job.StdinSent.Contains("q\n"));
        started.Job.CompleteExited(0);
        await killTask;
    }

    [Fact]
    public async Task StartFfMpeg_PreferRemoteTrue_RoutesEvenWithNoActiveLocalJobs()
    {
        using var harness = new AnemoneTranscodeManagerHarness(cfg => cfg.PreferRemote = true);
        var outputPath = harness.OutputPath("prefer-remote.m3u8");
        var jobId = Guid.NewGuid().ToString("N");
        var agent = new FakeAgentConnection(new AgentInfoBuilder().Build()) { ExitCodeAfterStart = null };
        agent.OnStartJobCalled = (_, _) => File.WriteAllText(outputPath, string.Empty);
        harness.Router.PlanToReturn = BuildPlan(agent, jobId, Path.GetDirectoryName(outputPath)!, "prefer-remote");

        var state = harness.NewState().Build();
        using var cts = new CancellationTokenSource();

        var job = await harness.Manager.StartFfMpeg(state, outputPath, "-f hls -y " + outputPath, Guid.Empty, TranscodingJobType.Hls, cts);

        Assert.Equal(jobId, job.Id);
        Assert.Single(harness.Router.Calls);

        var killTask = harness.Manager.KillTranscodingJobs(state.Request.DeviceId, state.Request.PlaySessionId, _ => false);
        var started = Assert.Single(agent.StartedJobs);
        await Waiting.UntilAsync(() => started.Job.StdinSent.Contains("q\n"));
        started.Job.CompleteExited(0);
        await killTask;
    }

    // --- Throttling (v2.2) ---
    //
    // Pure throttler decision logic (pause/resume/never-twice/EnableThrottling gate/HasExited) is covered
    // directly, fast, against a hand-built TranscodingJob in AnemoneTranscodingThrottlerTests. These tests
    // are about the MANAGER's own responsibilities: only building a throttler for a remote job whose agent
    // reported ffmpeg.pause_keys, and tearing it down on every terminal path.

    [Fact]
    public async Task Throttling_NonCapableAgent_GetsNoThrottler()
    {
        using var harness = new AnemoneTranscodeManagerHarness();
        var outputPath = harness.OutputPath("throttle-incapable.m3u8");
        var jobId = Guid.NewGuid().ToString("N");
        // AgentInfoBuilder's PauseKeysSupported defaults to false, mirroring the wire's own
        // "absent means unsupported" rule (PROTOCOL.md "Throttling (v2.2)").
        var agent = new FakeAgentConnection(new AgentInfoBuilder().WithName("trish").Build())
        {
            ExitCodeAfterStart = null,
        };
        agent.OnStartJobCalled = (_, _) => File.WriteAllText(outputPath, string.Empty);
        harness.Router.PlanToReturn = BuildPlan(agent, jobId, Path.GetDirectoryName(outputPath)!, "throttle-incapable");

        var state = harness.NewState().Build();
        using var cts = new CancellationTokenSource();
        await harness.Manager.StartFfMpeg(state, outputPath, "-f hls -y " + outputPath, Guid.Empty, TranscodingJobType.Hls, cts);

        Assert.Empty(harness.Manager.GetThrottleStatus());
        Assert.True(harness.LoggerFactory.HasMessageContaining("did not report ffmpeg.pause_keys support"));

        var killTask = harness.Manager.KillTranscodingJobs(state.Request.DeviceId, state.Request.PlaySessionId, _ => false);
        var started = Assert.Single(agent.StartedJobs);
        await Waiting.UntilAsync(() => started.Job.StdinSent.Contains("q\n"));
        started.Job.CompleteExited(0);
        await killTask;
    }

    [Fact]
    public async Task Throttling_CapableAgent_GetsThrottler_RemovedFromMapOnKill()
    {
        using var harness = new AnemoneTranscodeManagerHarness();
        var outputPath = harness.OutputPath("throttle-kill.m3u8");
        var jobId = Guid.NewGuid().ToString("N");
        var agent = new FakeAgentConnection(new AgentInfoBuilder().WithName("trish").WithPauseKeysSupported().Build())
        {
            ExitCodeAfterStart = null,
        };
        agent.OnStartJobCalled = (_, _) => File.WriteAllText(outputPath, string.Empty);
        harness.Router.PlanToReturn = BuildPlan(agent, jobId, Path.GetDirectoryName(outputPath)!, "throttle-kill");

        var state = harness.NewState().Build();
        using var cts = new CancellationTokenSource();
        await harness.Manager.StartFfMpeg(state, outputPath, "-f hls -y " + outputPath, Guid.Empty, TranscodingJobType.Hls, cts);

        Assert.Contains(harness.Manager.GetThrottleStatus(), t => t.JobId == jobId && t.AgentName == "trish" && !t.Paused);

        var killTask = harness.Manager.KillTranscodingJobs(state.Request.DeviceId, state.Request.PlaySessionId, _ => false);
        var started = Assert.Single(agent.StartedJobs);
        await Waiting.UntilAsync(() => started.Job.StdinSent.Contains("q\n"));
        started.Job.CompleteExited(0);
        await killTask;

        // Never got paused (the real timer never ticked in this test), so kill's Stop() call is a no-op
        // resume - see Throttling_RemoteJob_PauseCapableAgent below for the paused case.
        Assert.DoesNotContain("u", started.Job.StdinSent);
        Assert.Empty(harness.Manager.GetThrottleStatus());
    }

    [Fact]
    public async Task Throttling_CapableAgent_RemovedFromMapOnNormalExit()
    {
        using var harness = new AnemoneTranscodeManagerHarness();
        var outputPath = harness.OutputPath("throttle-exit.m3u8");
        var jobId = Guid.NewGuid().ToString("N");
        var agent = new FakeAgentConnection(new AgentInfoBuilder().WithName("trish").WithPauseKeysSupported().Build())
        {
            ExitCodeAfterStart = null, // drive the exit ourselves, via the sink, below
        };
        agent.OnStartJobCalled = (_, _) => File.WriteAllText(outputPath, string.Empty);
        harness.Router.PlanToReturn = BuildPlan(agent, jobId, Path.GetDirectoryName(outputPath)!, "throttle-exit");

        var state = harness.NewState().Build();
        using var cts = new CancellationTokenSource();
        await harness.Manager.StartFfMpeg(state, outputPath, "-f hls -y " + outputPath, Guid.Empty, TranscodingJobType.Hls, cts);
        var started = Assert.Single(agent.StartedJobs);

        Assert.Contains(harness.Manager.GetThrottleStatus(), t => t.JobId == jobId);

        // The agent's own ffmpeg finished normally - same IRemoteJobSink.OnExited call a real "exit" frame
        // drives (see AgentConnection.HandleExit).
        started.Sink.OnExited(0, null);

        await Waiting.UntilAsync(() => harness.Manager.GetThrottleStatus().Count == 0, because: "FinishRemoteJob should tear the throttler down on normal exit");
    }

    [Fact]
    public async Task Throttling_CapableAgent_RemovedFromMapOnAgentDisconnect()
    {
        using var harness = new AnemoneTranscodeManagerHarness();
        var outputPath = harness.OutputPath("throttle-disconnect.m3u8");
        var jobId = Guid.NewGuid().ToString("N");
        var agent = new FakeAgentConnection(new AgentInfoBuilder().WithName("trish").WithPauseKeysSupported().Build())
        {
            ExitCodeAfterStart = null,
        };
        agent.OnStartJobCalled = (_, _) => File.WriteAllText(outputPath, string.Empty);
        harness.Router.PlanToReturn = BuildPlan(agent, jobId, Path.GetDirectoryName(outputPath)!, "throttle-disconnect");

        var state = harness.NewState().Build();
        using var cts = new CancellationTokenSource();
        await harness.Manager.StartFfMpeg(state, outputPath, "-f hls -y " + outputPath, Guid.Empty, TranscodingJobType.Hls, cts);
        var started = Assert.Single(agent.StartedJobs);

        Assert.Contains(harness.Manager.GetThrottleStatus(), t => t.JobId == jobId);

        // Mirrors AgentConnection.FailAllPendingJobs: the control connection dropped, code -1, no more
        // frames will ever come for this job.
        started.Sink.OnExited(-1, "connection lost");

        await Waiting.UntilAsync(() => harness.Manager.GetThrottleStatus().Count == 0, because: "FinishRemoteJob should tear the throttler down when the agent connection is lost");
    }

    [Fact(Timeout = 20000)]
    public async Task Throttling_RemoteJob_PauseCapableAgent_KeysReachAgent_AndClearOnKill()
    {
        // Genuinely slow (~5s): AnemoneTranscodingThrottler's timer (forked verbatim from upstream) has a
        // hardcoded 5000ms due-time/period with no configuration seam to shorten for tests, mirroring
        // KillTranscodingJobs_RemoteJob_SendsQuitThenKillsAfterGracePeriod above. See
        // AnemoneTranscodingThrottlerTests for a fast, seam-driven version of the pause/resume decision
        // logic in isolation - this test's only job is proving the MANAGER wires a throttler up to a real
        // remote job end to end and its keys really reach IRemoteJob.SendStdinAsync.
        using var harness = new AnemoneTranscodeManagerHarness();
        // EncodingOptions is a real Jellyfin model POCO (FakeServerConfigurationManager's own remarks) -
        // set its EnableThrottling explicitly rather than relying on whatever upstream's own default is.
        harness.ConfigManager.EncodingOptions.EnableThrottling = true;
        var outputPath = harness.OutputPath("throttle-pause.m3u8");
        var jobId = Guid.NewGuid().ToString("N");
        var agent = new FakeAgentConnection(new AgentInfoBuilder().WithName("trish").WithPauseKeysSupported().Build())
        {
            ExitCodeAfterStart = null,
        };
        agent.OnStartJobCalled = (_, _) => File.WriteAllText(outputPath, string.Empty);
        harness.Router.PlanToReturn = BuildPlan(agent, jobId, Path.GetDirectoryName(outputPath)!, "throttle-pause");

        var state = harness.NewState().Build();
        using var cts = new CancellationTokenSource();
        var job = await harness.Manager.StartFfMpeg(state, outputPath, "-f hls -y " + outputPath, Guid.Empty, TranscodingJobType.Hls, cts);

        Assert.Contains(harness.Manager.GetThrottleStatus(), t => t.JobId == jobId && t.AgentName == "trish");

        // Far enough ahead of the (nonexistent, in this test) viewer that the throttler's first tick pauses.
        job.DownloadPositionTicks = TimeSpan.FromMinutes(1).Ticks;
        job.TranscodingPositionTicks = job.DownloadPositionTicks + TimeSpan.FromMinutes(5).Ticks;

        var started = Assert.Single(agent.StartedJobs);
        await Waiting.UntilAsync(() => started.Job.StdinSent.Contains("p"), timeout: TimeSpan.FromSeconds(8), because: "the throttler's 5s timer should have paused by now");
        Assert.True(harness.Manager.GetThrottleStatus().Single(t => t.JobId == jobId).Paused);

        var killTask = harness.Manager.KillTranscodingJobs(state.Request.DeviceId, state.Request.PlaySessionId, _ => false);
        await Waiting.UntilAsync(() => started.Job.StdinSent.Contains("q\n"));
        started.Job.CompleteExited(0);
        await killTask;

        // Kill unpauses before sending "q" (see KillTranscodingJob) and removes the throttler.
        Assert.Contains("u", started.Job.StdinSent);
        Assert.Empty(harness.Manager.GetThrottleStatus());
    }
}
