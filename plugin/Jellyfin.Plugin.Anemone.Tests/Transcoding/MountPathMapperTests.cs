using Jellyfin.Plugin.Anemone.Contracts;
using Jellyfin.Plugin.Anemone.Transcoding;

namespace Jellyfin.Plugin.Anemone.Tests.Transcoding;

public class MountPathMapperTests
{
    [Fact]
    public void IsUnderMount_CoversPathUnderIt()
    {
        Assert.True(MountPathMapper.IsUnderMount("/Volumes/data/x.mkv", "/Volumes/data"));
    }

    [Fact]
    public void IsUnderMount_CoversTheMountPathItself()
    {
        Assert.True(MountPathMapper.IsUnderMount("/Volumes/data", "/Volumes/data"));
    }

    [Fact]
    public void IsUnderMount_DoesNotCoverASiblingWithTheSamePrefix()
    {
        // /Volumes/data must NOT cover /Volumes/database/x.mkv - the match must land on a path-segment
        // boundary, not just a string prefix.
        Assert.False(MountPathMapper.IsUnderMount("/Volumes/database/x.mkv", "/Volumes/data"));
    }

    [Fact]
    public void IsUnderMount_DoesNotCoverAnUnrelatedPath()
    {
        Assert.False(MountPathMapper.IsUnderMount("/Volumes/other/x.mkv", "/Volumes/data"));
    }

    [Fact]
    public void FindLongestMatch_PrefersTheLongestCoveringServerPath()
    {
        AgentMount[] mounts =
        [
            new AgentMount("/mnt/media", true, "/Volumes/data"),
            new AgentMount("/mnt/4k", true, "/Volumes/data/4k"),
        ];

        var match = MountPathMapper.FindLongestMatch(mounts, "/Volumes/data/4k/movie.mkv");

        Assert.NotNull(match);
        Assert.Equal("/mnt/4k", match!.Path);
    }

    [Fact]
    public void FindLongestMatch_IgnoresNotOkMounts()
    {
        AgentMount[] mounts = [new AgentMount("/mnt/media", false, "/Volumes/data")];

        var match = MountPathMapper.FindLongestMatch(mounts, "/Volumes/data/x.mkv");

        Assert.Null(match);
    }

    [Fact]
    public void FindLongestMatch_FallsBackToPathWhenServerPathAbsent()
    {
        // server_path absent on the wire -> AgentMount.EffectiveServerPath defaults to Path (identical layout).
        AgentMount[] mounts = [new AgentMount("/Volumes/data", true)];

        var match = MountPathMapper.FindLongestMatch(mounts, "/Volumes/data/x.mkv");

        Assert.Same(mounts[0], match);
    }

    [Fact]
    public void TryMapInputPaths_RewritesFilePrefixedInput_LongestServerPathPrefix()
    {
        List<string> argv = ["-hwaccel", "videotoolbox", "-i", "file:/Volumes/data/s/e.mkv", "-f", "hls", "-y", "out.m3u8"];
        AgentMount[] mounts = [new AgentMount("/mnt/media", true, "/Volumes/data")];

        var ok = MountPathMapper.TryMapInputPaths(argv, mounts, out var mapped, out var reason);

        Assert.True(ok);
        Assert.Null(reason);
        Assert.Equal("file:/mnt/media/s/e.mkv", mapped[3]);
    }

    [Fact]
    public void TryMapInputPaths_PreservesFilePrefixExactly_NoPrefixWhenInputHadNone()
    {
        List<string> argv = ["-i", "/Volumes/data/e.mkv", "-f", "hls", "-y", "out.m3u8"];
        AgentMount[] mounts = [new AgentMount("/mnt/media", true, "/Volumes/data")];

        var ok = MountPathMapper.TryMapInputPaths(argv, mounts, out var mapped, out _);

        Assert.True(ok);
        Assert.Equal("/mnt/media/e.mkv", mapped[1]);
        Assert.DoesNotContain("file:", mapped[1], StringComparison.Ordinal);
    }

    [Fact]
    public void TryMapInputPaths_ExactMountRootInput_MapsToBareAgentPath()
    {
        List<string> argv = ["-i", "file:/Volumes/data", "-f", "hls", "-y", "out.m3u8"];
        AgentMount[] mounts = [new AgentMount("/mnt/media", true, "/Volumes/data")];

        var ok = MountPathMapper.TryMapInputPaths(argv, mounts, out var mapped, out _);

        Assert.True(ok);
        Assert.Equal("file:/mnt/media", mapped[1]);
    }

    [Fact]
    public void TryMapInputPaths_PicksLongestMatchingServerPath()
    {
        List<string> argv = ["-i", "file:/Volumes/data/4k/movie.mkv", "-f", "hls", "-y", "out.m3u8"];
        AgentMount[] mounts =
        [
            new AgentMount("/mnt/media", true, "/Volumes/data"),
            new AgentMount("/mnt/4k", true, "/Volumes/data/4k"),
        ];

        var ok = MountPathMapper.TryMapInputPaths(argv, mounts, out var mapped, out _);

        Assert.True(ok);
        Assert.Equal("file:/mnt/4k/movie.mkv", mapped[1]);
    }

    [Fact]
    public void TryMapInputPaths_NoCoveringMount_Fails()
    {
        List<string> argv = ["-i", "file:/Volumes/database/x.mkv", "-f", "hls", "-y", "out.m3u8"];
        AgentMount[] mounts = [new AgentMount("/mnt/media", true, "/Volumes/data")];

        var ok = MountPathMapper.TryMapInputPaths(argv, mounts, out var mapped, out var reason);

        Assert.False(ok);
        Assert.NotNull(reason);
        Assert.Same(argv, mapped);
    }

    [Fact]
    public void TryMapInputPaths_LeavesEveryOtherTokenByteForByteUntouched()
    {
        var argv = ArgumentLine.Split(Fixtures.HwVideotoolboxCommandLine);
        AgentMount[] mounts = [new AgentMount("/mnt/media", true, "/Volumes/data")];

        var ok = MountPathMapper.TryMapInputPaths(argv, mounts, out var mapped, out _);

        Assert.True(ok);
        Assert.Equal(argv.Count, mapped.Count);
        var inputIndex = argv.IndexOf("-i") + 1;
        for (var i = 0; i < argv.Count; i++)
        {
            if (i == inputIndex)
            {
                continue;
            }

            Assert.Equal(argv[i], mapped[i]);
        }

        Assert.Equal("file:/mnt/media/x/e.mkv", mapped[inputIndex]);
    }

    [Fact]
    public void TryMapInputPaths_OutputAlreadyAtIngestUrl_NeverTouched()
    {
        // Only -i inputs are mapped; the (still local, pre-Rewrite) output path must survive untouched.
        List<string> argv = ["-i", "file:/Volumes/data/e.mkv", "-f", "hls", "-hls_segment_filename", "/Volumes/data/out%d.ts", "-y", "/Volumes/data/out.m3u8"];
        AgentMount[] mounts = [new AgentMount("/mnt/media", true, "/Volumes/data")];

        var ok = MountPathMapper.TryMapInputPaths(argv, mounts, out var mapped, out _);

        Assert.True(ok);
        Assert.Equal("/Volumes/data/out%d.ts", mapped[^3]);
        Assert.Equal("/Volumes/data/out.m3u8", mapped[^1]);
    }
}
