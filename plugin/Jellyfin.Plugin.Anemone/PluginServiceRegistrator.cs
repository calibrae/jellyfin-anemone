using Jellyfin.Plugin.Anemone.Agents;
using Jellyfin.Plugin.Anemone.Contracts;
using Jellyfin.Plugin.Anemone.Ingest;
using Jellyfin.Plugin.Anemone.Transcoding;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Anemone;

/// <summary>
/// Registers the plugin services. Runs after core registration (ApplicationHost.cs:460 → :462 on v10.11.0),
/// so the ITranscodeManager registration below replaces Jellyfin's own TranscodeManager.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<AgentHub>();
        serviceCollection.AddSingleton<AgentWebSocketEndpoint>();
        serviceCollection.AddSingleton<IngestHandler>();
        serviceCollection.AddSingleton<AnemoneListener>();
        serviceCollection.AddSingleton<IAgentRegistry>(sp => sp.GetRequiredService<AgentHub>());
        serviceCollection.AddSingleton<IIngestTokenStore, IngestTokenStore>();
        serviceCollection.AddSingleton<IJobRouter, JobRouter>();
        serviceCollection.AddSingleton<ITranscodeManager, AnemoneTranscodeManager>();

        // anemone (v2.2 throttling): AnemoneStatusController needs the concrete manager (GetThrottleStatus)
        // alongside ITranscodeManager - same "concrete type resolvable too" pattern as IAgentRegistry/AgentHub
        // above, just registered from the other direction since ITranscodeManager was already the primary
        // registration. Resolves to the SAME singleton instance ITranscodeManager already cached.
        serviceCollection.AddSingleton(sp => (AnemoneTranscodeManager)sp.GetRequiredService<ITranscodeManager>());

        serviceCollection.AddHostedService<AnemoneHostedService>();
    }
}
