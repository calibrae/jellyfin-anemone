using Jellyfin.Plugin.Cluster.Contracts;

namespace Jellyfin.Plugin.Cluster.Agents;

/// <summary>STUB — replaced by the hub agent. Holds agent connections and implements placement.</summary>
public sealed class AgentHub : IAgentRegistry
{
    public IReadOnlyList<IAgentConnection> Agents => Array.Empty<IAgentConnection>();

    public IAgentConnection? Pick(JobRequirements requirements) => null;
}
