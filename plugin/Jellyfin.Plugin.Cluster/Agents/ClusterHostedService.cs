using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Cluster.Agents;

/// <summary>STUB — replaced by the hub agent. Startup banner + agent reaper timer.</summary>
public sealed class ClusterHostedService(ILogger<ClusterHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("jfc: Cluster plugin loaded");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
