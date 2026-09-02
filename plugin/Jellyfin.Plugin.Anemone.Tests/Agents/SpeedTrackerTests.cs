using Jellyfin.Plugin.Anemone.Agents;

namespace Jellyfin.Plugin.Anemone.Tests.Agents;

public class SpeedTrackerTests
{
    // --- TryParseSpeed ---

    [Theory]
    [InlineData("frame=  120 fps= 60 q=-0.0 size=    1024KiB time=00:00:04.00 bitrate=2097.2kbits/s speed=2.0x", 2.0)]
    [InlineData("speed=1x", 1.0)]
    [InlineData("...speed=0.75x...", 0.75)]
    public void TryParseSpeed_ParsesTheValue(string line, double expected)
    {
        Assert.True(SpeedTracker.TryParseSpeed(line, out var speed));
        Assert.Equal(expected, speed, precision: 9);
    }

    [Fact]
    public void TryParseSpeed_HandlesPaddingBeforeTheNumber()
    {
        Assert.True(SpeedTracker.TryParseSpeed("frame=1 speed=  1.5x", out var speed));
        Assert.Equal(1.5, speed, precision: 9);
    }

    [Fact]
    public void TryParseSpeed_ReturnsFalse_ForNotAvailable()
    {
        Assert.False(SpeedTracker.TryParseSpeed("frame=1 fps=0 q=0.0 size=0KiB time=00:00:00.00 bitrate=N/A speed=N/A", out _));
    }

    [Fact]
    public void TryParseSpeed_ReturnsFalse_WhenNoSpeedField()
    {
        Assert.False(SpeedTracker.TryParseSpeed("frame=1 fps=30 q=-1.0 size=100KiB time=00:00:01.00 bitrate=800kbits/s", out _));
    }

    [Fact]
    public void TryParseSpeed_PicksTheLastValue_WhenMultipleOnOneLine()
    {
        Assert.True(SpeedTracker.TryParseSpeed("speed=1.0x ... progress update ... speed=3.0x", out var speed));
        Assert.Equal(3.0, speed, precision: 9);
    }

    // --- Observe / Average (EWMA) ---

    [Fact]
    public void Average_IsNull_BeforeAnyObservation()
    {
        var tracker = new SpeedTracker();

        Assert.Null(tracker.Average);
    }

    [Fact]
    public void Observe_FirstObservation_SetsAverageDirectly()
    {
        var tracker = new SpeedTracker();

        tracker.Observe(2.0);

        Assert.Equal(2.0, tracker.Average!.Value, precision: 9);
    }

    [Fact]
    public void Observe_SecondObservation_BlendsWithAlpha()
    {
        var tracker = new SpeedTracker();
        tracker.Observe(2.0);
        tracker.Observe(4.0);

        // alpha=0.2: 0.2*4.0 + 0.8*2.0 = 2.4
        Assert.Equal(2.4, tracker.Average!.Value, precision: 9);
    }

    [Fact]
    public void Observe_ConvergesTowardsARepeatedValue()
    {
        var tracker = new SpeedTracker();
        tracker.Observe(1.0);

        for (var i = 0; i < 200; i++)
        {
            tracker.Observe(3.0);
        }

        Assert.Equal(3.0, tracker.Average!.Value, precision: 6);
    }

    [Fact]
    public void Observe_OneOutlier_DoesNotDominateTheAverage()
    {
        var tracker = new SpeedTracker();
        for (var i = 0; i < 20; i++)
        {
            tracker.Observe(2.0);
        }

        tracker.Observe(20.0); // one bad reading

        Assert.True(tracker.Average!.Value < 6.0, $"one outlier should not swing the average anywhere near 20, got {tracker.Average}");
        Assert.True(tracker.Average!.Value > 2.0);
    }
}
