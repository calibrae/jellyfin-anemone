using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Dlna;

namespace Jellyfin.Plugin.Cluster.Transcoding;

/// <summary>STUB — replaced by the core agent with a fork of upstream TranscodeManager (v10.11.0).</summary>
public sealed class ClusterTranscodeManager : ITranscodeManager
{
    public TranscodingJob? GetTranscodingJob(string playSessionId) => throw new NotImplementedException();

    public TranscodingJob? GetTranscodingJob(string path, TranscodingJobType type) => throw new NotImplementedException();

    public void PingTranscodingJob(string playSessionId, bool? isUserPaused) => throw new NotImplementedException();

    public Task KillTranscodingJobs(string deviceId, string? playSessionId, Func<string, bool> deleteFiles) => throw new NotImplementedException();

    public void ReportTranscodingProgress(TranscodingJob job, StreamState state, TimeSpan? transcodingPosition, float? framerate, double? percentComplete, long? bytesTranscoded, int? bitRate) => throw new NotImplementedException();

    public Task<TranscodingJob> StartFfMpeg(StreamState state, string outputPath, string commandLineArguments, Guid userId, TranscodingJobType transcodingJobType, CancellationTokenSource cancellationTokenSource, string? workingDirectory = null) => throw new NotImplementedException();

    public TranscodingJob OnTranscodeBeginRequest(string path, TranscodingJobType type) => throw new NotImplementedException();

    public void OnTranscodeEndRequest(TranscodingJob job) => throw new NotImplementedException();

    public ValueTask<IDisposable> LockAsync(string outputPath, CancellationToken cancellationToken) => throw new NotImplementedException();
}
