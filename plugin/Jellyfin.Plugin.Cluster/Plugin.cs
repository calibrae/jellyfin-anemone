using Jellyfin.Plugin.Cluster.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Cluster;

/// <summary>Cluster plugin entry point — offloads live transcodes to jfc-agents.</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "Cluster";

    public override Guid Id => Guid.Parse("7d0c3a4e-2f5b-4c8a-9e1d-6b2f0a9c1e77");

    public override string Description => "Offload live transcodes to remote ffmpeg agents (jfc-agent).";

    public static Plugin? Instance { get; private set; }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
            },
        ];
    }
}
