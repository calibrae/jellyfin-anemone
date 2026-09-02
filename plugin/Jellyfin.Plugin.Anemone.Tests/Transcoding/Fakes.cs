using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;

namespace Jellyfin.Plugin.Anemone.Tests.Transcoding;

/// <summary>
/// Hand-written fake for StreamState's <see cref="ITranscodeManager"/> dependency - StreamState only
/// stores the reference and calls back into it from <c>ReportTranscodingProgress</c>/<c>Dispose</c>,
/// neither of which <see cref="Jellyfin.Plugin.Anemone.Transcoding.JobRouter.TryPlan"/> ever triggers.
/// Not moved to <c>Jellyfin.Plugin.Anemone.TestKit</c>: it's a one-off null object specific to
/// JobRouterTests's use of <see cref="StreamState"/>, and AnemoneTranscodeManager tests use the TestKit's
/// own <c>StreamStateBuilder.WithTranscodeManager(manager)</c> (the manager under test itself) instead.
/// </summary>
internal sealed class FakeTranscodeManagerForStreamState : ITranscodeManager
{
    public TranscodingJob? GetTranscodingJob(string playSessionId) => throw new NotImplementedException();

    public TranscodingJob? GetTranscodingJob(string path, TranscodingJobType type) => throw new NotImplementedException();

    public void PingTranscodingJob(string playSessionId, bool? isUserPaused) => throw new NotImplementedException();

    public Task KillTranscodingJobs(string deviceId, string? playSessionId, Func<string, bool> deleteFiles) => throw new NotImplementedException();

    public void ReportTranscodingProgress(TranscodingJob job, StreamState state, TimeSpan? transcodingPosition, float? framerate, double? percentComplete, long? bytesTranscoded, int? bitRate) => throw new NotImplementedException();

    public Task<TranscodingJob> StartFfMpeg(StreamState state, string outputPath, string commandLineArguments, Guid userId, TranscodingJobType transcodingJobType, CancellationTokenSource cancellationTokenSource, string? workingDirectory = null) => throw new NotImplementedException();

    public TranscodingJob? OnTranscodeBeginRequest(string path, TranscodingJobType type) => throw new NotImplementedException();

    public void OnTranscodeEndRequest(TranscodingJob job) => throw new NotImplementedException();

    public ValueTask<IDisposable> LockAsync(string outputPath, CancellationToken cancellationToken) => throw new NotImplementedException();
}
