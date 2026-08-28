using Jellyfin.Plugin.Cluster.Contracts;
using Jellyfin.Plugin.Cluster.Transcoding;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Cluster.Tests.Transcoding;

public class JobRouterTests
{
    private static StreamState CreateStreamState(string mediaPath, MediaProtocol protocol = MediaProtocol.File)
    {
        var state = new StreamState(new FakeMediaSourceManager(), TranscodingJobType.Hls, new FakeTranscodeManagerForStreamState())
        {
            Request = new StreamingRequestDto
            {
                DeviceId = "device-1",
                PlaySessionId = "play-session-1",
                MediaSourceId = "media-source-1",
            },
            MediaSource = new MediaSourceInfo
            {
                Id = "media-source-1",
                Path = mediaPath,
                Protocol = protocol,
            },
        };

        return state;
    }

    private static AgentInfo CreateAgentInfo(string name = "trish", IReadOnlyList<string>? hwaccels = null, IReadOnlyList<string>? encoders = null)
    {
        return new AgentInfo(
            name,
            "0.1.0",
            "macos-arm64",
            "/opt/jfc/ffmpeg",
            "7.1.2-Jellyfin",
            hwaccels ?? ["videotoolbox"],
            encoders ?? ["h264_videotoolbox", "aac_at"],
            ["h264", "hevc"],
            ["scale_vt", "volume"],
            [new AgentMount("/Volumes/data", true)],
            3,
            DateTimeOffset.UtcNow);
    }

    private static JobRouter CreateRouter(FakeAgentRegistry registry, FakeIngestTokenStore tokenStore, FakeServerApplicationHost host)
        => new(registry, tokenStore, new FakeMediaEncoder(), host, NullLogger<JobRouter>.Instance);

    [Fact]
    public void TryPlan_RoutableCommandWithAvailableAgent_ReturnsPlan()
    {
        var registry = new FakeAgentRegistry { AgentToReturn = new FakeAgentConnection(CreateAgentInfo()) };
        var tokenStore = new FakeIngestTokenStore { TokenToReturn = "tok-123" };
        var host = new FakeServerApplicationHost { UrlToReturn = "http://10.240.0.1:8096" };
        var router = CreateRouter(registry, tokenStore, host);
        var state = CreateStreamState(Fixtures.InputPath);
        var outputPath = Fixtures.TranscodesDir + "/" + Fixtures.Md5 + ".m3u8";

        var plan = router.TryPlan(state, outputPath, Fixtures.TranscodeCommandLine, TranscodingJobType.Hls);

        Assert.NotNull(plan);
        Assert.Same(registry.AgentToReturn, plan!.Agent);
        Assert.Equal(Fixtures.TranscodesDir, plan.TargetDirectory);
        Assert.Equal(Fixtures.Md5, plan.FilePrefix);
        Assert.Equal("tok-123", plan.Spec.IngestToken);
        Assert.Equal($"Transcode {Fixtures.Md5}", plan.Spec.Label);
        Assert.Contains($"http://10.240.0.1:8096/Cluster/ingest/{plan.Spec.Id}/{Fixtures.Md5}.m3u8", plan.Spec.Argv);
    }

    [Fact]
    public void TryPlan_IssuesTokenForTheOutputDirectoryAndPrefix()
    {
        var registry = new FakeAgentRegistry { AgentToReturn = new FakeAgentConnection(CreateAgentInfo()) };
        var tokenStore = new FakeIngestTokenStore();
        var host = new FakeServerApplicationHost();
        var router = CreateRouter(registry, tokenStore, host);
        var state = CreateStreamState(Fixtures.InputPath);
        var outputPath = Fixtures.TranscodesDir + "/" + Fixtures.Md5 + ".m3u8";

        var plan = router.TryPlan(state, outputPath, Fixtures.TranscodeCommandLine, TranscodingJobType.Hls);

        Assert.NotNull(plan);
        var issued = Assert.Single(tokenStore.Issued);
        Assert.Equal(plan!.Spec.Id, issued.JobId);
        Assert.Equal(Fixtures.TranscodesDir, issued.TargetDirectory);
        Assert.Equal(Fixtures.Md5, issued.FilePrefix);
    }

    [Fact]
    public void TryPlan_PassesRequirementsFromTheCommandLineToTheRegistry()
    {
        var registry = new FakeAgentRegistry { AgentToReturn = new FakeAgentConnection(CreateAgentInfo()) };
        var router = CreateRouter(registry, new FakeIngestTokenStore(), new FakeServerApplicationHost());
        var state = CreateStreamState(Fixtures.InputPath);

        router.TryPlan(state, Fixtures.TranscodesDir + "/" + Fixtures.Md5 + ".m3u8", Fixtures.TranscodeCommandLine, TranscodingJobType.Hls);

        Assert.NotNull(registry.LastRequirements);
        Assert.Equal(["videotoolbox"], registry.LastRequirements!.Hwaccels);
        Assert.Equal(["h264_videotoolbox", "aac_at"], registry.LastRequirements.Encoders);
    }

    [Fact]
    public void TryPlan_NoAgentAvailable_ReturnsNullAndDoesNotIssueAToken()
    {
        var registry = new FakeAgentRegistry { AgentToReturn = null };
        var tokenStore = new FakeIngestTokenStore();
        var router = CreateRouter(registry, tokenStore, new FakeServerApplicationHost());
        var state = CreateStreamState(Fixtures.InputPath);

        var plan = router.TryPlan(state, Fixtures.TranscodesDir + "/" + Fixtures.Md5 + ".m3u8", Fixtures.TranscodeCommandLine, TranscodingJobType.Hls);

        Assert.Null(plan);
        Assert.Empty(tokenStore.Issued);
    }

    [Fact]
    public void TryPlan_NotRoutableCommandLine_ReturnsNull()
    {
        var registry = new FakeAgentRegistry { AgentToReturn = new FakeAgentConnection(CreateAgentInfo()) };
        var router = CreateRouter(registry, new FakeIngestTokenStore(), new FakeServerApplicationHost());
        var state = CreateStreamState(Fixtures.InputPath);

        var plan = router.TryPlan(state, Fixtures.TranscodesDir + "/" + Fixtures.Md5 + ".mp4", Fixtures.ProgressiveCommandLine, TranscodingJobType.Progressive);

        Assert.Null(plan);
    }

    [Fact]
    public void TryPlan_NonFileMediaProtocol_ReturnsNull()
    {
        var registry = new FakeAgentRegistry { AgentToReturn = new FakeAgentConnection(CreateAgentInfo()) };
        var router = CreateRouter(registry, new FakeIngestTokenStore(), new FakeServerApplicationHost());
        var state = CreateStreamState("http://example.com/video.mkv", MediaProtocol.Http);

        var plan = router.TryPlan(state, Fixtures.TranscodesDir + "/" + Fixtures.Md5 + ".m3u8", Fixtures.TranscodeCommandLine, TranscodingJobType.Hls);

        Assert.Null(plan);
    }

    [Fact]
    public void TryPlan_MalformedCommandLine_NeverThrows()
    {
        var registry = new FakeAgentRegistry { AgentToReturn = new FakeAgentConnection(CreateAgentInfo()) };
        var router = CreateRouter(registry, new FakeIngestTokenStore(), new FakeServerApplicationHost());
        var state = CreateStreamState(Fixtures.InputPath);

        // Dangling quote, dangling flag with no value, empty string - none of this should throw.
        var plan1 = router.TryPlan(state, "/out/x.m3u8", "-i file:\"/unterminated", TranscodingJobType.Hls);
        var plan2 = router.TryPlan(state, "/out/x.m3u8", "-hls_segment_filename", TranscodingJobType.Hls);
        var plan3 = router.TryPlan(state, "/out/x.m3u8", string.Empty, TranscodingJobType.Hls);

        Assert.Null(plan1);
        Assert.Null(plan2);
        Assert.Null(plan3);
    }

    [Fact]
    public void TryPlan_EmptyIngestBaseUrlConfigured_FallsBackToApplicationHost()
    {
        var registry = new FakeAgentRegistry { AgentToReturn = new FakeAgentConnection(CreateAgentInfo()) };
        var host = new FakeServerApplicationHost { UrlToReturn = "http://fallback.local:8096" };
        var router = CreateRouter(registry, new FakeIngestTokenStore(), host);
        var state = CreateStreamState(Fixtures.InputPath);

        // Plugin.Instance is null in this test host (no real plugin loaded), so IngestBaseUrl is
        // never configured - TryPlan must fall back to IServerApplicationHost.GetApiUrlForLocalAccess.
        var plan = router.TryPlan(state, Fixtures.TranscodesDir + "/" + Fixtures.Md5 + ".m3u8", Fixtures.TranscodeCommandLine, TranscodingJobType.Hls);

        Assert.NotNull(plan);
        Assert.NotNull(host.LastGetApiUrlForLocalAccessCall);
        Assert.Null(host.LastGetApiUrlForLocalAccessCall!.Value.IpAddress);
        Assert.False(host.LastGetApiUrlForLocalAccessCall!.Value.AllowHttps);
        Assert.Contains("http://fallback.local:8096/Cluster/ingest/", plan!.Spec.Argv[^1]);
    }
}
