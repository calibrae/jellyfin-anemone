using Jellyfin.Plugin.Anemone.Agents;

namespace Jellyfin.Plugin.Anemone.Tests.Agents;

/// <summary>
/// Pins the trade-off between "has the media on its own disk" and "is faster", so that retuning the
/// ranking weights is a deliberate decision rather than a side effect. Reading the source over the network
/// is usually the larger transfer — the segments an agent sends back are already compressed — so a
/// network-mounted agent has to be substantially faster before it is the better choice.
/// </summary>
public class AgentRankerCrossoverTests
{
    private static double ScoreOf(bool? local, double? speed) =>
        AgentRanker.Score(new AgentRankingInput("a", local, speed, SpareCapacityFraction: 1.0, Load: null)).Score;

    [Fact]
    public void ANetworkMountedAgentMustBeAboutThreeTimesFasterToWin()
    {
        var localMedia = ScoreOf(local: true, speed: 1.0);

        Assert.True(ScoreOf(local: false, speed: AgentRanker.LocalityCrossoverSpeed * 0.9) < localMedia);
        Assert.True(ScoreOf(local: false, speed: AgentRanker.LocalityCrossoverSpeed * 1.1) > localMedia);
        Assert.InRange(AgentRanker.LocalityCrossoverSpeed, 2.0, 4.0);
    }

    [Fact]
    public void UnboundedRealtimeFactorsCannotSwampEveryOtherSignal()
    {
        // A realtime factor is unbounded and job-dependent (480p reports far higher than 4K on the same
        // box). Compression keeps a 50x agent from being ranked ~35 points above a 15x one, which would
        // make locality, capacity and load decorative.
        var fast = ScoreOf(local: false, speed: 50.0);
        var lessFast = ScoreOf(local: false, speed: 15.0);

        Assert.True(fast >= lessFast);
        Assert.True(fast - lessFast < 1.0, "the gap between two already-fast agents must stay small");
    }

    [Fact]
    public void AnUnmeasuredAgentIsNotTreatedAsSlow()
    {
        Assert.Equal(ScoreOf(local: null, speed: 1.0), ScoreOf(local: null, speed: null), 6);
    }
}
