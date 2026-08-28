using Jellyfin.Plugin.Anemone.Transcoding;

namespace Jellyfin.Plugin.Anemone.Tests.Transcoding;

public class RoutePlannerTests
{
    [Fact]
    public void Analyze_TranscodeFixture_IsHls()
    {
        var analysis = Analyze(Fixtures.TranscodeCommandLine);

        Assert.True(analysis.IsHls);
        Assert.True(analysis.IsRoutable);
        Assert.Null(analysis.NotRoutableReason);
    }

    [Fact]
    public void Analyze_TranscodeFixture_OneInputWithMkvPath()
    {
        var analysis = Analyze(Fixtures.TranscodeCommandLine);

        Assert.Equal(1, analysis.InputCount);
        Assert.Equal([Fixtures.InputPath], analysis.InputPaths);
    }

    [Fact]
    public void Analyze_TranscodeFixture_HwaccelsIsVideoToolbox()
    {
        var analysis = Analyze(Fixtures.TranscodeCommandLine);

        Assert.Equal(["videotoolbox"], analysis.Requirements.Hwaccels);
    }

    [Fact]
    public void Analyze_TranscodeFixture_EncodersAreVideoAndAudio()
    {
        var analysis = Analyze(Fixtures.TranscodeCommandLine);

        Assert.Equal(["h264_videotoolbox", "aac_at"], analysis.Requirements.Encoders);
    }

    [Fact]
    public void Analyze_TranscodeFixture_FiltersAreScaleVtAndVolume()
    {
        var analysis = Analyze(Fixtures.TranscodeCommandLine);

        Assert.Equal(["scale_vt", "volume"], analysis.Requirements.Filters);
    }

    [Fact]
    public void Analyze_TranscodeFixture_NoDecodersBeforeInput()
    {
        // -codec:v:0 h264_videotoolbox appears AFTER -i in this fixture, so it's an encoder, not a decoder.
        var analysis = Analyze(Fixtures.TranscodeCommandLine);

        Assert.Empty(analysis.Requirements.Decoders);
    }

    [Fact]
    public void Analyze_DirectStreamFixture_IsHlsAndRoutable()
    {
        var analysis = Analyze(Fixtures.DirectStreamCommandLine);

        Assert.True(analysis.IsHls);
        Assert.True(analysis.IsRoutable);
    }

    [Fact]
    public void Analyze_DirectStreamFixture_EncodersExcludeCopiedVideo()
    {
        var analysis = Analyze(Fixtures.DirectStreamCommandLine);

        Assert.Equal(["aac_at"], analysis.Requirements.Encoders);
    }

    [Fact]
    public void Analyze_DirectStreamFixture_FiltersIsJustVolume()
    {
        var analysis = Analyze(Fixtures.DirectStreamCommandLine);

        Assert.Equal(["volume"], analysis.Requirements.Filters);
    }

    [Fact]
    public void Analyze_DirectStreamFixture_HwaccelsIsVideoToolbox()
    {
        var analysis = Analyze(Fixtures.DirectStreamCommandLine);

        Assert.Equal(["videotoolbox"], analysis.Requirements.Hwaccels);
    }

    [Fact]
    public void Analyze_ProgressiveCommand_IsNotRoutable()
    {
        var analysis = Analyze(Fixtures.ProgressiveCommandLine);

        Assert.False(analysis.IsHls);
        Assert.False(analysis.IsRoutable);
        Assert.NotNull(analysis.NotRoutableReason);
    }

    [Fact]
    public void Analyze_SubtitleBurnInCommand_IsNotRoutable()
    {
        var analysis = Analyze(Fixtures.SubtitleBurnInCommandLine);

        Assert.False(analysis.IsRoutable);
        Assert.Contains("burn", analysis.NotRoutableReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Analyze_FontsdirWithoutSubtitlesKeyword_IsStillNotRoutable()
    {
        // Only "fontsdir=" appears here (no "subtitles=") - both substrings must independently trigger burn-in detection.
        var argv = ArgumentLine.Split("-i file:\"/x.mkv\" -vf \"ass=x:fontsdir='/data/attachments/ab/c'\" -f hls -y out.m3u8");

        var analysis = RoutePlanner.Analyze(argv);

        Assert.False(analysis.IsRoutable);
    }

    [Fact]
    public void Analyze_ConcatInput_IsNotRoutable()
    {
        var argv = ArgumentLine.Split("-f concat -safe 0 -i \"/cache/x.concat\" -f hls -y out.m3u8");

        var analysis = RoutePlanner.Analyze(argv);

        Assert.False(analysis.IsRoutable);
    }

    [Fact]
    public void Analyze_MoreThanOneInput_IsNotRoutable()
    {
        var argv = ArgumentLine.Split("-i file:\"/a.mkv\" -i file:\"/b.srt\" -f hls -y out.m3u8");

        var analysis = RoutePlanner.Analyze(argv);

        Assert.False(analysis.IsRoutable);
        Assert.Equal(2, analysis.InputCount);
    }

    [Fact]
    public void Analyze_HttpInput_IsNotRoutable()
    {
        var argv = ArgumentLine.Split("-i \"http://example.com/video.mkv\" -f hls -y out.m3u8");

        var analysis = RoutePlanner.Analyze(argv);

        Assert.False(analysis.IsRoutable);
    }

    [Fact]
    public void Analyze_FilterComplexOverlaySubtitles_IsNotRoutable()
    {
        // No "subtitles=" or "fontsdir=" substring anywhere - this exercises the filter_complex-specific
        // "overlay and subtitles both referenced" burn-in check on its own, not the generic substring scan.
        var argv = ArgumentLine.Split("-i file:\"/a.mkv\" -filter_complex \"[0:v][0:s]overlay,subtitles[out]\" -f hls -y out.m3u8");

        var analysis = RoutePlanner.Analyze(argv);

        Assert.False(analysis.IsRoutable);
    }

    [Fact]
    public void Analyze_EmptyArgv_IsNotRoutable()
    {
        var analysis = RoutePlanner.Analyze([]);

        Assert.False(analysis.IsRoutable);
    }

    [Fact]
    public void Analyze_DecoderBeforeInput_IsCapturedSeparatelyFromEncoderAfterInput()
    {
        var argv = ArgumentLine.Split("-c:v h264_cuvid -i file:\"/a.mkv\" -c:v libx264 -f hls -hls_segment_filename \"/t/x%d.ts\" -y /t/x.m3u8");

        var analysis = RoutePlanner.Analyze(argv);

        Assert.Equal(["h264_cuvid"], analysis.Requirements.Decoders);
        Assert.Equal(["libx264"], analysis.Requirements.Encoders);
    }

    [Fact]
    public void Analyze_SegmentFilenameIndex_PointsAtTheValueToken()
    {
        var argv = ArgumentLine.Split(Fixtures.TranscodeCommandLine);

        var analysis = RoutePlanner.Analyze(argv);

        Assert.NotNull(analysis.SegmentFilenameIndex);
        Assert.Equal(argv[analysis.SegmentFilenameIndex!.Value], argv[argv.IndexOf("-hls_segment_filename") + 1]);
        Assert.Equal(argv.Count - 1, analysis.OutputIndex);
    }

    [Fact]
    public void Rewrite_TranscodeFixture_MatchesIndependentlyComputedExpectation()
    {
        var argv = ArgumentLine.Split(Fixtures.TranscodeCommandLine);
        const string ingestBase = "http://10.240.0.1:8096";
        const string jobId = "5f1cabc000000000000000000000abc";
        const string token = "9kQtoken";

        var rewritten = RoutePlanner.Rewrite(argv, ingestBase, jobId, token);

        // Built independently of RoutePlanner's own code path, so this isn't just re-asserting the
        // implementation against itself.
        var expected = new List<string>(argv);
        var segValueIndex = expected.IndexOf("-hls_segment_filename") + 1;
        expected[segValueIndex] = $"{ingestBase}/Anemone/ingest/{jobId}/{Fixtures.Md5}%d.ts";
        expected[^1] = $"{ingestBase}/Anemone/ingest/{jobId}/{Fixtures.Md5}.m3u8";
        expected.InsertRange(
            expected.Count - 1,
            new[] { "-method", "PUT", "-http_persistent", "1", "-headers", $"Authorization: Bearer {token}\r\n" });

        Assert.Equal(expected, rewritten);
    }

    [Fact]
    public void Rewrite_TranscodeFixture_HeaderValueHasLiteralCrLf()
    {
        var argv = ArgumentLine.Split(Fixtures.TranscodeCommandLine);

        var rewritten = RoutePlanner.Rewrite(argv, "http://10.240.0.1:8096", "job1", "tok1").ToList();

        var headersIndex = rewritten.IndexOf("-headers");
        Assert.True(headersIndex >= 0);
        Assert.Equal("Authorization: Bearer tok1\r\n", rewritten[headersIndex + 1]);
    }

    [Fact]
    public void Rewrite_TranscodeFixture_InsertsSixTokensImmediatelyBeforeLastElement()
    {
        var argv = ArgumentLine.Split(Fixtures.TranscodeCommandLine);

        var rewritten = RoutePlanner.Rewrite(argv, "http://10.240.0.1:8096", "job1", "tok1").ToList();

        Assert.Equal(argv.Count + 6, rewritten.Count);
        Assert.Equal(["-method", "PUT", "-http_persistent", "1", "-headers", "Authorization: Bearer tok1\r\n"], rewritten.Skip(rewritten.Count - 7).Take(6));
        Assert.Equal($"http://10.240.0.1:8096/Anemone/ingest/job1/{Fixtures.Md5}.m3u8", rewritten[^1]);
    }

    [Fact]
    public void Rewrite_TrimsTrailingSlashFromIngestBase()
    {
        var argv = ArgumentLine.Split(Fixtures.TranscodeCommandLine);

        var rewritten = RoutePlanner.Rewrite(argv, "http://10.240.0.1:8096/", "job1", "tok1");

        Assert.Equal($"http://10.240.0.1:8096/Anemone/ingest/job1/{Fixtures.Md5}.m3u8", rewritten[^1]);
    }

    [Fact]
    public void Rewrite_LeavesEveryOtherTokenUntouched()
    {
        var argv = ArgumentLine.Split(Fixtures.TranscodeCommandLine);

        var rewritten = RoutePlanner.Rewrite(argv, "http://10.240.0.1:8096", "job1", "tok1").ToList();

        // Every token up to (but not including) -hls_segment_filename's value, and everything
        // between it and the inserted options, must be byte-for-byte identical to the input.
        var segValueIndex = argv.IndexOf("-hls_segment_filename") + 1;
        for (var i = 0; i < segValueIndex; i++)
        {
            Assert.Equal(argv[i], rewritten[i]);
        }

        for (var i = segValueIndex + 1; i < argv.Count - 1; i++)
        {
            Assert.Equal(argv[i], rewritten[i]);
        }
    }

    private static RouteAnalysis Analyze(string commandLine) => RoutePlanner.Analyze(ArgumentLine.Split(commandLine));
}
