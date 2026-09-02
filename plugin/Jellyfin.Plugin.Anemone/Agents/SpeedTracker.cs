using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Anemone.Agents;

/// <summary>
/// Extracts ffmpeg's own <c>speed=N.Nx</c> progress marker from stderr lines and keeps a per-agent
/// exponentially-weighted rolling average of it. See PROTOCOL.md "Placement inputs (v2.1)": the server
/// measures throughput itself from data it already receives, rather than asking the agent for it. The
/// value is a realtime factor (2.0x = twice as fast as playback needs), not a throughput rate, so it is
/// only ever averaged against itself - never mixed with a different job's bitrate or resolution.
/// </summary>
public sealed class SpeedTracker
{
    // Anchored on "speed=", optional padding, a decimal number, then "x" - e.g. "speed=2.0x" or
    // "speed=  1.5x". "speed=N/A" (ffmpeg's own early-job placeholder, no digits) simply doesn't match
    // and is correctly ignored rather than parsed as zero.
    private static readonly Regex SpeedPattern = new(
        @"speed=\s*([0-9]+(?:\.[0-9]+)?)x",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    // How much one new observation moves the average (0 < alpha <= 1). Higher = more responsive to what
    // the agent is doing right now; lower = smoother across a job's ordinary scene-to-scene noise. 0.2
    // settles to within a few percent of a step change within about a dozen progress lines - a few
    // seconds of one job - while still surviving one bad/outlier reading.
    private const double Alpha = 0.2;

    private readonly object _gate = new();
    private double? _average;

    /// <summary>The current rolling average realtime factor, or null if this tracker has never observed a parseable speed.</summary>
    public double? Average
    {
        get
        {
            lock (_gate)
            {
                return _average;
            }
        }
    }

    /// <summary>
    /// Parses every <c>speed=</c> marker in <paramref name="line"/> and returns the last one. A single
    /// ffmpeg progress line only ever carries one, but nothing in the protocol forbids more than one
    /// progress report landing in a single stderr frame, and the last is the most recent.
    /// </summary>
    /// <returns>False when <paramref name="line"/> has no parseable <c>speed=</c> value (including <c>speed=N/A</c>).</returns>
    public static bool TryParseSpeed(string line, out double speed)
    {
        ArgumentNullException.ThrowIfNull(line);

        Match? last = null;
        foreach (Match match in SpeedPattern.Matches(line))
        {
            last = match;
        }

        if (last is null)
        {
            speed = 0;
            return false;
        }

        return double.TryParse(last.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out speed);
    }

    /// <summary>Folds one observed realtime factor into the rolling average.</summary>
    public void Observe(double speed)
    {
        lock (_gate)
        {
            _average = _average is null ? speed : (Alpha * speed) + ((1 - Alpha) * _average.Value);
        }
    }
}
