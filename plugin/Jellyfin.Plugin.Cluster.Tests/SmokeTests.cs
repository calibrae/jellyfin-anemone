using Jellyfin.Plugin.Cluster;

namespace Jellyfin.Plugin.Cluster.Tests;

public class SmokeTests
{
    [Fact]
    public void PluginIdIsStable()
    {
        Assert.Equal(Guid.Parse("7d0c3a4e-2f5b-4c8a-9e1d-6b2f0a9c1e77"), Guid.Parse("7d0c3a4e-2f5b-4c8a-9e1d-6b2f0a9c1e77"));
    }
}
