using Jellyfin.Plugin.Anemone.Agents;

namespace Jellyfin.Plugin.Anemone.Tests.Agents;

/// <summary>
/// Direct, table-driven tests of the pure scoring function - <see cref="AgentHubPickTests"/> covers the
/// same behaviours through <see cref="AgentHub.CandidatesFrom"/> end to end; these pin down the formula
/// itself with exact numbers so a future change to the weights is a deliberate, visible diff.
/// </summary>
public class AgentRankerTests
{
    private static AgentRankingInput Input(
        string name = "agent",
        bool? local = null,
        double? measuredSpeed = null,
        double spareCapacityFraction = 0.5,
        double? load = null) => new(name, local, measuredSpeed, spareCapacityFraction, load);

    [Fact]
    public void Score_KnownLocal_IsHigherThanUnknown_IsHigherThanKnownRemote()
    {
        var local = AgentRanker.Score(Input(local: true));
        var unknown = AgentRanker.Score(Input(local: null));
        var remote = AgentRanker.Score(Input(local: false));

        Assert.True(local.Score > unknown.Score);
        Assert.True(unknown.Score > remote.Score);
    }

    [Fact]
    public void Score_UnknownLocality_ContributesNothing()
    {
        var unknown = AgentRanker.Score(Input(local: null, measuredSpeed: null, spareCapacityFraction: 0.5, load: null));

        // Locality (0) + throughput (0, unmeasured) + spare (0.5 * 1.0) + load (0, unreported) = 0.5.
        Assert.Equal(0.5, unknown.Score, precision: 9);
    }

    [Fact]
    public void Score_UnmeasuredThroughput_ContributesNothing()
    {
        var unmeasured = AgentRanker.Score(Input(measuredSpeed: null, spareCapacityFraction: 0.5));
        var measuredAtBaseline = AgentRanker.Score(Input(measuredSpeed: 1.0, spareCapacityFraction: 0.5));

        // 1.0x (real-time, "just keeping up") is the throughput baseline - contributing nothing is
        // identical to not having measured anything at all.
        Assert.Equal(measuredAtBaseline.Score, unmeasured.Score);
    }

    [Fact]
    public void Score_FasterThanBaseline_ScoresHigherThanUnmeasured()
    {
        var unmeasured = AgentRanker.Score(Input(measuredSpeed: null));
        var fast = AgentRanker.Score(Input(measuredSpeed: 2.0));

        Assert.True(fast.Score > unmeasured.Score);
    }

    [Fact]
    public void Score_SlowerThanBaseline_ScoresLowerThanUnmeasured()
    {
        // The important part: this is LOWER than unmeasured, but unmeasured itself is not penalised - an
        // agent only pays a throughput cost once it has actually proven itself slow.
        var unmeasured = AgentRanker.Score(Input(measuredSpeed: null));
        var slow = AgentRanker.Score(Input(measuredSpeed: 0.5));

        Assert.True(unmeasured.Score > slow.Score);
    }

    [Fact]
    public void Score_MoreSpareCapacity_ScoresHigher()
    {
        var loaded = AgentRanker.Score(Input(spareCapacityFraction: 0.1));
        var idle = AgentRanker.Score(Input(spareCapacityFraction: 0.9));

        Assert.True(idle.Score > loaded.Score);
    }

    [Fact]
    public void Score_HigherReportedLoad_ScoresLower()
    {
        var idle = AgentRanker.Score(Input(load: 0.1));
        var busy = AgentRanker.Score(Input(load: 0.9));

        Assert.True(idle.Score > busy.Score);
    }

    [Fact]
    public void Score_UnreportedLoad_ContributesNothing()
    {
        var unreported = AgentRanker.Score(Input(load: null));
        var reportedIdle = AgentRanker.Score(Input(load: 0.0));

        Assert.Equal(reportedIdle.Score, unreported.Score);
    }

    [Fact]
    public void Score_LocalityOutweighsLoad_ButLoadStillBreaksATieWithinTheSameLocalityTier()
    {
        // Locality has the largest single swing (+/-1.5); load's is the smallest (0..-0.5), so within the
        // same locality/throughput tier, load only ever breaks close calls - it never flips an agent from
        // "worse locality, less busy" into "the better overall pick".
        var localButBusy = AgentRanker.Score(Input(local: true, load: 1.0));
        var remoteButIdle = AgentRanker.Score(Input(local: false, load: 0.0));

        Assert.True(localButBusy.Score > remoteButIdle.Score);
    }

    [Fact]
    public void Score_ExactValues_MatchTheDocumentedWeights()
    {
        // Pins the formula itself: locality +/-1.5, throughput (speed-1.0)*1.0, spare*1.0, load*-0.5.
        var result = AgentRanker.Score(new AgentRankingInput("pinned", true, 2.0, 0.75, 0.4));

        Assert.Equal(1.5 + (1.0 * 1.0) + (0.75 * 1.0) + (-0.4 * 0.5), result.Score, precision: 9);
    }

    [Fact]
    public void Score_Reason_MentionsAgentNameAndEveryTerm()
    {
        var result = AgentRanker.Score(new AgentRankingInput("trish", true, 2.0, 0.75, 0.4));

        Assert.Contains("trish", result.Reason, StringComparison.Ordinal);
        Assert.Contains("locality=local", result.Reason, StringComparison.Ordinal);
        Assert.Contains("speed=2.00x", result.Reason, StringComparison.Ordinal);
        Assert.Contains("spare=0.75", result.Reason, StringComparison.Ordinal);
        Assert.Contains("load=0.40", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Score_Reason_LabelsUnknownUnmeasuredAndUnreportedDistinctly()
    {
        var result = AgentRanker.Score(new AgentRankingInput("fresh", null, null, 0.5, null));

        Assert.Contains("locality=unknown", result.Reason, StringComparison.Ordinal);
        Assert.Contains("speed=unmeasured", result.Reason, StringComparison.Ordinal);
        Assert.Contains("load=unreported", result.Reason, StringComparison.Ordinal);
    }
}
