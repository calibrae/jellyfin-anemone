using Jellyfin.Plugin.Cluster.Transcoding;

namespace Jellyfin.Plugin.Cluster.Tests.Transcoding;

public class ArgumentLineTests
{
    [Fact]
    public void Split_TranscodeFixture_MergesQuotedPathIntoOneToken()
    {
        var argv = ArgumentLine.Split(Fixtures.TranscodeCommandLine);

        var inputIndex = argv.IndexOf("-i");
        Assert.True(inputIndex >= 0);
        Assert.Equal("file:" + Fixtures.InputPath, argv[inputIndex + 1]);
    }

    [Fact]
    public void Split_TranscodeFixture_PreservesSpacesInPathAsOneToken()
    {
        var argv = ArgumentLine.Split(Fixtures.TranscodeCommandLine);

        // The path itself contains spaces (Show Name/... - Pilot.mkv) - must not be split into
        // multiple argv entries.
        Assert.Contains(argv, a => a == "file:" + Fixtures.InputPath);
        Assert.DoesNotContain(argv, a => a == "Show");
    }

    [Fact]
    public void Split_TranscodeFixture_KeepsExprFilterIntact()
    {
        var argv = ArgumentLine.Split(Fixtures.TranscodeCommandLine);

        Assert.Contains("expr:gte(t,n_forced*3)", argv);
    }

    [Fact]
    public void Split_TranscodeFixture_KeepsScaleVtFilterIntact()
    {
        var argv = ArgumentLine.Split(Fixtures.TranscodeCommandLine);

        Assert.Contains("scale_vt=w=1280:h=640:format=nv12", argv);
    }

    [Fact]
    public void Split_DirectStreamFixture_KeepsVolumeFilterIntact()
    {
        var argv = ArgumentLine.Split(Fixtures.DirectStreamCommandLine);

        Assert.Contains("volume=2", argv);
    }

    [Fact]
    public void Split_DirectStreamFixture_LastTwoTokensAreSegmentFilenameAndPlaylist()
    {
        var argv = ArgumentLine.Split(Fixtures.DirectStreamCommandLine);

        Assert.Equal(Fixtures.TranscodesDir + "/" + Fixtures.Md5 + ".m3u8", argv[^1]);
        Assert.Equal("-y", argv[^2]);
    }

    [Fact]
    public void Split_EmptyString_ReturnsEmptyList()
    {
        Assert.Empty(ArgumentLine.Split(string.Empty));
    }

    [Fact]
    public void Split_WhitespaceOnly_ReturnsEmptyList()
    {
        Assert.Empty(ArgumentLine.Split("   \t  "));
    }

    [Fact]
    public void Split_TrailingWhitespace_DoesNotProduceEmptyTrailingToken()
    {
        var argv = ArgumentLine.Split("-a foo   ");

        Assert.Equal(["-a", "foo"], argv);
    }

    [Fact]
    public void Split_LeadingWhitespace_Ignored()
    {
        var argv = ArgumentLine.Split("   -a foo");

        Assert.Equal(["-a", "foo"], argv);
    }

    [Fact]
    public void Split_EscapedQuote_BecomesLiteralQuoteWithoutTogglingQuoteMode()
    {
        // a\"b (unquoted) -> the backslash escapes the quote: literal `"` in the middle of the token,
        // never enters quoted mode, so the following space (if any) still terminates the token.
        var argv = ArgumentLine.Split("a\\\"b c");

        Assert.Equal(["a\"b", "c"], argv);
    }

    [Fact]
    public void Split_EvenBackslashesBeforeQuote_HalveAndQuoteToggles()
    {
        // a\\\\"b c" -> four backslashes collapse to two literal backslashes, and the quote that
        // follows is NOT escaped - it opens quoted mode normally, so the space before the closing
        // quote is part of the token.
        var argv = ArgumentLine.Split("a\\\\\\\\\"b c\"");

        Assert.Equal(["a\\\\b c"], argv);
    }

    [Fact]
    public void Split_BackslashNotFollowedByQuote_IsLiteral()
    {
        var argv = ArgumentLine.Split(@"C:\no\quote\here");

        Assert.Equal([@"C:\no\quote\here"], argv);
    }

    [Fact]
    public void Split_AdjacentQuotedAndUnquotedSegments_MergeIntoOneToken()
    {
        var argv = ArgumentLine.Split("file:\"/a b.mkv\"");

        Assert.Equal(["file:/a b.mkv"], argv);
    }

    [Fact]
    public void Split_EmptyQuotedArgument_ProducesOneEmptyToken()
    {
        var argv = ArgumentLine.Split("-headers \"\" -y");

        Assert.Equal(["-headers", string.Empty, "-y"], argv);
    }

    [Fact]
    public void Join_RoundTripsThroughSplit()
    {
        var original = new List<string> { "-headers", "Authorization: Bearer abc\r\n", "-y", "/plain/path", "has\"quote" };

        var joined = ArgumentLine.Join(original);
        var reparsed = ArgumentLine.Split(joined);

        Assert.Equal(original, reparsed);
    }

    [Fact]
    public void Join_LeavesSimpleTokensUnquoted()
    {
        Assert.Equal("-y", ArgumentLine.Join(["-y"]));
    }

    [Fact]
    public void Join_QuotesTokenContainingWhitespace()
    {
        Assert.Equal("\"a b\"", ArgumentLine.Join(["a b"]));
    }
}
