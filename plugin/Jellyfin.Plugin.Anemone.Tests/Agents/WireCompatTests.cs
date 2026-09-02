using System.Text.Json;
using Jellyfin.Plugin.Anemone.Agents.Protocol;

namespace Jellyfin.Plugin.Anemone.Tests.Agents;

/// <summary>
/// Cross-implementation contract: the literal JSON here is what the Rust polyp (agent/src/protocol.rs)
/// emits and accepts (PROTOCOL.md examples + the strings asserted in its own tests). If either side drifts,
/// this is where it shows up before the live test does.
/// </summary>
public class WireCompatTests
{
    private const string RustHello = """
        {"type":"hello","name":"trish","version":"0.1.0","platform":"macos-arm64",
         "ffmpeg":{"path":"/opt/anemone/ffmpeg","version":"7.1.2-Jellyfin","hwaccels":["videotoolbox"],
                   "encoders":["h264_videotoolbox","hevc_videotoolbox","aac_at","libx264"],"decoders":["h264","hevc"],
                   "filters":["scale_vt","scale","overlay"]},
         "mounts":[{"path":"/Volumes/data","ok":true}],"max_sessions":3}
        """;

    [Fact]
    public void ParsesRustHello()
    {
        var f = Assert.IsType<HelloFrame>(Frame.Parse(RustHello));
        Assert.Equal("trish", f.Name);
        Assert.Equal("macos-arm64", f.Platform);
        Assert.Equal("7.1.2-Jellyfin", f.Ffmpeg.Version);
        Assert.Equal("/opt/anemone/ffmpeg", f.Ffmpeg.Path);
        Assert.Contains("videotoolbox", f.Ffmpeg.Hwaccels!);
        Assert.Contains("aac_at", f.Ffmpeg.Encoders!);
        Assert.Equal(3, f.MaxSessions);
        Assert.Single(f.Mounts!);
        Assert.True(f.Mounts![0].Ok);
        Assert.Equal("/Volumes/data", f.Mounts[0].Path);
    }

    private const string RustHelloWithHwaccelAndServerPath = """
        {"type":"hello","name":"linux-box","version":"0.2.0","platform":"linux-x86_64",
         "ffmpeg":{"path":"/opt/anemone/ffmpeg","version":"7.1.2-Jellyfin","hwaccels":["vaapi"],
                   "encoders":["h264_vaapi","hevc_vaapi","aac"],"decoders":["h264","hevc"],
                   "filters":["scale_vaapi","volume"]},
         "mounts":[{"path":"/mnt/media","ok":true,"server_path":"/Volumes/data"}],"max_sessions":2,
         "hwaccel":"vaapi","hwaccel_device":"/dev/dri/renderD128"}
        """;

    [Fact]
    public void ParsesRustHello_WithHwaccelAndMountServerPath()
    {
        var f = Assert.IsType<HelloFrame>(Frame.Parse(RustHelloWithHwaccelAndServerPath));

        Assert.Equal("vaapi", f.Hwaccel);
        Assert.Equal("/dev/dri/renderD128", f.HwaccelDevice);
        Assert.Single(f.Mounts!);
        Assert.Equal("/mnt/media", f.Mounts![0].Path);
        Assert.Equal("/Volumes/data", f.Mounts[0].ServerPath);
    }

    [Theory]
    [InlineData("""{"type":"status","active":2}""")]
    [InlineData("""{"type":"status","active":1,"load":0.5,"mounts":[{"path":"/Volumes/data","ok":false}]}""")]
    public void ParsesRustStatus(string json)
    {
        var f = Assert.IsType<StatusFrame>(Frame.Parse(json));
        Assert.True(f.Active is 1 or 2);
    }

    [Fact]
    public void ParsesRustStartedStderrExitErrorPong()
    {
        Assert.Equal(4242, Assert.IsType<StartedFrame>(Frame.Parse("""{"type":"started","id":"5f1c","pid":4242}""")).Pid);
        Assert.Equal("frame=  120 fps= 60", Assert.IsType<StderrFrame>(Frame.Parse("""{"type":"stderr","id":"5f1c","line":"frame=  120 fps= 60"}""")).Line);
        var exit0 = Assert.IsType<ExitFrame>(Frame.Parse("""{"type":"exit","id":"5f1c","code":0}"""));
        Assert.Equal(0, exit0.Code);
        Assert.Null(exit0.Error);
        var exitSig = Assert.IsType<ExitFrame>(Frame.Parse("""{"type":"exit","id":"5f1c","code":-1,"error":"killed by signal 9 (SIGKILL)"}"""));
        Assert.Equal(-1, exitSig.Code);
        Assert.Contains("SIGKILL", exitSig.Error);
        var err = Assert.IsType<ErrorFrame>(Frame.Parse("""{"type":"error","message":"generic problem"}"""));
        Assert.Null(err.Id);
        Assert.IsType<PongFrame>(Frame.Parse("""{"type":"pong"}"""));
        Assert.IsType<UnknownFrame>(Frame.Parse("""{"type":"frobnicate","foo":"bar"}"""));
    }

    [Fact]
    public void SerializesWelcomeTheWayRustExpects()
    {
        var json = Frame.Serialize(new WelcomeFrame(new ServerInfo("10.11.0", "7.1.2-Jellyfin"), "http://10.240.0.1:8096", 10));
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        Assert.Equal("welcome", r.GetProperty("type").GetString());
        Assert.Equal("http://10.240.0.1:8096", r.GetProperty("ingest_base").GetString());
        Assert.Equal(10, r.GetProperty("ping_interval_s").GetInt32());
        Assert.Equal("10.11.0", r.GetProperty("server").GetProperty("version").GetString());
        Assert.Equal("7.1.2-Jellyfin", r.GetProperty("server").GetProperty("ffmpeg_version").GetString());
    }

    [Fact]
    public void SerializesJobStdinKillPingTheWayRustExpects()
    {
        var job = Frame.Serialize(new JobFrame("5f1c", ["-f", "hls", "-headers", "Authorization: Bearer 9kQ\r\n"], "9kQ", "Transcode a7858c"));
        using (var doc = JsonDocument.Parse(job))
        {
            var r = doc.RootElement;
            Assert.Equal("job", r.GetProperty("type").GetString());
            Assert.Equal("5f1c", r.GetProperty("id").GetString());
            Assert.Equal("9kQ", r.GetProperty("token").GetString());
            Assert.Equal("Transcode a7858c", r.GetProperty("label").GetString());
            Assert.Equal("Authorization: Bearer 9kQ\r\n", r.GetProperty("argv")[3].GetString());
            // env is optional on the Rust side (Option<HashMap>); null or absent are both accepted
            if (r.TryGetProperty("env", out var env))
            {
                Assert.Equal(JsonValueKind.Null, env.ValueKind);
            }
        }

        var stdin = Frame.Serialize(new StdinFrame("5f1c", "q\n"));
        using (var doc = JsonDocument.Parse(stdin))
        {
            Assert.Equal("stdin", doc.RootElement.GetProperty("type").GetString());
            Assert.Equal("q\n", doc.RootElement.GetProperty("data").GetString());
        }

        Assert.Equal("kill", JsonDocument.Parse(Frame.Serialize(new KillFrame("5f1c"))).RootElement.GetProperty("type").GetString());
        Assert.Equal("""{"type":"ping"}""", Frame.Serialize(new PingFrame()));
    }
}
