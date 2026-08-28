using Jellyfin.Plugin.Cluster.Contracts;

namespace Jellyfin.Plugin.Cluster.Ingest;

/// <summary>STUB — replaced by the hub agent.</summary>
public sealed class IngestTokenStore : IIngestTokenStore
{
    public string Issue(string jobId, string targetDirectory, string filePrefix) => throw new NotImplementedException();

    public bool TryValidate(string jobId, string bearerToken, out IngestGrant grant)
    {
        grant = null!;
        return false;
    }

    public void Revoke(string jobId)
    {
    }
}
