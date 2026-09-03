using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using Jellyfin.Plugin.Anemone.Agents.Protocol;
using Jellyfin.Plugin.Anemone.Contracts;
using Jellyfin.Plugin.Anemone.TestKit;
using Jellyfin.Plugin.Anemone.Transcoding;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.Anemone.IntegrationTests;

/// <summary>
/// Throttling (v2.2, PROTOCOL.md) through the real hub: a scripted "fake agent" that reports
/// <c>ffmpeg.pause_keys: true</c> in its hello, and a real <see cref="AnemoneTranscodeManager"/> routed to
/// it exactly like <see cref="JobLifecycleTests"/> (routing itself is short-circuited via
/// <see cref="FakeJobRouter"/>, already covered elsewhere). What's under test is everything downstream of
/// "the router said: this agent" for throttling specifically: a real <c>stdin</c> frame carrying
/// <c>p</c> reaching the agent's real <see cref="ClientWebSocket"/> when the job races far ahead of the
/// viewer, and <c>u</c> once it catches back up - <see cref="AnemoneTranscodingThrottler"/>'s own decision
/// logic is covered fast and directly (no real Timer, no real socket) in
/// <c>AnemoneTranscodingThrottlerTests</c>; this only proves the end-to-end wiring, including the real 5s
/// Timer it's built on (see that class's own remarks - no configuration seam exists to shorten it, hence
/// this test's own wall-clock cost).
/// </summary>
/// <remarks>See <see cref="JobLifecycleTests"/>'s own remarks on why every scripted agent driver below sends a <see cref="StderrFrame"/> shortly after <see cref="StartedFrame"/>.</remarks>
public class ThrottlingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // AnemoneTranscodingThrottler's timer is a hardcoded, verbatim-from-upstream 5000ms due-time/period
    // with no configuration seam (see its own remarks) - waits below need slack for at least one real tick.
    private static readonly TimeSpan ThrottleTimeout = TimeSpan.FromSeconds(12);

    private static async Task<ClientWebSocket> ConnectFakeAgentAsync(AnemoneIntegrationHarness harness, string name)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + harness.Configuration.SharedSecret);
        using (var connectCts = new CancellationTokenSource(Timeout))
        {
            await socket.ConnectAsync(new Uri(harness.WebSocketUrl), connectCts.Token).ConfigureAwait(false);
        }

        // WithPauseKeys(true): this agent's ffmpeg supports p/u - see PROTOCOL.md "Throttling (v2.2)".
        // Without it, AnemoneTranscodeManager.StartRemoteThrottler would never build a throttler at all.
        await HandshakeTests.SendAsync(socket, Frame.Serialize(new HelloFrameBuilder().WithName(name).WithPauseKeys(true).Build())).ConfigureAwait(false);
        var welcome = Assert.IsType<WelcomeFrame>(Frame.Parse(await HandshakeTests.ReceiveAsync(socket).ConfigureAwait(false)));
        Assert.NotNull(welcome);

        await Waiting.UntilAsync(() => harness.Hub.Agents.Any(a => a.Info.Name == name)).ConfigureAwait(false);
        return socket;
    }

    /// <summary>Same wiring as <c>JobLifecycleTests.BuildManager</c>, plus <c>EnableThrottling</c> turned on explicitly (upstream's own default is not something this test should depend on).</summary>
    private static (AnemoneTranscodeManager Manager, FakeJobRouter Router, FakeMediaSourceManager MediaSourceManager) BuildManager(AnemoneIntegrationHarness harness)
    {
        var loggerFactory = new FakeLoggerFactory();
        var appPaths = new FakeApplicationPaths(harness.Root);
        var mediaEncoder = new FakeMediaEncoder { EncoderPath = "/opt/anemone/ffmpeg-placeholder-not-runnable", EncoderVersion = harness.MediaEncoder.EncoderVersion };
        var configManager = new FakeServerConfigurationManager(new FakeServerApplicationPaths(appPaths), appPaths);
        configManager.EncodingOptions.EnableThrottling = true;
        var userManager = new FakeUserManager();
        var sessionManager = new FakeSessionManager();
        var mediaSourceManager = new FakeMediaSourceManager();
        var attachmentExtractor = new FakeAttachmentExtractor();
        var encodingHelper = EncodingHelperFactory.Create(appPaths, mediaEncoder, configManager);
        var router = new FakeJobRouter();

        var manager = new AnemoneTranscodeManager(
            loggerFactory,
            new RealFileSystem(),
            appPaths,
            configManager,
            userManager,
            sessionManager,
            encodingHelper,
            mediaEncoder,
            mediaSourceManager,
            attachmentExtractor,
            router,
            harness.TokenStore); // the REAL token store the real IngestHandler also validates against

        return (manager, router, mediaSourceManager);
    }

    [Fact(Timeout = 40000)]
    public async Task RemoteJob_RunningFarAhead_GetsPaused_ThenResumedOnCatchUp()
    {
        await using var harness = await AnemoneIntegrationHarness.StartAsync();
        var agentSocket = await ConnectFakeAgentAsync(harness, "fake-agent-throttle");
        var agentConnection = harness.Hub.Agents.Single(a => a.Info.Name == "fake-agent-throttle");
        Assert.True(agentConnection.Info.PauseKeysSupported);

        var (manager, router, mediaSourceManager) = BuildManager(harness);

        var targetDir = harness.Root.CreateSubdirectory("throttle");
        var jobId = Guid.NewGuid().ToString("N");
        const string Prefix = "throttle";
        var outputPath = Path.Combine(targetDir, Prefix + ".m3u8");
        var token = harness.TokenStore.Issue(jobId, targetDir, Prefix);
        var spec = new RemoteJobSpec(jobId, ["-f", "hls", "-hls_segment_filename", Prefix + "%d.ts", "-y", Prefix + ".m3u8"], token, "integration throttle job");
        router.PlanToReturn = new RoutePlan(agentConnection, spec, targetDir, Prefix, "integration test plan");

        var state = new StreamStateBuilder()
            .WithMediaSourceManager(mediaSourceManager)
            .WithTranscodeManager(manager)
            .Build();
        using var cts = new CancellationTokenSource();

        // Drives the fake agent's side of the protocol concurrently with the manager call below: ack the
        // job, PUT the playlist so StartFfMpeg's wait loop returns, then read exactly the stdin sequence
        // throttling should produce - "p" once the job is set (below) to look far ahead, "u" once it's set
        // to look caught up, "q\n" from the final kill.
        var agentDriverTask = Task.Run(async () =>
        {
            var jobFrame = Assert.IsType<JobFrame>(Frame.Parse(await HandshakeTests.ReceiveAsync(agentSocket)));
            Assert.Equal(jobId, jobFrame.Id);
            Assert.Equal(token, jobFrame.Token);

            await HandshakeTests.SendAsync(agentSocket, Frame.Serialize(new StartedFrame(jobId, 1234)));
            await HandshakeTests.SendAsync(agentSocket, Frame.Serialize(new StderrFrame(jobId, "frame=  10 fps= 30 q=-1.0 size=100KiB time=00:00:01.00 bitrate=800kbits/s speed=25.0x")));

            using var putClient = new HttpClient();
            await PutAsync(putClient, harness.HttpBaseUrl, jobId, Prefix + "0.ts", token, "segment-bytes");
            await PutAsync(putClient, harness.HttpBaseUrl, jobId, Prefix + ".m3u8", token, "#EXTM3U\n#EXT-X-VERSION:3\n");

            var pauseFrame = Assert.IsType<StdinFrame>(Frame.Parse(await HandshakeTests.ReceiveAsync(agentSocket)));
            Assert.Equal(jobId, pauseFrame.Id);
            Assert.Equal("p", pauseFrame.Data);

            var resumeFrame = Assert.IsType<StdinFrame>(Frame.Parse(await HandshakeTests.ReceiveAsync(agentSocket)));
            Assert.Equal(jobId, resumeFrame.Id);
            Assert.Equal("u", resumeFrame.Data);

            var quitFrame = Assert.IsType<StdinFrame>(Frame.Parse(await HandshakeTests.ReceiveAsync(agentSocket)));
            Assert.Equal("q\n", quitFrame.Data);
            await HandshakeTests.SendAsync(agentSocket, Frame.Serialize(new ExitFrame(jobId, 0)));
        });

        var job = await manager.StartFfMpeg(state, outputPath, "-f hls -y " + outputPath, Guid.Empty, TranscodingJobType.Hls, cts);
        Assert.NotNull(job);
        Assert.True(File.Exists(outputPath), "anemone-test: the playlist PUT by the fake agent should have satisfied StartFfMpeg's wait-for-output loop");
        Assert.Contains(manager.GetThrottleStatus(), t => t.JobId == jobId && t.AgentName == "fake-agent-throttle" && !t.Paused);

        // The job is racing far ahead of what's been downloaded - the throttler's next real tick should pause.
        job.DownloadPositionTicks = TimeSpan.FromMinutes(1).Ticks;
        job.TranscodingPositionTicks = job.DownloadPositionTicks + TimeSpan.FromMinutes(5).Ticks;

        await Waiting.UntilAsync(
            () => manager.GetThrottleStatus().Single(t => t.JobId == jobId).Paused,
            timeout: ThrottleTimeout,
            because: "the throttler's real timer should have paused by now");

        // The viewer catches up - the next tick should resume.
        job.DownloadPositionTicks = job.TranscodingPositionTicks;

        await Waiting.UntilAsync(
            () => !manager.GetThrottleStatus().Single(t => t.JobId == jobId).Paused,
            timeout: ThrottleTimeout,
            because: "the throttler's real timer should have resumed by now");

        await manager.KillTranscodingJobs(state.Request.DeviceId, state.Request.PlaySessionId, _ => false);
        await agentDriverTask.WaitAsync(Timeout);

        await Waiting.UntilAsync(() => job.HasExited, because: "FinishRemoteJob should have run after the real exit frame arrived");
        Assert.Equal(0, job.ExitCode);
        Assert.Empty(manager.GetThrottleStatus());

        await HandshakeTests.CloseQuietlyAsync(agentSocket);
        agentSocket.Dispose();
    }

    private static async Task PutAsync(HttpClient client, string httpBaseUrl, string jobId, string name, string token, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{httpBaseUrl}/Anemone/ingest/{jobId}/{name}")
        {
            Content = new StringContent(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
