using Jellyfin.Plugin.Cluster.Contracts;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;

namespace Jellyfin.Plugin.Cluster.Transcoding;

/// <summary>STUB — replaced by the core agent. Always routes local.</summary>
public sealed class JobRouter : IJobRouter
{
    public RoutePlan? TryPlan(StreamState state, string outputPath, string commandLineArguments, TranscodingJobType jobType) => null;
}
