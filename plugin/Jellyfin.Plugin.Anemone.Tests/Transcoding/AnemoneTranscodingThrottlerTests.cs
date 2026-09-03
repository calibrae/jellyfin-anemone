using Jellyfin.Plugin.Anemone.TestKit;
using Jellyfin.Plugin.Anemone.Transcoding;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.Anemone.Tests.Transcoding;

/// <summary>
/// Direct unit tests for the forked <see cref="AnemoneTranscodingThrottler"/>: a hand-built
/// <see cref="TranscodingJob"/> (no manager/StartFfMpeg/agent in the loop at all) and a recording fake key
/// sink, driven through the internal <see cref="AnemoneTranscodingThrottler.RunThrottleCheckAsync"/> test
/// seam instead of waiting on the real 5s Timer (see that method's own remarks - it's exactly what the
/// Timer invokes every tick). Reuses <see cref="AnemoneTranscodeManagerHarness"/> purely for its real
/// <c>EncodingOptions</c>/<c>IFileSystem</c>/logger plumbing, same as every other Transcoding test file -
/// nothing here goes through <see cref="AnemoneTranscodeManagerHarness.Manager"/> itself.
///
/// Only the HLS time-based branch of upstream's <c>IsThrottleAllowed</c> is exercised (download/transcoding
/// position ticks) since that's the only branch Anemone's HLS-only routing ever reaches in production; the
/// progressive byte-based branch is unmodified upstream code untouched by this fork.
/// </summary>
public sealed class AnemoneTranscodingThrottlerTests : IDisposable
{
    private readonly AnemoneTranscodeManagerHarness _harness = new();
    private readonly List<string> _sentKeys = [];

    public AnemoneTranscodingThrottlerTests()
    {
        _harness.ConfigManager.EncodingOptions.EnableThrottling = true;
        _harness.ConfigManager.EncodingOptions.ThrottleDelaySeconds = 60;
    }

    public void Dispose() => _harness.Dispose();

    private TranscodingJob NewJob(string path = "/tmp/anemone-throttler-test.m3u8") =>
        new(_harness.LoggerFactory.CreateLogger<TranscodingJob>())
        {
            Id = Guid.NewGuid().ToString("N"),
            Path = path,
        };

    private AnemoneTranscodingThrottler NewThrottler(TranscodingJob job, bool pkeyPauseSupported = true) =>
        new(
            job,
            _harness.LoggerFactory.CreateLogger<AnemoneTranscodingThrottler>(),
            _harness.ConfigManager,
            _harness.FileSystem,
            pkeyPauseSupported,
            key =>
            {
                _sentKeys.Add(key);
                return Task.CompletedTask;
            });

    /// <summary>Sets up the HLS time-based gap <c>IsThrottleAllowed</c> reads: transcoding is <paramref name="aheadBy"/> ahead of what's been downloaded.</summary>
    private static void SetGap(TranscodingJob job, TimeSpan aheadBy)
    {
        job.DownloadPositionTicks = TimeSpan.FromMinutes(10).Ticks;
        job.TranscodingPositionTicks = job.DownloadPositionTicks + aheadBy.Ticks;
    }

    [Fact]
    public async Task Pauses_WhenGapExceedsThreshold()
    {
        var job = NewJob();
        SetGap(job, TimeSpan.FromSeconds(90)); // threshold floors to 60s (see MinimumThresholdIsSixtySeconds)
        var throttler = NewThrottler(job);

        await throttler.RunThrottleCheckAsync();

        Assert.True(throttler.IsPaused);
        Assert.Equal(["p"], _sentKeys);
    }

    [Fact]
    public async Task DoesNotPause_WhenGapBelowThreshold()
    {
        var job = NewJob();
        SetGap(job, TimeSpan.FromSeconds(30));
        var throttler = NewThrottler(job);

        await throttler.RunThrottleCheckAsync();

        Assert.False(throttler.IsPaused);
        Assert.Empty(_sentKeys);
    }

    [Fact]
    public async Task Resumes_WhenViewerCatchesUp()
    {
        var job = NewJob();
        SetGap(job, TimeSpan.FromSeconds(90));
        var throttler = NewThrottler(job);
        await throttler.RunThrottleCheckAsync();
        Assert.True(throttler.IsPaused);

        // Viewer catches up: the gap shrinks back below the threshold on the next tick.
        SetGap(job, TimeSpan.FromSeconds(10));
        await throttler.RunThrottleCheckAsync();

        Assert.False(throttler.IsPaused);
        Assert.Equal(["p", "u"], _sentKeys);
    }

    [Fact]
    public async Task NeverPausesTwice()
    {
        var job = NewJob();
        SetGap(job, TimeSpan.FromSeconds(90));
        var throttler = NewThrottler(job);

        await throttler.RunThrottleCheckAsync();
        await throttler.RunThrottleCheckAsync();
        await throttler.RunThrottleCheckAsync();

        Assert.True(throttler.IsPaused);
        Assert.Equal(["p"], _sentKeys); // one "p" total, not one per tick
    }

    [Fact]
    public async Task UnpausesOnStop()
    {
        var job = NewJob();
        SetGap(job, TimeSpan.FromSeconds(90));
        var throttler = NewThrottler(job);
        await throttler.RunThrottleCheckAsync();
        Assert.True(throttler.IsPaused);

        await throttler.Stop();

        Assert.False(throttler.IsPaused);
        Assert.Equal(["p", "u"], _sentKeys);
    }

    [Fact]
    public async Task UnpauseOnStop_IsANoOp_WhenNeverPaused()
    {
        var job = NewJob();
        SetGap(job, TimeSpan.FromSeconds(5)); // never crosses the threshold
        var throttler = NewThrottler(job);
        await throttler.RunThrottleCheckAsync();
        Assert.False(throttler.IsPaused);

        await throttler.Stop();

        Assert.Empty(_sentKeys); // no spurious resume key when nothing was paused
    }

    [Fact]
    public async Task StopsChecking_OnceJobHasExited()
    {
        var job = NewJob();
        SetGap(job, TimeSpan.FromSeconds(90)); // would obviously pause if the job were still running
        job.HasExited = true;
        var throttler = NewThrottler(job);

        await throttler.RunThrottleCheckAsync();

        Assert.False(throttler.IsPaused);
        Assert.Empty(_sentKeys);
    }

    [Fact]
    public async Task RespectsEnableThrottlingFalse()
    {
        _harness.ConfigManager.EncodingOptions.EnableThrottling = false;

        var job = NewJob();
        SetGap(job, TimeSpan.FromMinutes(30)); // would obviously pause if throttling were on
        var throttler = NewThrottler(job);

        await throttler.RunThrottleCheckAsync();

        Assert.False(throttler.IsPaused);
        Assert.Empty(_sentKeys);
    }

    [Fact]
    public async Task MinimumThresholdIsSixtySeconds_EvenWhenConfiguredLower()
    {
        // options.ThrottleDelaySeconds is floored to 60 via Math.Max(options.ThrottleDelaySeconds, 60) -
        // upstream's own gate, kept verbatim. A 45s configured delay must NOT make a 45s gap throttle.
        _harness.ConfigManager.EncodingOptions.ThrottleDelaySeconds = 5;

        var job = NewJob();
        SetGap(job, TimeSpan.FromSeconds(45));
        var throttler = NewThrottler(job);

        await throttler.RunThrottleCheckAsync();

        Assert.False(throttler.IsPaused);
        Assert.Empty(_sentKeys);
    }

    [Fact]
    public async Task LocalFallback_UsesCKey_WhenPkeyPauseUnsupported()
    {
        // Upstream's own local-only "c" fallback for an ffmpeg without the pause patch (old enough to
        // still satisfy StartThrottler's EncoderVersion<=6.1 gate) - never reachable for a remote job, see
        // StartRemoteThrottler/AnemoneTranscodeManagerTests for that half.
        var job = NewJob();
        SetGap(job, TimeSpan.FromSeconds(90));
        var throttler = NewThrottler(job, pkeyPauseSupported: false);

        await throttler.RunThrottleCheckAsync();

        Assert.Equal(["c"], _sentKeys);
    }

    [Fact]
    public async Task LocalFallback_ResumesWithNewline_WhenPkeyPauseUnsupported()
    {
        var job = NewJob();
        SetGap(job, TimeSpan.FromSeconds(90));
        var throttler = NewThrottler(job, pkeyPauseSupported: false);
        await throttler.RunThrottleCheckAsync();

        SetGap(job, TimeSpan.FromSeconds(10));
        await throttler.RunThrottleCheckAsync();

        Assert.Equal(["c", Environment.NewLine], _sentKeys);
    }
}
