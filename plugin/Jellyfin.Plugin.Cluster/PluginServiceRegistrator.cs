using Jellyfin.Plugin.Cluster.Agents;
using Jellyfin.Plugin.Cluster.Contracts;
using Jellyfin.Plugin.Cluster.Ingest;
using Jellyfin.Plugin.Cluster.Transcoding;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Cluster;

/// <summary>
/// Registers the plugin services. Runs after core registration (ApplicationHost.cs:460 → :462 on v10.11.0),
/// so the ITranscodeManager registration below replaces Jellyfin's own TranscodeManager.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<AgentHub>();
        serviceCollection.AddSingleton<IAgentRegistry>(sp => sp.GetRequiredService<AgentHub>());
        serviceCollection.AddSingleton<IIngestTokenStore, IngestTokenStore>();
        serviceCollection.AddSingleton<IJobRouter, JobRouter>();
        serviceCollection.AddSingleton<ITranscodeManager, ClusterTranscodeManager>();
        serviceCollection.AddHostedService<ClusterHostedService>();
    }
}
