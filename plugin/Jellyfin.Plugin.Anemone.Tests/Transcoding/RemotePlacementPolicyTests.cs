using Jellyfin.Plugin.Anemone.Transcoding;

namespace Jellyfin.Plugin.Anemone.Tests.Transcoding;

/// <summary>Direct tests of the PreferRemote/LocalMaxSessions policy - see AnemoneTranscodeManagerTests for the end-to-end version.</summary>
public class RemotePlacementPolicyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(100)]
    public void ShouldConsultRouter_PreferRemoteTrue_AlwaysConsults(int activeLocalJobs)
    {
        Assert.True(RemotePlacementPolicy.ShouldConsultRouter(preferRemote: true, localMaxSessions: 2, activeLocalJobs: activeLocalJobs));
    }

    [Fact]
    public void ShouldConsultRouter_PreferRemoteFalse_NoLocalJobsYet_StaysLocal()
    {
        Assert.False(RemotePlacementPolicy.ShouldConsultRouter(preferRemote: false, localMaxSessions: 2, activeLocalJobs: 0));
    }

    [Fact]
    public void ShouldConsultRouter_PreferRemoteFalse_BelowCap_StaysLocal()
    {
        Assert.False(RemotePlacementPolicy.ShouldConsultRouter(preferRemote: false, localMaxSessions: 2, activeLocalJobs: 1));
    }

    [Fact]
    public void ShouldConsultRouter_PreferRemoteFalse_AtCap_Consults()
    {
        Assert.True(RemotePlacementPolicy.ShouldConsultRouter(preferRemote: false, localMaxSessions: 2, activeLocalJobs: 2));
    }

    [Fact]
    public void ShouldConsultRouter_PreferRemoteFalse_AboveCap_Consults()
    {
        Assert.True(RemotePlacementPolicy.ShouldConsultRouter(preferRemote: false, localMaxSessions: 2, activeLocalJobs: 5));
    }

    [Fact]
    public void ShouldConsultRouter_PreferRemoteFalse_ZeroLocalMaxSessions_AlwaysConsults()
    {
        // "Keep zero jobs local" is a valid (if odd) way to say "always prefer remote" without flipping
        // PreferRemote itself.
        Assert.True(RemotePlacementPolicy.ShouldConsultRouter(preferRemote: false, localMaxSessions: 0, activeLocalJobs: 0));
    }
}
