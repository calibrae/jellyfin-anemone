using Jellyfin.Plugin.Anemone.Agents.Protocol;

namespace Jellyfin.Plugin.Anemone.Tests.Agents;

public class FrameTests
{
    [Fact]
    public void Hello_RoundTrips_WithSnakeCaseFields()
    {
        var frame = new HelloFrame(
            "trish",
            "0.1.0",
            "macos-arm64",
            new FfmpegInfoFrame(
                "/opt/anemone/ffmpeg",
                "7.1.2-Jellyfin",
                ["videotoolbox"],
                ["h264_videotoolbox", "hevc_videotoolbox", "aac_at", "libx264"],
                ["h264", "hevc"],
                ["scale_vt", "scale", "overlay"]),
            [new AgentMountFrame("/Volumes/data", true)],
            3);

        var json = Frame.Serialize(frame);

        Assert.Contains("\"type\":\"hello\"", json, StringComparison.Ordinal);
        Assert.Contains("\"max_sessions\":3", json, StringComparison.Ordinal);
        Assert.Contains("\"hwaccels\":", json, StringComparison.Ordinal);

        var parsed = Assert.IsType<HelloFrame>(Frame.Parse(json));
        Assert.Equal("trish", parsed.Name);
        Assert.Equal("0.1.0", parsed.Version);
        Assert.Equal("macos-arm64", parsed.Platform);
        Assert.Equal(3, parsed.MaxSessions);
        Assert.Equal("/opt/anemone/ffmpeg", parsed.Ffmpeg.Path);
        Assert.Equal("7.1.2-Jellyfin", parsed.Ffmpeg.Version);
        Assert.Equal(["videotoolbox"], parsed.Ffmpeg.Hwaccels);
        Assert.Equal(["h264_videotoolbox", "hevc_videotoolbox", "aac_at", "libx264"], parsed.Ffmpeg.Encoders);
        Assert.Equal(["h264", "hevc"], parsed.Ffmpeg.Decoders);
        Assert.Equal(["scale_vt", "scale", "overlay"], parsed.Ffmpeg.Filters);
        Assert.Single(parsed.Mounts!);
        Assert.Equal("/Volumes/data", parsed.Mounts![0].Path);
        Assert.True(parsed.Mounts[0].Ok);
    }

    [Fact]
    public void Status_RoundTrips_WithOptionalFieldsOmitted()
    {
        var frame = new StatusFrame(2);
        var json = Frame.Serialize(frame);

        Assert.Contains("\"type\":\"status\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"load\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"mounts\"", json, StringComparison.Ordinal);

        var parsed = Assert.IsType<StatusFrame>(Frame.Parse(json));
        Assert.Equal(2, parsed.Active);
        Assert.Null(parsed.Load);
        Assert.Null(parsed.Mounts);
    }

    [Fact]
    public void Status_RoundTrips_WithOptionalFieldsPresent()
    {
        var frame = new StatusFrame(1, 0.5, [new AgentMountFrame("/Volumes/data", true), new AgentMountFrame("/Volumes/broken", false)]);
        var json = Frame.Serialize(frame);

        var parsed = Assert.IsType<StatusFrame>(Frame.Parse(json));
        Assert.Equal(1, parsed.Active);
        Assert.Equal(0.5, parsed.Load);
        Assert.Equal(2, parsed.Mounts!.Count);
        Assert.False(parsed.Mounts[1].Ok);
    }

    [Fact]
    public void Started_RoundTrips()
    {
        var json = Frame.Serialize(new StartedFrame("job-1", 4242));
        Assert.Contains("\"type\":\"started\"", json, StringComparison.Ordinal);

        var parsed = Assert.IsType<StartedFrame>(Frame.Parse(json));
        Assert.Equal("job-1", parsed.Id);
        Assert.Equal(4242, parsed.Pid);
    }

    [Fact]
    public void Stderr_RoundTrips_VerbatimNoTrailingNewline()
    {
        const string Line = "frame=  120 fps= 60 q=-0.0 size=    1024KiB time=00:00:04.00 bitrate=2097.2kbits/s speed=2.0x";
        var json = Frame.Serialize(new StderrFrame("job-1", Line));

        var parsed = Assert.IsType<StderrFrame>(Frame.Parse(json));
        Assert.Equal("job-1", parsed.Id);
        Assert.Equal(Line, parsed.Line);
        Assert.DoesNotContain('\n', parsed.Line);
    }

    [Fact]
    public void Exit_RoundTrips_WithAndWithoutError()
    {
        var clean = Assert.IsType<ExitFrame>(Frame.Parse(Frame.Serialize(new ExitFrame("job-1", 0))));
        Assert.Equal(0, clean.Code);
        Assert.Null(clean.Error);

        var killed = Assert.IsType<ExitFrame>(Frame.Parse(Frame.Serialize(new ExitFrame("job-1", -1, "capacity"))));
        Assert.Equal(-1, killed.Code);
        Assert.Equal("capacity", killed.Error);
    }

    [Fact]
    public void Error_RoundTrips_WithAndWithoutId()
    {
        var scoped = Assert.IsType<ErrorFrame>(Frame.Parse(Frame.Serialize(new ErrorFrame("spawn failed", "job-1"))));
        Assert.Equal("job-1", scoped.Id);
        Assert.Equal("spawn failed", scoped.Message);

        var unscoped = Assert.IsType<ErrorFrame>(Frame.Parse(Frame.Serialize(new ErrorFrame("disk full"))));
        Assert.Null(unscoped.Id);
        Assert.Equal("disk full", unscoped.Message);
    }

    [Fact]
    public void Pong_And_Ping_RoundTrip()
    {
        Assert.IsType<PongFrame>(Frame.Parse(Frame.Serialize(new PongFrame())));
        Assert.IsType<PingFrame>(Frame.Parse(Frame.Serialize(new PingFrame())));
    }

    [Fact]
    public void Welcome_RoundTrips_WithSnakeCaseFields()
    {
        var frame = new WelcomeFrame(new ServerInfo("10.11.0", "7.1.2-Jellyfin"), "http://10.240.0.1:8096", 10);
        var json = Frame.Serialize(frame);

        Assert.Contains("\"ingest_base\":\"http://10.240.0.1:8096\"", json, StringComparison.Ordinal);
        Assert.Contains("\"ping_interval_s\":10", json, StringComparison.Ordinal);
        Assert.Contains("\"ffmpeg_version\":\"7.1.2-Jellyfin\"", json, StringComparison.Ordinal);

        var parsed = Assert.IsType<WelcomeFrame>(Frame.Parse(json));
        Assert.Equal("10.11.0", parsed.Server.Version);
        Assert.Equal("7.1.2-Jellyfin", parsed.Server.FfmpegVersion);
        Assert.Equal("http://10.240.0.1:8096", parsed.IngestBase);
        Assert.Equal(10, parsed.PingIntervalS);
    }

    [Fact]
    public void Reject_RoundTrips()
    {
        var parsed = Assert.IsType<RejectFrame>(Frame.Parse(Frame.Serialize(new RejectFrame("name and ffmpeg.version are required"))));
        Assert.Equal("name and ffmpeg.version are required", parsed.Reason);
    }

    [Fact]
    public void Job_RoundTrips_WithEnvOmittedWhenNull()
    {
        var frame = new JobFrame("job-1", ["-i", "file:/x.mkv"], "tok123", "Transcode x");
        var json = Frame.Serialize(frame);

        Assert.DoesNotContain("\"env\"", json, StringComparison.Ordinal);

        var parsed = Assert.IsType<JobFrame>(Frame.Parse(json));
        Assert.Equal("job-1", parsed.Id);
        Assert.Equal(["-i", "file:/x.mkv"], parsed.Argv);
        Assert.Equal("tok123", parsed.Token);
        Assert.Equal("Transcode x", parsed.Label);
        Assert.Null(parsed.Env);
    }

    [Fact]
    public void Job_RoundTrips_WithEnvPresent()
    {
        var env = new Dictionary<string, string> { ["ANEMONE_JOB_ID"] = "job-1" };
        var frame = new JobFrame("job-1", ["-i", "x"], "tok", "label", env);
        var parsed = Assert.IsType<JobFrame>(Frame.Parse(Frame.Serialize(frame)));

        Assert.NotNull(parsed.Env);
        Assert.Equal("job-1", parsed.Env!["ANEMONE_JOB_ID"]);
    }

    [Fact]
    public void Job_Argv_SurvivesEmbeddedCrLf()
    {
        // The -headers argv element carries a literal CRLF (see PROTOCOL.md "Argument rewriting" step 3).
        var headerValue = "Authorization: Bearer 9kQabc\r\n";
        var frame = new JobFrame("job-1", ["-headers", headerValue], "tok", "label");

        var parsed = Assert.IsType<JobFrame>(Frame.Parse(Frame.Serialize(frame)));

        Assert.Equal(headerValue, parsed.Argv[1]);
        Assert.EndsWith("\r\n", parsed.Argv[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Stdin_RoundTrips()
    {
        var parsed = Assert.IsType<StdinFrame>(Frame.Parse(Frame.Serialize(new StdinFrame("job-1", "q\n"))));
        Assert.Equal("job-1", parsed.Id);
        Assert.Equal("q\n", parsed.Data);
    }

    [Fact]
    public void Kill_RoundTrips()
    {
        var parsed = Assert.IsType<KillFrame>(Frame.Parse(Frame.Serialize(new KillFrame("job-1"))));
        Assert.Equal("job-1", parsed.Id);
    }

    [Fact]
    public void Parse_UnknownType_ReturnsUnknownFrame()
    {
        var parsed = Assert.IsType<UnknownFrame>(Frame.Parse("{\"type\":\"frobnicate\",\"x\":1}"));
        Assert.Equal("frobnicate", parsed.Type);
    }

    [Fact]
    public void Parse_MissingType_ReturnsUnknownFrame()
    {
        var parsed = Assert.IsType<UnknownFrame>(Frame.Parse("{\"foo\":\"bar\"}"));
        Assert.Equal(string.Empty, parsed.Type);
    }

    [Fact]
    public void Parse_IgnoresUnknownFields()
    {
        var parsed = Assert.IsType<PingFrame>(Frame.Parse("{\"type\":\"ping\",\"surprise\":true}"));
        Assert.Equal("ping", parsed.Type);
    }
}
