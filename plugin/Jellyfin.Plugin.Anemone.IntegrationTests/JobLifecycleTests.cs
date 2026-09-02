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
/// A full job lifecycle through the real <see cref="AnemoneTranscodeManager"/>, against a scripted "fake
/// agent" that speaks the real wire protocol over a real <see cref="ClientWebSocket"/>, PUTting real
/// segments into the real ingest endpoint. Routing itself is short-circuited (a <see cref="FakeJobRouter"/>
/// hands back a <see cref="RoutePlan"/> pointing at the real, already-connected <see cref="IAgentConnection"/>)
/// since JobRouter's own decision logic is already covered by JobRouterTests/RoutePlannerTests/
/// HwTranslatorTests - what's new here is everything downstream of "the router said: this agent".
/// </summary>
/// <remarks>
/// Every scripted agent driver below sends at least one <see cref="StderrFrame"/> shortly after
/// <see cref="StartedFrame"/>, even the disconnect test that otherwise has no use for one. That's
/// required, not decorative: <c>AnemoneTranscodeManager.TryStartRemoteAsync</c> attaches the real
/// <c>JobLogger</c> to the job's <c>RemoteJobSink</c> pipe with a *synchronous* first read
/// (<see cref="StreamReader.EndOfStream"/> blocks the calling thread on its first evaluation - see
/// <c>FakeAgentConnection</c>'s and <c>FakeFfmpegScript</c>'s matching remarks for the full explanation).
/// With zero stderr ever sent, that read blocks the manager's own calling thread forever, and
/// <c>StartFfMpeg</c> never returns - including never reaching the point where a test could abort the
/// socket to unblock it, which is a real deadlock, not just a slow test.
/// </remarks>
public class JobLifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Connects a real ClientWebSocket "fake agent" and completes the real hello/welcome handshake.</summary>
    private static async Task<ClientWebSocket> ConnectFakeAgentAsync(AnemoneIntegrationHarness harness, string name)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + harness.Configuration.SharedSecret);
        using (var connectCts = new CancellationTokenSource(Timeout))
        {
            await socket.ConnectAsync(new Uri(harness.WebSocketUrl), connectCts.Token).ConfigureAwait(false);
        }

        await HandshakeTests.SendAsync(socket, Frame.Serialize(new HelloFrameBuilder().WithName(name).Build())).ConfigureAwait(false);
        var welcome = Assert.IsType<WelcomeFrame>(Frame.Parse(await HandshakeTests.ReceiveAsync(socket).ConfigureAwait(false)));
        Assert.NotNull(welcome);

        await Waiting.UntilAsync(() => harness.Hub.Agents.Any(a => a.Info.Name == name)).ConfigureAwait(false);
        return socket;
    }

    /// <summary>Builds a real AnemoneTranscodeManager wired to the integration harness's real Hub/TokenStore; everything else is a TestKit fake.</summary>
    private static (AnemoneTranscodeManager Manager, FakeJobRouter Router, FakeMediaSourceManager MediaSourceManager, FakeSessionManager SessionManager, FakeLoggerFactory LoggerFactory) BuildManager(AnemoneIntegrationHarness harness)
    {
        var loggerFactory = new FakeLoggerFactory();
        var appPaths = new FakeApplicationPaths(harness.Root);
        var mediaEncoder = new FakeMediaEncoder { EncoderPath = "/opt/anemone/ffmpeg-placeholder-not-runnable", EncoderVersion = harness.MediaEncoder.EncoderVersion };
        var configManager = new FakeServerConfigurationManager(new FakeServerApplicationPaths(appPaths), appPaths);
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

        return (manager, router, mediaSourceManager, sessionManager, loggerFactory);
    }

    [Fact]
    public async Task FullLifecycle_PlanToStartedToSegmentsToStopToExit()
    {
        await using var harness = await AnemoneIntegrationHarness.StartAsync();
        var agentSocket = await ConnectFakeAgentAsync(harness, "fake-agent-lifecycle");
        var agentConnection = harness.Hub.Agents.Single(a => a.Info.Name == "fake-agent-lifecycle");

        var (manager, router, mediaSourceManager, _, loggerFactory) = BuildManager(harness);

        var targetDir = harness.Root.CreateSubdirectory("lifecycle");
        var jobId = Guid.NewGuid().ToString("N");
        const string Prefix = "lifecycle";
        var outputPath = Path.Combine(targetDir, Prefix + ".m3u8");
        var token = harness.TokenStore.Issue(jobId, targetDir, Prefix);
        var spec = new RemoteJobSpec(jobId, ["-f", "hls", "-hls_segment_filename", Prefix + "%d.ts", "-y", Prefix + ".m3u8"], token, "integration lifecycle job");
        router.PlanToReturn = new RoutePlan(agentConnection, spec, targetDir, Prefix, "integration test plan");

        var state = new StreamStateBuilder()
            .WithMediaSourceManager(mediaSourceManager)
            .WithTranscodeManager(manager)
            .Build();
        using var cts = new CancellationTokenSource();

        // Drives the fake agent's side of the protocol concurrently with the manager call below: receive
        // the job frame, ack started, emit a stderr progress line, PUT a segment then the playlist into
        // the real ingest endpoint (StartFfMpeg's own polling loop is watching for the playlist), then
        // wait for the "q" stdin the kill path below sends and confirm exit.
        var agentDriverTask = Task.Run(async () =>
        {
            var jobFrame = Assert.IsType<JobFrame>(Frame.Parse(await HandshakeTests.ReceiveAsync(agentSocket)));
            Assert.Equal(jobId, jobFrame.Id);
            Assert.Equal(token, jobFrame.Token);

            await HandshakeTests.SendAsync(agentSocket, Frame.Serialize(new StartedFrame(jobId, 4321)));
            await HandshakeTests.SendAsync(agentSocket, Frame.Serialize(new StderrFrame(jobId, "frame=  10 fps= 30 q=-1.0 size=100KiB time=00:00:01.00 bitrate=800kbits/s speed=1.0x")));

            using var putClient = new HttpClient();
            await PutAsync(putClient, harness.HttpBaseUrl, jobId, Prefix + "0.ts", token, "segment-bytes");
            await PutAsync(putClient, harness.HttpBaseUrl, jobId, Prefix + ".m3u8", token, "#EXTM3U\n#EXT-X-VERSION:3\n");

            var stdinFrame = Assert.IsType<StdinFrame>(Frame.Parse(await HandshakeTests.ReceiveAsync(agentSocket)));
            Assert.Equal("q\n", stdinFrame.Data);
            await HandshakeTests.SendAsync(agentSocket, Frame.Serialize(new ExitFrame(jobId, 0)));
        });

        var job = await manager.StartFfMpeg(state, outputPath, "-f hls -y " + outputPath, Guid.Empty, TranscodingJobType.Hls, cts);

        Assert.NotNull(job);
        Assert.Equal(jobId, job.Id);
        Assert.False(job.HasExited);
        Assert.True(File.Exists(outputPath), "anemone-test: the playlist PUT by the fake agent should have satisfied StartFfMpeg's wait-for-output loop");
        Assert.True(File.Exists(Path.Combine(targetDir, Prefix + "0.ts")));

        await manager.KillTranscodingJobs(state.Request.DeviceId, state.Request.PlaySessionId, _ => false);
        await agentDriverTask.WaitAsync(Timeout);

        await Waiting.UntilAsync(() => job.HasExited, because: "FinishRemoteJob should have run after the real exit frame arrived");
        Assert.Equal(0, job.ExitCode);
        Assert.True(loggerFactory.HasMessageContaining("stopping remote job"));

        await HandshakeTests.CloseQuietlyAsync(agentSocket);
        agentSocket.Dispose();
    }

    [Fact]
    public async Task AgentDisconnectMidJob_MarksJobExited()
    {
        await using var harness = await AnemoneIntegrationHarness.StartAsync();
        var agentSocket = await ConnectFakeAgentAsync(harness, "fake-agent-disconnect");
        var agentConnection = harness.Hub.Agents.Single(a => a.Info.Name == "fake-agent-disconnect");

        var (manager, router, mediaSourceManager, _, _) = BuildManager(harness);

        var targetDir = harness.Root.CreateSubdirectory("disconnect");
        var jobId = Guid.NewGuid().ToString("N");
        const string Prefix = "disconnect";
        var outputPath = Path.Combine(targetDir, Prefix + ".m3u8");
        var token = harness.TokenStore.Issue(jobId, targetDir, Prefix);
        var spec = new RemoteJobSpec(jobId, ["-f", "hls", "-y", Prefix + ".m3u8"], token, "integration disconnect job");
        router.PlanToReturn = new RoutePlan(agentConnection, spec, targetDir, Prefix, "integration test plan");

        var state = new StreamStateBuilder()
            .WithMediaSourceManager(mediaSourceManager)
            .WithTranscodeManager(manager)
            .Build();
        using var cts = new CancellationTokenSource();

        var agentDriverTask = Task.Run(async () =>
        {
            var jobFrame = Assert.IsType<JobFrame>(Frame.Parse(await HandshakeTests.ReceiveAsync(agentSocket)));
            Assert.Equal(jobId, jobFrame.Id);
            await HandshakeTests.SendAsync(agentSocket, Frame.Serialize(new StartedFrame(jobId, 9999)));
            await HandshakeTests.SendAsync(agentSocket, Frame.Serialize(new StderrFrame(jobId, "frame=1 fps=30"))); // see the class-level remarks

            using var putClient = new HttpClient();
            await PutAsync(putClient, harness.HttpBaseUrl, jobId, Prefix + ".m3u8", token, "#EXTM3U\n");
        });

        var job = await manager.StartFfMpeg(state, outputPath, "-f hls -y " + outputPath, Guid.Empty, TranscodingJobType.Hls, cts);
        await agentDriverTask.WaitAsync(Timeout);

        Assert.False(job.HasExited);

        // Simulate the agent process dying (as opposed to a graceful close - PROTOCOL.md's "agent socket
        // lost -> server marks all its jobs exited" applies to any connection loss, not just clean ones).
        // This is also the harness's own cleanup for this socket - an aborted socket needs no CloseAsync.
        agentSocket.Abort();
        agentSocket.Dispose();

        await Waiting.UntilAsync(() => job.HasExited, because: "the agent connection dropping should mark the job exited (code -1)");
        Assert.Equal(-1, job.ExitCode);
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
