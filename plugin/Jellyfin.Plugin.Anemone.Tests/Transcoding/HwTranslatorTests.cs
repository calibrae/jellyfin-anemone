using Jellyfin.Plugin.Anemone.Contracts;
using Jellyfin.Plugin.Anemone.Transcoding;

namespace Jellyfin.Plugin.Anemone.Tests.Transcoding;

/// <summary>
/// Tests for <see cref="HwTranslator"/> against the real captured command lines in <see cref="Fixtures"/>
/// (HwVideotoolbox*/HwRemux*) per PROTOCOL.md "Hardware acceleration". Expected token sequences are built
/// independently (list surgery on the split argv, not by mirroring HwTranslator's own code path) so these
/// tests aren't just re-asserting the implementation against itself.
/// </summary>
public class HwTranslatorTests
{
    private static AgentInfo MakeVideotoolboxAgent(string name = "mac-2")
        => new(
            name, "0.1.0", "macos-arm64", "/opt/anemone/ffmpeg", "7.1.2-Jellyfin",
            ["videotoolbox"], ["h264_videotoolbox", "hevc_videotoolbox", "aac_at", "aac"], ["h264", "hevc"],
            ["scale_vt", "volume"], [new AgentMount("/Volumes/data", true)],
            3, DateTimeOffset.UtcNow, "videotoolbox", null);

    private static AgentInfo MakeVaapiAgent(
        string? device = "/dev/dri/renderD128",
        IReadOnlyList<string>? encoders = null,
        IReadOnlyList<string>? filters = null,
        IReadOnlyList<string>? hwaccels = null)
        => new(
            "linux-vaapi", "0.1.0", "linux-x86_64", "/opt/anemone/ffmpeg", "7.1.2-Jellyfin",
            hwaccels ?? ["vaapi"], encoders ?? ["h264_vaapi", "hevc_vaapi", "aac"], ["h264", "hevc"],
            filters ?? ["scale_vaapi", "volume", "format"], [new AgentMount("/Volumes/data", true)],
            3, DateTimeOffset.UtcNow, "vaapi", device);

    private static AgentInfo MakeNvencAgent(
        IReadOnlyList<string>? encoders = null,
        IReadOnlyList<string>? filters = null,
        IReadOnlyList<string>? hwaccels = null)
        => new(
            "linux-nvenc", "0.1.0", "linux-x86_64", "/opt/anemone/ffmpeg", "7.1.2-Jellyfin",
            hwaccels ?? ["cuda"], encoders ?? ["h264_nvenc", "hevc_nvenc", "aac"], ["h264", "hevc"],
            filters ?? ["scale_cuda", "volume"], [new AgentMount("/Volumes/data", true)],
            3, DateTimeOffset.UtcNow, "nvenc", null);

    private static AgentInfo MakeQsvAgent(string? device = "/dev/dri/renderD129")
        => new(
            "linux-qsv", "0.1.0", "linux-x86_64", "/opt/anemone/ffmpeg", "7.1.2-Jellyfin",
            ["qsv"], ["h264_qsv", "hevc_qsv", "aac"], ["h264", "hevc"],
            ["scale_qsv", "volume"], [new AgentMount("/Volumes/data", true)],
            3, DateTimeOffset.UtcNow, "qsv", device);

    private static AgentInfo MakeNoneAgent(IReadOnlyList<string>? encoders = null, IReadOnlyList<string>? filters = null)
        => new(
            "cpu-only", "0.1.0", "linux-x86_64", "/opt/anemone/ffmpeg", "7.1.2-Jellyfin",
            [], encoders ?? ["libx264", "libx265", "aac"], ["h264", "hevc"],
            filters ?? ["scale", "volume"], [new AgentMount("/Volumes/data", true)],
            3, DateTimeOffset.UtcNow, "none", null);

    // --- Identity (source profile == agent profile) ---

    [Fact]
    public void TryTranslate_IdentitySameProfile_ArgvByteIdenticalAndSameReference()
    {
        var argv = ArgumentLine.Split(Fixtures.HwVideotoolboxCommandLine);
        var agent = MakeVideotoolboxAgent();

        var ok = HwTranslator.TryTranslate(argv, agent, out var translated, out var reason);

        Assert.True(ok, reason);
        Assert.Same(argv, translated);
    }

    [Fact]
    public void TryTranslate_RemuxFixture_IdentitySameProfile_ArgvUnchanged()
    {
        var argv = ArgumentLine.Split(Fixtures.HwRemuxCommandLine);
        var agent = MakeVideotoolboxAgent();

        var ok = HwTranslator.TryTranslate(argv, agent, out var translated, out var reason);

        Assert.True(ok, reason);
        Assert.Same(argv, translated);
    }

    // --- videotoolbox -> {none, vaapi, nvenc}: exact expected token sequence ---

    [Fact]
    public void TryTranslate_Fixture1_VideotoolboxToNone_ProducesExpectedSequence()
    {
        var argv = ArgumentLine.Split(Fixtures.HwVideotoolboxCommandLine);
        var agent = MakeNoneAgent();

        var ok = HwTranslator.TryTranslate(argv, agent, out var translated, out var reason);
        Assert.True(ok, reason);

        var expected = new List<string>(argv);
        var deviceInitIndex = expected.IndexOf("-init_hw_device");
        expected.RemoveRange(deviceInitIndex, 6); // -init_hw_device X -hwaccel X -hwaccel_output_format X, all removed
        expected[expected.IndexOf("-codec:v:0") + 1] = "libx264";
        expected[expected.IndexOf("-vf") + 1] = "scale=w=640:h=360";
        expected.RemoveRange(expected.IndexOf("-prio_speed"), 2);

        Assert.Equal(expected, translated);
    }

    [Fact]
    public void TryTranslate_Fixture1_VideotoolboxToVaapi_ProducesExpectedSequence()
    {
        var argv = ArgumentLine.Split(Fixtures.HwVideotoolboxCommandLine);
        var agent = MakeVaapiAgent();

        var ok = HwTranslator.TryTranslate(argv, agent, out var translated, out var reason);
        Assert.True(ok, reason);

        var expected = new List<string>(argv);
        var deviceInitIndex = expected.IndexOf("-init_hw_device");
        expected.RemoveRange(deviceInitIndex, 6);
        expected.InsertRange(
            deviceInitIndex,
            ["-init_hw_device", "vaapi=va:/dev/dri/renderD128", "-hwaccel", "vaapi", "-hwaccel_output_format", "vaapi"]);
        expected[expected.IndexOf("-codec:v:0") + 1] = "h264_vaapi";
        expected[expected.IndexOf("-vf") + 1] = "scale_vaapi=w=640:h=360";
        expected.RemoveRange(expected.IndexOf("-prio_speed"), 2);

        Assert.Equal(expected, translated);
    }

    [Fact]
    public void TryTranslate_Fixture1_VideotoolboxToNvenc_ProducesExpectedSequence()
    {
        var argv = ArgumentLine.Split(Fixtures.HwVideotoolboxCommandLine);
        var agent = MakeNvencAgent();

        var ok = HwTranslator.TryTranslate(argv, agent, out var translated, out var reason);
        Assert.True(ok, reason);

        var expected = new List<string>(argv);
        var deviceInitIndex = expected.IndexOf("-init_hw_device");
        expected.RemoveRange(deviceInitIndex, 6);
        expected.InsertRange(deviceInitIndex, ["-hwaccel", "cuda", "-hwaccel_output_format", "cuda"]); // no -init_hw_device for nvenc
        expected[expected.IndexOf("-codec:v:0") + 1] = "h264_nvenc";
        expected[expected.IndexOf("-vf") + 1] = "scale_cuda=w=640:h=360";
        expected.RemoveRange(expected.IndexOf("-prio_speed"), 2);

        Assert.Equal(expected, translated);
    }

    [Fact]
    public void TryTranslate_Fixture1_VideotoolboxToQsv_ProducesExpectedSequence()
    {
        var argv = ArgumentLine.Split(Fixtures.HwVideotoolboxCommandLine);
        var agent = MakeQsvAgent();

        var ok = HwTranslator.TryTranslate(argv, agent, out var translated, out var reason);
        Assert.True(ok, reason);

        var expected = new List<string>(argv);
        var deviceInitIndex = expected.IndexOf("-init_hw_device");
        expected.RemoveRange(deviceInitIndex, 6);
        expected.InsertRange(
            deviceInitIndex,
            ["-init_hw_device", "qsv=qs:/dev/dri/renderD129", "-hwaccel", "qsv", "-hwaccel_output_format", "qsv"]);
        expected[expected.IndexOf("-codec:v:0") + 1] = "h264_qsv";
        expected[expected.IndexOf("-vf") + 1] = "scale_qsv=w=640:h=360";
        expected.RemoveRange(expected.IndexOf("-prio_speed"), 2);

        Assert.Equal(expected, translated);
    }

    // --- Fixture 2: aac_at mapping, format= param dropped, profile/level/-af preserved untouched ---

    [Fact]
    public void TryTranslate_HigherResFixture_MapsAacAtAndDropsFormatParam()
    {
        var argv = ArgumentLine.Split(Fixtures.HwVideotoolboxHigherResCommandLine);
        var agent = MakeVaapiAgent();

        var ok = HwTranslator.TryTranslate(argv, agent, out var translated, out var reason);
        Assert.True(ok, reason);

        var expected = new List<string>(argv);
        var deviceInitIndex = expected.IndexOf("-init_hw_device");
        expected.RemoveRange(deviceInitIndex, 6);
        expected.InsertRange(
            deviceInitIndex,
            ["-init_hw_device", "vaapi=va:/dev/dri/renderD128", "-hwaccel", "vaapi", "-hwaccel_output_format", "vaapi"]);
        expected[expected.IndexOf("-codec:v:0") + 1] = "h264_vaapi";
        expected[expected.IndexOf("-vf") + 1] = "scale_vaapi=w=1280:h=640"; // format=nv12 dropped, only w/h kept
        expected[expected.IndexOf("-codec:a:0") + 1] = "aac"; // aac_at -> aac
        expected.RemoveRange(expected.IndexOf("-prio_speed"), 2);

        Assert.Equal(expected, translated);

        // -profile:v:0/-level and -af volume=2 are untouched tokens - already proven by the full sequence
        // equality above, but assert them directly too since they're the point of this fixture.
        Assert.Contains("-profile:v:0", translated);
        Assert.Contains("volume=2", translated);
    }

    // --- Fixture 3 (remux): no video translation, audio still mapped ---

    [Fact]
    public void TryTranslate_RemuxFixture_ToVaapi_NoVideoTranslationOnlyAudioMapped()
    {
        var argv = ArgumentLine.Split(Fixtures.HwRemuxCommandLine);
        var agent = MakeVaapiAgent();

        var ok = HwTranslator.TryTranslate(argv, agent, out var translated, out var reason);
        Assert.True(ok, reason);

        // "-codec:v:0 copy" needs no video translation: the stream is copied, so no encoder or scaler is
        // rewritten and the bitstream filter is untouched. The videotoolbox hwaccel-init tokens ARE
        // removed though - nothing consumes them in a copy, and ffmpeg on a box without VideoToolbox
        // fails outright on "-hwaccel videotoolbox" rather than ignoring it.
        var expected = new List<string>(argv);
        expected[expected.IndexOf("-codec:a:0") + 1] = "aac";
        foreach (var flag in new[] { "-init_hw_device", "-hwaccel", "-hwaccel_output_format" })
        {
            int at;
            while ((at = expected.IndexOf(flag)) >= 0)
            {
                expected.RemoveRange(at, 2);
            }
        }

        Assert.Equal(expected, translated);
        Assert.DoesNotContain("videotoolbox", translated);
        Assert.Contains("h264_mp4toannexb", translated);
    }

    [Fact]
    public void TryTranslate_RemuxFixture_ToNone_VideoCodecStaysCopy()
    {
        var argv = ArgumentLine.Split(Fixtures.HwRemuxCommandLine);
        var agent = MakeNoneAgent();

        var ok = HwTranslator.TryTranslate(argv, agent, out var translated, out var reason);
        Assert.True(ok, reason);

        Assert.Equal("copy", translated[translated.ToList().IndexOf("-codec:v:0") + 1]);
        Assert.Equal("aac", translated[translated.ToList().IndexOf("-codec:a:0") + 1]);
    }

    // --- Refusals: anything not fully modeled ---

    [Fact]
    public void TryTranslate_SubtitleBurnIn_Refuses()
    {
        var argv = ArgumentLine.Split(Fixtures.SubtitleBurnInCommandLine);
        var agent = MakeVaapiAgent();

        var ok = HwTranslator.TryTranslate(argv, agent, out _, out var reason);

        Assert.False(ok);
        Assert.Contains("burn-in", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryTranslate_FilterComplex_Refuses()
    {
        var argv = ArgumentLine.Split("-hwaccel videotoolbox -i file:/x.mkv -filter_complex \"[0:v]scale=100:100[out]\" -codec:v:0 h264_videotoolbox -f hls -y out.m3u8");
        var agent = MakeVaapiAgent();

        var ok = HwTranslator.TryTranslate(argv, agent, out _, out var reason);

        Assert.False(ok);
        Assert.Contains("filter_complex", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("tonemap=linear")]
    [InlineData("zscale=transfer=linear")]
    public void TryTranslate_Tonemapping_Refuses(string filterValue)
    {
        List<string> argv = ["-hwaccel", "videotoolbox", "-i", "file:/x.mkv", "-vf", filterValue, "-codec:v:0", "h264_videotoolbox", "-f", "hls", "-y", "out.m3u8"];
        var agent = MakeVaapiAgent();

        var ok = HwTranslator.TryTranslate(argv, agent, out _, out var reason);

        Assert.False(ok);
        Assert.Contains("tonemap", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryTranslate_UnrecognizedFilter_Refuses()
    {
        List<string> argv = ["-hwaccel", "videotoolbox", "-i", "file:/x.mkv", "-vf", "eq=brightness=0.1", "-codec:v:0", "h264_videotoolbox", "-f", "hls", "-y", "out.m3u8"];
        var agent = MakeVaapiAgent();

        var ok = HwTranslator.TryTranslate(argv, agent, out _, out var reason);

        Assert.False(ok);
        Assert.Contains("unrecognized filter 'eq'", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TryTranslate_ConcatInput_Refuses()
    {
        List<string> argv = ["-hwaccel", "videotoolbox", "-f", "concat", "-safe", "0", "-i", "file:/x.concat", "-codec:v:0", "h264_videotoolbox", "-f", "hls", "-y", "out.m3u8"];
        var agent = MakeVaapiAgent();

        var ok = HwTranslator.TryTranslate(argv, agent, out _, out var reason);

        Assert.False(ok);
        Assert.Contains("concat", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryTranslate_MoreThanOneInput_Refuses()
    {
        List<string> argv = ["-hwaccel", "videotoolbox", "-i", "file:/a.mkv", "-i", "file:/b.mkv", "-codec:v:0", "h264_videotoolbox", "-f", "hls", "-y", "out.m3u8"];
        var agent = MakeVaapiAgent();

        var ok = HwTranslator.TryTranslate(argv, agent, out _, out var reason);

        Assert.False(ok);
        Assert.Contains("one -i", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryTranslate_PreInputHardwareDecoder_NotModeled_Refuses()
    {
        // "-c:v h264_cuvid" before -i is a hw decoder select we don't model at all (PROTOCOL.md's table has
        // no decoder row); source still resolves via the post-input encoder ("nvenc"), so this isn't identity.
        List<string> argv = ["-c:v", "h264_cuvid", "-i", "file:/x.mkv", "-codec:v:0", "h264_nvenc", "-f", "hls", "-y", "out.m3u8"];
        var agent = MakeVaapiAgent();

        var ok = HwTranslator.TryTranslate(argv, agent, out _, out var reason);

        Assert.False(ok);
        Assert.Contains("decoder", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryTranslate_MissingEncoderOnAgent_Refuses()
    {
        var argv = ArgumentLine.Split(Fixtures.HwVideotoolboxCommandLine);
        var agent = MakeVaapiAgent(encoders: ["aac"]); // no h264_vaapi announced

        var ok = HwTranslator.TryTranslate(argv, agent, out _, out var reason);

        Assert.False(ok);
        Assert.Contains("h264_vaapi", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TryTranslate_MissingScaleFilterOnAgent_Refuses()
    {
        var argv = ArgumentLine.Split(Fixtures.HwVideotoolboxCommandLine);
        var agent = MakeVaapiAgent(filters: ["volume"]); // no scale_vaapi announced

        var ok = HwTranslator.TryTranslate(argv, agent, out _, out var reason);

        Assert.False(ok);
        Assert.Contains("scale_vaapi", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TryTranslate_MissingHwaccelOnAgent_Refuses()
    {
        var argv = ArgumentLine.Split(Fixtures.HwVideotoolboxCommandLine);
        var agent = MakeVaapiAgent(hwaccels: []); // agent doesn't actually report vaapi hwaccel support

        var ok = HwTranslator.TryTranslate(argv, agent, out _, out var reason);

        Assert.False(ok);
        Assert.Contains("vaapi", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TryTranslate_MissingVaapiDevice_Refuses()
    {
        var argv = ArgumentLine.Split(Fixtures.HwVideotoolboxCommandLine);
        var agent = MakeVaapiAgent(device: null);

        var ok = HwTranslator.TryTranslate(argv, agent, out _, out var reason);

        Assert.False(ok);
        Assert.Contains("hwaccel_device", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryTranslate_MissingQsvDevice_Refuses()
    {
        var argv = ArgumentLine.Split(Fixtures.HwVideotoolboxCommandLine);
        var agent = MakeQsvAgent(device: null);

        var ok = HwTranslator.TryTranslate(argv, agent, out _, out var reason);

        Assert.False(ok);
        Assert.Contains("hwaccel_device", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("amf")]
    [InlineData("rkmpp")]
    public void TryTranslate_TargetProfileWithNoTableEntry_Refuses(string targetProfile)
    {
        var argv = ArgumentLine.Split(Fixtures.HwVideotoolboxCommandLine);
        var agent = new AgentInfo(
            "weird", "0.1.0", "linux-x86_64", "/opt/anemone/ffmpeg", "7.1.2-Jellyfin",
            [targetProfile], ["h264_" + targetProfile], [], [], [new AgentMount("/Volumes/data", true)],
            3, DateTimeOffset.UtcNow, targetProfile, null);

        var ok = HwTranslator.TryTranslate(argv, agent, out _, out var reason);

        Assert.False(ok);
        Assert.Contains(targetProfile, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TryTranslate_NoTableEntryTarget_ButSourceMatches_StillIdentity()
    {
        // amf/rkmpp aren't in the translation table, but identity (source == target) is checked first and
        // is unconditional - refusing an already-matching agent would be a regression, not a safety win.
        List<string> argv = ["-hwaccel", "amf", "-i", "file:/x.mkv", "-codec:v:0", "h264_amf", "-f", "hls", "-y", "out.m3u8"];
        var agent = new AgentInfo(
            "amf-agent", "0.1.0", "windows-x86_64", "/opt/anemone/ffmpeg", "7.1.2-Jellyfin",
            ["amf"], ["h264_amf"], [], [], [new AgentMount("/Volumes/data", true)],
            3, DateTimeOffset.UtcNow, "amf", null);

        var ok = HwTranslator.TryTranslate(argv, agent, out var translated, out var reason);

        Assert.True(ok, reason);
        Assert.Same(argv, translated);
    }

    // --- InferProfile ---

    [Fact]
    public void InferProfile_MacosPlatform_IsVideotoolbox_RegardlessOfHwaccels()
    {
        Assert.Equal("videotoolbox", HwTranslator.InferProfile([], "macos-arm64"));
        Assert.Equal("videotoolbox", HwTranslator.InferProfile(["vaapi"], "macos-x86_64"));
    }

    [Fact]
    public void InferProfile_LinuxWithVaapi_IsVaapi()
    {
        Assert.Equal("vaapi", HwTranslator.InferProfile(["vaapi"], "linux-x86_64"));
    }

    [Fact]
    public void InferProfile_LinuxWithCuda_IsNvenc()
    {
        Assert.Equal("nvenc", HwTranslator.InferProfile(["cuda"], "linux-x86_64"));
    }

    [Fact]
    public void InferProfile_LinuxWithQsv_IsQsv()
    {
        Assert.Equal("qsv", HwTranslator.InferProfile(["qsv"], "linux-x86_64"));
    }

    [Fact]
    public void InferProfile_LinuxWithNoRecognizedHwaccel_IsNone()
    {
        Assert.Equal("none", HwTranslator.InferProfile(["something-else"], "linux-x86_64"));
        Assert.Equal("none", HwTranslator.InferProfile([], "linux-x86_64"));
    }

    [Fact]
    public void InferProfile_PriorityOrder_VaapiBeforeCudaBeforeQsv()
    {
        Assert.Equal("vaapi", HwTranslator.InferProfile(["vaapi", "cuda", "qsv"], "linux-x86_64"));
        Assert.Equal("nvenc", HwTranslator.InferProfile(["cuda", "qsv"], "linux-x86_64"));
    }

    // --- IdentifySourceProfile ---

    [Fact]
    public void IdentifySourceProfile_FromHwaccelToken()
    {
        var argv = ArgumentLine.Split(Fixtures.HwVideotoolboxCommandLine);

        Assert.Equal("videotoolbox", HwTranslator.IdentifySourceProfile(argv));
    }

    [Fact]
    public void IdentifySourceProfile_FromInitHwDeviceToken_WhenNoHwaccelToken()
    {
        List<string> argv = ["-init_hw_device", "vaapi=va:/dev/dri/renderD128", "-i", "file:/x.mkv", "-codec:v:0", "h264_vaapi", "-f", "hls", "-y", "out.m3u8"];

        Assert.Equal("vaapi", HwTranslator.IdentifySourceProfile(argv));
    }

    [Fact]
    public void IdentifySourceProfile_FallsBackToEncoderSuffix_NvencSpecialCase()
    {
        // No -hwaccel/-init_hw_device tokens at all; must fall back to the "_nvenc" encoder suffix, which
        // does NOT literally match the "cuda" hwaccel device-type token (see EncoderSuffixToProfile).
        List<string> argv = ["-i", "file:/x.mkv", "-codec:v:0", "h264_nvenc", "-f", "hls", "-y", "out.m3u8"];

        Assert.Equal("nvenc", HwTranslator.IdentifySourceProfile(argv));
    }

    [Fact]
    public void IdentifySourceProfile_NothingHardwareSpecific_IsNone()
    {
        List<string> argv = ["-i", "file:/x.mkv", "-codec:v:0", "libx264", "-f", "hls", "-y", "out.m3u8"];

        Assert.Equal("none", HwTranslator.IdentifySourceProfile(argv));
    }

    // A remux (video copied) engages no video hardware, so it must stay routable to EVERY agent —
    // including a videotoolbox one, which the translation table only ever knows as a source profile.
    // Regression: the table-entry check used to run before the remux branch and refused these outright.
    [Fact]
    public void RemuxRoutesToVideoToolboxAgentEvenThoughItIsNotATranslationTarget()
    {
        var argv = ArgumentLine.Split(
            "-analyzeduration 200M -f matroska -i file:/Volumes/data/s/e.mkv " +
            "-map 0:0 -map 0:1 -codec:v:0 copy -bsf:v h264_mp4toannexb -codec:a:0 aac_at -ac 2 -f hls");

        var agent = MakeVideotoolboxAgent();

        Assert.True(HwTranslator.TryTranslate(argv, agent, out var translated, out var reason), reason);
        Assert.Contains("copy", translated);
        Assert.Contains("aac_at", translated);
    }

    // The same remux sent to Linux/VAAPI: aac_at does not exist off macOS and must become aac.
    [Fact]
    public void RemuxToVaapiAgentMapsAudioAndDropsUnusedHwaccelTokens()
    {
        var argv = ArgumentLine.Split(
            "-analyzeduration 200M -init_hw_device videotoolbox=vt -hwaccel videotoolbox -f matroska " +
            "-i file:/Volumes/data/s/e.mkv -codec:v:0 copy -codec:a:0 aac_at -ac 2 -f hls");

        var agent = MakeVaapiAgent();

        Assert.True(HwTranslator.TryTranslate(argv, agent, out var translated, out var reason), reason);
        Assert.Contains("aac", translated);
        Assert.DoesNotContain("aac_at", translated);
        Assert.Contains("copy", translated);

        // the videotoolbox device tokens are dead weight for a copy and would fail on Linux
        Assert.DoesNotContain("-init_hw_device", translated);
        Assert.DoesNotContain("-hwaccel", translated);
        Assert.DoesNotContain("videotoolbox", translated);
    }
}
