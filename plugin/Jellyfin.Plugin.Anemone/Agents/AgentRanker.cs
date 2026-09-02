using System.Globalization;

namespace Jellyfin.Plugin.Anemone.Agents;

/// <summary>
/// Immutable per-candidate input to <see cref="AgentRanker.Score"/>, factored out of live
/// <see cref="IAgentConnection"/>/<see cref="Contracts.AgentInfo"/> objects so the ranking formula is a
/// pure, unit-testable function over plain data - see PROTOCOL.md "Placement inputs (v2.1)".
/// </summary>
/// <param name="AgentName">For the <see cref="AgentRankingResult.Reason"/> string only.</param>
/// <param name="Local">
/// Whether the job's input is on storage attached to this agent itself: true/false when known, null when
/// no mount covering the job said either way (or there is no specific job, e.g. a status-page summary).
/// </param>
/// <param name="MeasuredSpeed">This agent's rolling-average ffmpeg <c>speed=</c> factor, or null if unmeasured.</param>
/// <param name="SpareCapacityFraction">Free-slot fraction, <c>1 - active/max</c>, already known to be in [0, 1].</param>
/// <param name="Load">The agent's own last-reported <c>status.load</c> (0..1), or null if never reported.</param>
public sealed record AgentRankingInput(
    string AgentName,
    bool? Local,
    double? MeasuredSpeed,
    double SpareCapacityFraction,
    double? Load);

/// <summary>The score plus a human-readable breakdown, from <see cref="AgentRanker.Score"/>.</summary>
public sealed record AgentRankingResult(double Score, string Reason);

/// <summary>
/// Ranks agents that already passed eligibility (<see cref="AgentHub.CandidatesFrom"/>'s connected/alive/
/// capacity/mount-coverage/ffmpeg-version checks) by what they can actually deliver, not just whether they
/// have a free slot. See PROTOCOL.md "Placement inputs (v2.1)" and RESEARCH.md.
///
/// A weighted sum of four clearly-named terms, listed here in descending order of influence:
///
///  1. Media locality (<see cref="LocalityWeight"/>) - reading the source off the agent's own disk skips a
///     network round trip entirely; on the real fleet that's the single biggest factor. Contributes
///     +/-<see cref="LocalityWeight"/>, or 0 when unknown - which therefore ranks strictly between a
///     known-local and a known-remote agent, never as either.
///  2. Measured throughput (<see cref="ThroughputWeight"/>) - the agent's own rolling-average ffmpeg
///     <c>speed=</c> factor (see <see cref="SpeedTracker"/>), relative to the 1.0x real-time baseline. An
///     agent with no measurement yet contributes exactly 0 here - neither penalised nor favoured - so it
///     is ranked on capacity alone and still gets work, which is how it ever gets measured at all.
///  3. Spare capacity (<see cref="CapacityWeight"/>) - the existing free-slot fraction.
///  4. Reported load (<see cref="LoadWeight"/>) - the agent's own <c>status.load</c> (0..1). Advisory and
///     given the smallest weight: the server's own active-job count is authoritative, this only adds what
///     that count can't see (another tenant on the box, an unusually cheap/expensive job).
///
/// Deliberately NOT lexicographic (locality does not simply override everything below it): a large enough
/// throughput gap can still beat a locality edge, because a sufficiently fast network-mounted agent really
/// is the better pick. See AgentRankerTests for the table-driven cases this is tuned against.
/// </summary>
public static class AgentRanker
{
    private const double LocalityWeight = 1.5;
    private const double ThroughputWeight = 1.0;
    private const double CapacityWeight = 1.0;
    private const double LoadWeight = 0.5;

    /// <summary>Realtime factor treated as "neither ahead nor behind" - the baseline <see cref="ThroughputWeight"/> is scaled around.</summary>
    private const double ThroughputBaseline = 1.0;

    /// <summary>Scores one candidate. Pure: same input always yields the same output.</summary>
    public static AgentRankingResult Score(AgentRankingInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var localityTerm = input.Local switch
        {
            true => LocalityWeight,
            false => -LocalityWeight,
            null => 0.0,
        };

        var throughputTerm = input.MeasuredSpeed is { } speed
            ? (speed - ThroughputBaseline) * ThroughputWeight
            : 0.0;

        var capacityTerm = input.SpareCapacityFraction * CapacityWeight;

        var loadTerm = input.Load is { } load ? -load * LoadWeight : 0.0;

        var score = localityTerm + throughputTerm + capacityTerm + loadTerm;

        var localityLabel = input.Local switch { true => "local", false => "remote", null => "unknown" };
        var speedLabel = input.MeasuredSpeed is { } measured
            ? measured.ToString("0.00", CultureInfo.InvariantCulture) + "x"
            : "unmeasured";
        var loadLabel = input.Load is { } reportedLoad
            ? reportedLoad.ToString("0.00", CultureInfo.InvariantCulture)
            : "unreported";

        var reason = string.Format(
            CultureInfo.InvariantCulture,
            "{0}: locality={1} ({2:+0.00;-0.00;0.00}) + speed={3} ({4:+0.00;-0.00;0.00}) + spare={5:0.00} ({6:+0.00;-0.00;0.00}) + load={7} ({8:+0.00;-0.00;0.00}) = {9:+0.00;-0.00;0.00}",
            input.AgentName,
            localityLabel,
            localityTerm,
            speedLabel,
            throughputTerm,
            input.SpareCapacityFraction,
            capacityTerm,
            loadLabel,
            loadTerm,
            score);

        return new AgentRankingResult(score, reason);
    }
}
