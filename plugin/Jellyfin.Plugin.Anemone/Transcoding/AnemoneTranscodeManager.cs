// anemone: this file is a derivative of Jellyfin's own
// MediaBrowser.MediaEncoding/Transcoding/TranscodeManager.cs @ v10.11.0 (jellyfin-server,
// GPL-2.0-or-later) - kept as close to verbatim as possible, with every deviation marked by a
// "// anemone:" comment so this can be re-based against the next Jellyfin minor. The unmodified upstream
// file this was forked from is kept alongside it at docs/upstream-10.11.0/TranscodeManager.cs for that
// rebase, and its presence is why this repository as a whole is licensed GPL-2.0-or-later. See
// RESEARCH.md and PROTOCOL.md for why/what.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using Jellyfin.Plugin.Anemone.Contracts;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Anemone.Transcoding;

/// <inheritdoc cref="ITranscodeManager"/>
public sealed class AnemoneTranscodeManager : ITranscodeManager, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AnemoneTranscodeManager> _logger;
    private readonly IFileSystem _fileSystem;
    private readonly IApplicationPaths _appPaths;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly IUserManager _userManager;
    private readonly ISessionManager _sessionManager;
    private readonly EncodingHelper _encodingHelper;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IAttachmentExtractor _attachmentExtractor;

    // anemone: added dependencies — job placement + ingest token lifecycle.
    private readonly IJobRouter _router;
    private readonly IIngestTokenStore _tokenStore;

    private readonly List<TranscodingJob> _activeTranscodingJobs = new();

    // anemone: AsyncKeyedLocker<string> (AsyncKeyedLock package) replaced with an in-file KeyedLock so we
    // don't ship a second copy of an assembly Jellyfin doesn't already load. Same ref-counted
    // one-semaphore-per-key semantics as upstream's _transcodingLocks.
    private readonly KeyedLock _transcodingLocks = new();

    // anemone: jobs currently running on an agent, keyed by TranscodingJob.Id (== RemoteJobSpec.Id ==
    // IRemoteJob.Id). A job appears here from the moment StartJobAsync succeeds until OnExited fires.
    private readonly ConcurrentDictionary<string, IRemoteJob> _remoteJobs = new(StringComparer.Ordinal);

    private readonly Version _maxFFmpegCkeyPauseSupported = new Version(6, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="AnemoneTranscodeManager"/> class.
    /// </summary>
    /// <param name="loggerFactory">The <see cref="ILoggerFactory"/>.</param>
    /// <param name="fileSystem">The <see cref="IFileSystem"/>.</param>
    /// <param name="appPaths">The <see cref="IApplicationPaths"/>.</param>
    /// <param name="serverConfigurationManager">The <see cref="IServerConfigurationManager"/>.</param>
    /// <param name="userManager">The <see cref="IUserManager"/>.</param>
    /// <param name="sessionManager">The <see cref="ISessionManager"/>.</param>
    /// <param name="encodingHelper">The <see cref="EncodingHelper"/>.</param>
    /// <param name="mediaEncoder">The <see cref="IMediaEncoder"/>.</param>
    /// <param name="mediaSourceManager">The <see cref="IMediaSourceManager"/>.</param>
    /// <param name="attachmentExtractor">The <see cref="IAttachmentExtractor"/>.</param>
    /// <param name="router">anemone: decides local-vs-remote and rewrites the command line.</param>
    /// <param name="tokenStore">anemone: mints/validates/revokes ingest bearer tokens.</param>
    public AnemoneTranscodeManager(
        ILoggerFactory loggerFactory,
        IFileSystem fileSystem,
        IApplicationPaths appPaths,
        IServerConfigurationManager serverConfigurationManager,
        IUserManager userManager,
        ISessionManager sessionManager,
        EncodingHelper encodingHelper,
        IMediaEncoder mediaEncoder,
        IMediaSourceManager mediaSourceManager,
        IAttachmentExtractor attachmentExtractor,
        IJobRouter router,
        IIngestTokenStore tokenStore)
    {
        _loggerFactory = loggerFactory;
        _fileSystem = fileSystem;
        _appPaths = appPaths;
        _serverConfigurationManager = serverConfigurationManager;
        _userManager = userManager;
        _sessionManager = sessionManager;
        _encodingHelper = encodingHelper;
        _mediaEncoder = mediaEncoder;
        _mediaSourceManager = mediaSourceManager;
        _attachmentExtractor = attachmentExtractor;
        _router = router;
        _tokenStore = tokenStore;

        _logger = loggerFactory.CreateLogger<AnemoneTranscodeManager>();
        DeleteEncodedMediaCache();
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStart += OnPlaybackProgress;
    }

    /// <inheritdoc />
    public TranscodingJob? GetTranscodingJob(string playSessionId)
    {
        lock (_activeTranscodingJobs)
        {
            return _activeTranscodingJobs.FirstOrDefault(j => string.Equals(j.PlaySessionId, playSessionId, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <inheritdoc />
    public TranscodingJob? GetTranscodingJob(string path, TranscodingJobType type)
    {
        lock (_activeTranscodingJobs)
        {
            return _activeTranscodingJobs.FirstOrDefault(j => j.Type == type && string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <inheritdoc />
    public void PingTranscodingJob(string playSessionId, bool? isUserPaused)
    {
        ArgumentException.ThrowIfNullOrEmpty(playSessionId);

        _logger.LogDebug("PingTranscodingJob PlaySessionId={0} isUsedPaused: {1}", playSessionId, isUserPaused);

        List<TranscodingJob> jobs;

        lock (_activeTranscodingJobs)
        {
            // This is really only needed for HLS.
            // Progressive streams can stop on their own reliably.
            jobs = _activeTranscodingJobs.Where(j => string.Equals(playSessionId, j.PlaySessionId, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        foreach (var job in jobs)
        {
            if (isUserPaused.HasValue)
            {
                _logger.LogDebug("Setting job.IsUserPaused to {0}. jobId: {1}", isUserPaused, job.Id);
                job.IsUserPaused = isUserPaused.Value;
            }

            PingTimer(job, true);
        }
    }

    private void PingTimer(TranscodingJob job, bool isProgressCheckIn)
    {
        if (job.HasExited)
        {
            job.StopKillTimer();
            return;
        }

        var timerDuration = 10000;

        if (job.Type != TranscodingJobType.Progressive)
        {
            timerDuration = 60000;
        }

        job.PingTimeout = timerDuration;
        job.LastPingDate = DateTime.UtcNow;

        // Don't start the timer for playback checkins with progressive streaming
        if (job.Type != TranscodingJobType.Progressive || !isProgressCheckIn)
        {
            job.StartKillTimer(OnTranscodeKillTimerStopped);
        }
        else
        {
            job.ChangeKillTimerIfStarted();
        }
    }

    private async void OnTranscodeKillTimerStopped(object? state)
    {
        var job = state as TranscodingJob ?? throw new ArgumentException($"{nameof(state)} is not of type {nameof(TranscodingJob)}", nameof(state));
        if (!job.HasExited && job.Type != TranscodingJobType.Progressive)
        {
            var timeSinceLastPing = (DateTime.UtcNow - job.LastPingDate).TotalMilliseconds;

            if (timeSinceLastPing < job.PingTimeout)
            {
                job.StartKillTimer(OnTranscodeKillTimerStopped, job.PingTimeout);
                return;
            }
        }

        _logger.LogInformation("Transcoding kill timer stopped for JobId {0} PlaySessionId {1}. Killing transcoding", job.Id, job.PlaySessionId);

        await KillTranscodingJob(job, true, path => true).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task KillTranscodingJobs(string deviceId, string? playSessionId, Func<string, bool> deleteFiles)
    {
        var jobs = new List<TranscodingJob>();

        lock (_activeTranscodingJobs)
        {
            // This is really only needed for HLS.
            // Progressive streams can stop on their own reliably.
            jobs.AddRange(_activeTranscodingJobs.Where(j => string.IsNullOrWhiteSpace(playSessionId)
                ? string.Equals(deviceId, j.DeviceId, StringComparison.OrdinalIgnoreCase)
                : string.Equals(playSessionId, j.PlaySessionId, StringComparison.OrdinalIgnoreCase)));
        }

        return Task.WhenAll(GetKillJobs());

        IEnumerable<Task> GetKillJobs()
        {
            foreach (var job in jobs)
            {
                yield return KillTranscodingJob(job, false, deleteFiles);
            }
        }
    }

    private async Task KillTranscodingJob(TranscodingJob job, bool closeLiveStream, Func<string, bool> delete)
    {
        job.DisposeKillTimer();

        _logger.LogDebug("KillTranscodingJob - JobId {0} PlaySessionId {1}. Killing transcoding", job.Id, job.PlaySessionId);

        lock (_activeTranscodingJobs)
        {
            _activeTranscodingJobs.Remove(job);

            if (job.CancellationTokenSource?.IsCancellationRequested == false)
            {
#pragma warning disable CA1849 // Can't await in lock block
                job.CancellationTokenSource.Cancel();
#pragma warning restore CA1849
            }
        }

        // anemone: a remote job has no local Process — Stop() would NRE on process!.StandardInput if the job
        // hasn't exited yet (Process is unconditionally dereferenced there). Send the same "q" quit key
        // over the control channel instead, with the same 5s-then-kill grace upstream gives a local
        // ffmpeg. KillTranscodingJob is already async here (unlike TranscodingJob.Stop()), so there's no
        // deadlock risk in awaiting the socket write.
        if (job.Id is not null && _remoteJobs.TryGetValue(job.Id, out var remoteJob))
        {
            // Stop()'s throttler/cleaner calls are unconditional (not guarded by HasExited) — mirror them
            // here. TranscodingThrottler is always null for remote jobs today (kept for symmetry/in case
            // a future throttler fork attaches one); TranscodingSegmentCleaner is real and must be
            // stopped promptly rather than waiting for FinishRemoteJob's eventual job.Dispose().
            job.TranscodingThrottler?.Stop().GetAwaiter().GetResult();
            job.TranscodingSegmentCleaner?.Stop();
            await StopRemoteJobAsync(remoteJob).ConfigureAwait(false);
        }
        else
        {
            job.Stop();
        }

        if (delete(job.Path!))
        {
            await DeletePartialStreamFiles(job.Path!, job.Type, 0, 1500).ConfigureAwait(false);
        }

        if (closeLiveStream && !string.IsNullOrWhiteSpace(job.LiveStreamId))
        {
            await _sessionManager.CloseLiveStreamIfNeededAsync(job.LiveStreamId, job.PlaySessionId).ConfigureAwait(false);
        }
    }

    // anemone: mirrors TranscodingJob.Stop()'s "q then wait 5s then kill" contract, over the agent's
    // control channel instead of local stdin/Process.Kill().
    private async Task StopRemoteJobAsync(IRemoteJob remoteJob)
    {
        try
        {
            _logger.LogInformation("anemone: stopping remote job {Id} with q command", remoteJob.Id);
            await remoteJob.SendStdinAsync("q\n").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "anemone: error sending quit to remote job {Id}", remoteJob.Id);
        }

        var finished = await Task.WhenAny(remoteJob.Completion, Task.Delay(5000)).ConfigureAwait(false) == remoteJob.Completion;
        if (!finished)
        {
            _logger.LogInformation("anemone: killing remote job {Id} (did not exit within 5s of quit)", remoteJob.Id);
            try
            {
                await remoteJob.KillAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "anemone: error killing remote job {Id}", remoteJob.Id);
            }
        }
    }

    private async Task DeletePartialStreamFiles(string path, TranscodingJobType jobType, int retryCount, int delayMs)
    {
        if (retryCount >= 10)
        {
            return;
        }

        _logger.LogInformation("Deleting partial stream file(s) {Path}", path);

        await Task.Delay(delayMs).ConfigureAwait(false);

        try
        {
            if (jobType == TranscodingJobType.Progressive)
            {
                DeleteProgressivePartialStreamFiles(path);
            }
            else
            {
                DeleteHlsPartialStreamFiles(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Error deleting partial stream file(s) {Path}", path);

            await DeletePartialStreamFiles(path, jobType, retryCount + 1, 500).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting partial stream file(s) {Path}", path);
        }
    }

    private void DeleteProgressivePartialStreamFiles(string outputFilePath)
    {
        if (File.Exists(outputFilePath))
        {
            _fileSystem.DeleteFile(outputFilePath);
        }
    }

    private void DeleteHlsPartialStreamFiles(string outputFilePath)
    {
        var directory = Path.GetDirectoryName(outputFilePath)
                        ?? throw new ArgumentException("Path can't be a root directory.", nameof(outputFilePath));

        var name = Path.GetFileNameWithoutExtension(outputFilePath);

        var filesToDelete = _fileSystem.GetFilePaths(directory)
            .Where(f => f.Contains(name, StringComparison.OrdinalIgnoreCase));

        List<Exception>? exs = null;
        foreach (var file in filesToDelete)
        {
            try
            {
                _logger.LogDebug("Deleting HLS file {0}", file);
                _fileSystem.DeleteFile(file);
            }
            catch (IOException ex)
            {
                (exs ??= new List<Exception>()).Add(ex);
                _logger.LogError(ex, "Error deleting HLS file {Path}", file);
            }
        }

        if (exs is not null)
        {
            throw new AggregateException("Error deleting HLS files", exs);
        }
    }

    /// <inheritdoc />
    public void ReportTranscodingProgress(
        TranscodingJob job,
        StreamState state,
        TimeSpan? transcodingPosition,
        float? framerate,
        double? percentComplete,
        long? bytesTranscoded,
        int? bitRate)
    {
        var ticks = transcodingPosition?.Ticks;

        if (job is not null)
        {
            job.Framerate = framerate;
            job.CompletionPercentage = percentComplete;
            job.TranscodingPositionTicks = ticks;
            job.BytesTranscoded = bytesTranscoded;
            job.BitRate = bitRate;
        }

        var deviceId = state.Request.DeviceId;

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var audioCodec = state.ActualOutputAudioCodec;
            var videoCodec = state.ActualOutputVideoCodec;
            var hardwareAccelerationType = _serverConfigurationManager.GetEncodingOptions().HardwareAccelerationType;

            _sessionManager.ReportTranscodingInfo(deviceId, new TranscodingInfo
            {
                Bitrate = bitRate ?? state.TotalOutputBitrate,
                AudioCodec = audioCodec,
                VideoCodec = videoCodec,
                Container = state.OutputContainer,
                Framerate = framerate,
                CompletionPercentage = percentComplete,
                Width = state.OutputWidth,
                Height = state.OutputHeight,
                AudioChannels = state.OutputAudioChannels,
                IsAudioDirect = EncodingHelper.IsCopyCodec(state.OutputAudioCodec),
                IsVideoDirect = EncodingHelper.IsCopyCodec(state.OutputVideoCodec),
                HardwareAccelerationType = hardwareAccelerationType,
                TranscodeReasons = state.TranscodeReasons
            });
        }
    }

    /// <inheritdoc />
    public async Task<TranscodingJob> StartFfMpeg(
        StreamState state,
        string outputPath,
        string commandLineArguments,
        Guid userId,
        TranscodingJobType transcodingJobType,
        CancellationTokenSource cancellationTokenSource,
        string? workingDirectory = null)
    {
        var directory = Path.GetDirectoryName(outputPath) ?? throw new ArgumentException($"Provided path ({outputPath}) is not valid.", nameof(outputPath));
        Directory.CreateDirectory(directory);

        await AcquireResources(state, cancellationTokenSource).ConfigureAwait(false);

        if (state.VideoRequest is not null && !EncodingHelper.IsCopyCodec(state.OutputVideoCodec))
        {
            var user = userId.IsEmpty() ? null : _userManager.GetUserById(userId);
            if (user is not null && !user.HasPermission(PermissionKind.EnableVideoPlaybackTranscoding))
            {
                OnTranscodeFailedToStart(outputPath, transcodingJobType, state);

                throw new ArgumentException("User does not have access to video transcoding.");
            }
        }

        ArgumentException.ThrowIfNullOrEmpty(_mediaEncoder.EncoderPath);

        // If subtitles get burned in fonts may need to be extracted from the media file
        if (state.SubtitleStream is not null && state.SubtitleDeliveryMethod == SubtitleDeliveryMethod.Encode)
        {
            if (state.MediaSource.VideoType == VideoType.Dvd || state.MediaSource.VideoType == VideoType.BluRay)
            {
                var concatPath = Path.Join(_appPaths.CachePath, "concat", state.MediaSource.Id + ".concat");
                await _attachmentExtractor.ExtractAllAttachments(concatPath, state.MediaSource, cancellationTokenSource.Token).ConfigureAwait(false);
            }
            else
            {
                await _attachmentExtractor.ExtractAllAttachments(state.MediaPath, state.MediaSource, cancellationTokenSource.Token).ConfigureAwait(false);
            }

            if (state.SubtitleStream.IsExternal && Path.GetExtension(state.SubtitleStream.Path.AsSpan()).Equals(".mks", StringComparison.OrdinalIgnoreCase))
            {
                await _attachmentExtractor.ExtractAllAttachments(state.SubtitleStream.Path, state.MediaSource, cancellationTokenSource.Token).ConfigureAwait(false);
            }
        }

        // anemone: try to place the job on an agent before falling back to the upstream local-process path.
        var cfg = Plugin.Instance?.Configuration;
        RoutePlan? plan = null;
        if (cfg is { Enabled: true } && transcodingJobType == TranscodingJobType.Hls)
        {
            plan = _router.TryPlan(state, outputPath, commandLineArguments, transcodingJobType);
        }

        if (plan is not null && cfg!.DryRun)
        {
            _logger.LogInformation("anemone: dry-run — would route {Path} to agent {Agent} ({Reason})", outputPath, plan.Agent.Info.Name, plan.Reason);
            _tokenStore.Revoke(plan.Spec.Id);
            plan = null;
        }

        if (plan is not null)
        {
            var remoteJob = await TryStartRemoteAsync(plan, state, outputPath, commandLineArguments, transcodingJobType, cancellationTokenSource).ConfigureAwait(false);
            if (remoteJob is not null)
            {
                return remoteJob;
            }

            // else: TryStartRemoteAsync already cleaned up (activeTranscodingJobs, remoteJobs, token) —
            // fall through to the local path exactly as if routing had never been attempted.
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = false,

                // Must consume both stdout and stderr or deadlocks may occur
                // RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                FileName = _mediaEncoder.EncoderPath,
                Arguments = commandLineArguments,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? string.Empty : workingDirectory,
                ErrorDialog = false
            },
            EnableRaisingEvents = true
        };

        var transcodingJob = OnTranscodeBeginning(
            outputPath,
            state.Request.PlaySessionId,
            state.MediaSource.LiveStreamId,
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            transcodingJobType,
            process,
            state.Request.DeviceId,
            state,
            cancellationTokenSource);

        _logger.LogInformation("{Filename} {Arguments}", process.StartInfo.FileName, process.StartInfo.Arguments);

        var logFilePrefix = "FFmpeg.Transcode-";
        if (state.VideoRequest is not null
            && EncodingHelper.IsCopyCodec(state.OutputVideoCodec))
        {
            logFilePrefix = EncodingHelper.IsCopyCodec(state.OutputAudioCodec)
                ? "FFmpeg.Remux-"
                : "FFmpeg.DirectStream-";
        }

        if (state.VideoRequest is null && EncodingHelper.IsCopyCodec(state.OutputAudioCodec))
        {
            logFilePrefix = "FFmpeg.Remux-";
        }

        var logFilePath = Path.Combine(
            _serverConfigurationManager.ApplicationPaths.LogDirectoryPath,
            $"{logFilePrefix}{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{state.Request.MediaSourceId}_{Guid.NewGuid().ToString()[..8]}.log");

        // FFmpeg writes debug/error info to stderr. This is useful when debugging so let's put it in the log directory.
        Stream logStream = new FileStream(
            logFilePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            IODefaults.FileStreamBufferSize,
            FileOptions.Asynchronous);

        await JsonSerializer.SerializeAsync(logStream, state.MediaSource, cancellationToken: cancellationTokenSource.Token).ConfigureAwait(false);
        var commandLineLogMessageBytes = Encoding.UTF8.GetBytes(
            Environment.NewLine
            + Environment.NewLine
            + process.StartInfo.FileName + " " + process.StartInfo.Arguments
            + Environment.NewLine
            + Environment.NewLine);

        await logStream.WriteAsync(commandLineLogMessageBytes, cancellationTokenSource.Token).ConfigureAwait(false);

        process.Exited += (_, _) => OnFfMpegProcessExited(process, transcodingJob, state);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting FFmpeg");
            OnTranscodeFailedToStart(outputPath, transcodingJobType, state);

            throw;
        }

        _logger.LogDebug("Launched FFmpeg process");
        state.TranscodingJob = transcodingJob;

        // Important - don't await the log task or we won't be able to kill FFmpeg when the user stops playback
        _ = new JobLogger(_logger).StartStreamingLog(state, process.StandardError, logStream);

        // Wait for the file to exist before proceeding
        var ffmpegTargetFile = state.WaitForPath ?? outputPath;
        _logger.LogDebug("Waiting for the creation of {0}", ffmpegTargetFile);
        while (!File.Exists(ffmpegTargetFile) && !transcodingJob.HasExited)
        {
            await Task.Delay(100, cancellationTokenSource.Token).ConfigureAwait(false);
        }

        _logger.LogDebug("File {0} created or transcoding has finished", ffmpegTargetFile);

        if (state.IsInputVideo && transcodingJob.Type == TranscodingJobType.Progressive && !transcodingJob.HasExited)
        {
            await Task.Delay(1000, cancellationTokenSource.Token).ConfigureAwait(false);

            if (state.ReadInputAtNativeFramerate && !transcodingJob.HasExited)
            {
                await Task.Delay(1500, cancellationTokenSource.Token).ConfigureAwait(false);
            }
        }

        if (!transcodingJob.HasExited)
        {
            StartThrottler(state, transcodingJob);
            StartSegmentCleaner(state, transcodingJob);
        }
        else if (transcodingJob.ExitCode != 0)
        {
            throw new FfmpegException(string.Format(CultureInfo.InvariantCulture, "FFmpeg exited with code {0}", transcodingJob.ExitCode));
        }

        _logger.LogDebug("StartFfMpeg() finished successfully");

        return transcodingJob;
    }

    // anemone: everything below in this region is new — the remote-job mirror of the local flow above.

    /// <summary>
    /// Mirrors the local branch of <see cref="StartFfMpeg"/> with no local <see cref="Process"/>: sends
    /// the job to <paramref name="plan"/>'s agent, wires its stderr into the same <c>JobLogger</c> path
    /// local jobs use, and waits for the first output file exactly like upstream. Returns null (after
    /// cleaning up any bookkeeping it already did) if the agent never produces output, so the caller
    /// falls through to a normal local start of the same job.
    /// </summary>
    private async Task<TranscodingJob?> TryStartRemoteAsync(
        RoutePlan plan,
        StreamState state,
        string outputPath,
        string commandLineArguments,
        TranscodingJobType transcodingJobType,
        CancellationTokenSource cancellationTokenSource)
    {
        var jobId = plan.Spec.Id;

        var transcodingJob = OnTranscodeBeginning(
            outputPath,
            state.Request.PlaySessionId,
            state.MediaSource.LiveStreamId,
            jobId,
            transcodingJobType,
            process: null,
            state.Request.DeviceId,
            state,
            cancellationTokenSource);

        _logger.LogInformation("anemone: routing {Path} to agent {Agent}: {Reason}", outputPath, plan.Agent.Info.Name, plan.Reason);

        var logFilePrefix = "FFmpeg.Transcode-";
        if (state.VideoRequest is not null
            && EncodingHelper.IsCopyCodec(state.OutputVideoCodec))
        {
            logFilePrefix = EncodingHelper.IsCopyCodec(state.OutputAudioCodec)
                ? "FFmpeg.Remux-"
                : "FFmpeg.DirectStream-";
        }

        if (state.VideoRequest is null && EncodingHelper.IsCopyCodec(state.OutputAudioCodec))
        {
            logFilePrefix = "FFmpeg.Remux-";
        }

        var logFilePath = Path.Combine(
            _serverConfigurationManager.ApplicationPaths.LogDirectoryPath,
            $"{logFilePrefix}{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{state.Request.MediaSourceId}_{Guid.NewGuid().ToString()[..8]}.log");

        Stream logStream = new FileStream(
            logFilePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            IODefaults.FileStreamBufferSize,
            FileOptions.Asynchronous);

        await JsonSerializer.SerializeAsync(logStream, state.MediaSource, cancellationToken: cancellationTokenSource.Token).ConfigureAwait(false);

        var argvLine = ArgumentLine.Join(plan.Spec.Argv);
        var commandLineLogMessageBytes = Encoding.UTF8.GetBytes(
            Environment.NewLine
            + Environment.NewLine
            + _mediaEncoder.EncoderPath + " " + commandLineArguments
            + Environment.NewLine
            + Environment.NewLine
            + $"anemone: routed to agent {plan.Agent.Info.Name}: {argvLine}"
            + Environment.NewLine
            + Environment.NewLine);

        await logStream.WriteAsync(commandLineLogMessageBytes, cancellationTokenSource.Token).ConfigureAwait(false);

        // No backpressure: RemoteJobSink.OnStderrLine must never block the agent connection's read loop.
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
        var sink = new RemoteJobSink(
            pipe.Writer,
            pid => _logger.LogDebug("anemone: remote job {Id} started, agent pid {Pid}", jobId, pid),
            (exitCode, error) =>
            {
                transcodingJob.HasExited = true;
                transcodingJob.ExitCode = exitCode;
                if (!string.IsNullOrEmpty(error))
                {
                    _logger.LogWarning("anemone: remote job {Id} reported error: {Error}", jobId, error);
                }

                // Heavier bookkeeping (state.Dispose(), etc.) must not run on the agent's socket read loop.
                _ = Task.Run(() => FinishRemoteJob(transcodingJob, state));
            });

        IRemoteJob remoteJob;
        try
        {
            var timeoutSeconds = Math.Max(1, Plugin.Instance?.Configuration.AgentStartTimeoutSeconds ?? 15);
            using var startCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationTokenSource.Token);
            startCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            remoteJob = await plan.Agent.StartJobAsync(plan.Spec, sink, startCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "anemone: failed to start job on agent {Agent}, falling back to local", plan.Agent.Info.Name);
            CleanUpFailedRemoteStart(transcodingJob, logStream, plan);
            return null;
        }

        _remoteJobs[jobId] = remoteJob;
        state.TranscodingJob = transcodingJob;

        // Important - don't await the log task or we won't be able to kill the job when the user stops playback
        _ = new JobLogger(_logger).StartStreamingLog(state, new StreamReader(pipe.Reader.AsStream(), Encoding.UTF8), logStream);

        // Wait for the file to exist before proceeding — same polling loop as the local path.
        var ffmpegTargetFile = state.WaitForPath ?? outputPath;
        _logger.LogDebug("anemone: waiting for the creation of {0} (remote)", ffmpegTargetFile);
        while (!File.Exists(ffmpegTargetFile) && !transcodingJob.HasExited)
        {
            await Task.Delay(100, cancellationTokenSource.Token).ConfigureAwait(false);
        }

        if (transcodingJob.HasExited && !File.Exists(ffmpegTargetFile))
        {
            // The agent died before ever producing the first segment — fall back to a transparent
            // local start of the same job. _remoteJobs.TryRemove is the mutex deciding who owns
            // teardown: OnExited already raced us here and scheduled FinishRemoteJob on a background
            // task (see the sink above) — whichever of the two removes the entry first "wins":
            //  - we win  -> FinishRemoteJob's own TryRemove will fail and it skips FinishJob/state.Dispose()
            //               entirely, leaving `state` untouched and safe for the local retry below.
            //  - we lose -> FinishRemoteJob already (or is about to) dispose `state`; reusing it for a
            //               local retry would be unsafe, so this becomes a hard failure instead, same
            //               as upstream's own "exited with a bad code" path.
            if (!_remoteJobs.TryRemove(jobId, out _))
            {
                _logger.LogError("anemone: remote job on {Agent} exited before producing output, and its teardown already ran — cannot fall back safely", plan.Agent.Info.Name);
                throw new FfmpegException(string.Format(CultureInfo.InvariantCulture, "Remote FFmpeg exited with code {0} before producing output", transcodingJob.ExitCode));
            }

            _logger.LogWarning("anemone: remote job on {Agent} exited before producing output, falling back to local", plan.Agent.Info.Name);
            RemoveFromActiveJobs(transcodingJob);
            _tokenStore.Revoke(jobId);
            return null;
        }

        _logger.LogDebug("anemone: file {0} created or transcoding has finished (remote)", ffmpegTargetFile);

        if (!transcodingJob.HasExited)
        {
            // anemone: no throttler for remote jobs — TranscodingThrottler dereferences job.Process! to write
            // the pause/resume keystroke, which is always null here. v0 skips throttling remote jobs
            // entirely (RESEARCH.md §6); a v1 fork of TranscodingThrottler would route p/u over
            // IRemoteJob.SendStdinAsync instead.
            _logger.LogDebug("anemone: throttling not applied to remote job {Id}", jobId);

            // TranscodingSegmentCleaner only ever touches job.Path/DownloadPositionTicks/HasExited/Type —
            // never job.Process — so it's safe to reuse unchanged for remote jobs (segments land as local
            // files after the ingest endpoint's PUT+rename, same as a local job's output).
            StartSegmentCleaner(state, transcodingJob);
        }
        else if (transcodingJob.ExitCode != 0)
        {
            throw new FfmpegException(string.Format(CultureInfo.InvariantCulture, "Remote FFmpeg exited with code {0}", transcodingJob.ExitCode));
        }

        _logger.LogDebug("anemone: TryStartRemoteAsync() finished successfully");

        return transcodingJob;
    }

    private void RemoveFromActiveJobs(TranscodingJob job)
    {
        lock (_activeTranscodingJobs)
        {
            _activeTranscodingJobs.Remove(job);
        }
    }

    // anemone: NOTE - deliberately does NOT dispose transcodingJob.CancellationTokenSource (nor call
    // transcodingJob.Dispose(), which would). It's the same CancellationTokenSource the caller passed
    // into StartFfMpeg, and the local fallback path that runs right after this returns null reuses it
    // to build a brand new (local) TranscodingJob - disposing it here would break that fallback.
    private void CleanUpFailedRemoteStart(TranscodingJob transcodingJob, Stream logStream, RoutePlan plan)
    {
        RemoveFromActiveJobs(transcodingJob);
        if (transcodingJob.Id is not null)
        {
            _remoteJobs.TryRemove(transcodingJob.Id, out _);
        }

        _tokenStore.Revoke(plan.Spec.Id);

        try
        {
            logStream.Dispose();
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "anemone: error closing log stream after failed remote start");
        }
    }

    // anemone: remote-job equivalent of OnFfMpegProcessExited — same FinishJob bookkeeping, plus removing
    // the job from _remoteJobs and revoking its ingest token (nothing local owns that cleanup for a
    // remote job, since there's no Process.Exited event to hang it off of).
    //
    // _remoteJobs.TryRemove doubles as a mutex against TryStartRemoteAsync's own "fall back to local"
    // path (see there): if that path already claimed this job's teardown (agent died before producing
    // any output, and the caller is about to retry locally with the same StreamState), TryRemove here
    // fails and this method must NOT touch `state` — FinishJob disposes it, which would corrupt the
    // in-flight local retry.
    private void FinishRemoteJob(TranscodingJob job, StreamState state)
    {
        if (job.Id is null || !_remoteJobs.TryRemove(job.Id, out _))
        {
            _logger.LogDebug("anemone: remote job {Id} exit already handled by the local-fallback path, skipping FinishJob", job.Id);
            return;
        }

        _tokenStore.Revoke(job.Id);
        FinishJob(job, state, job.ExitCode);
    }

    private void StartThrottler(StreamState state, TranscodingJob transcodingJob)
    {
        if (EnableThrottling(state)
            && (_mediaEncoder.IsPkeyPauseSupported
                || _mediaEncoder.EncoderVersion <= _maxFFmpegCkeyPauseSupported))
        {
            transcodingJob.TranscodingThrottler = new TranscodingThrottler(transcodingJob, _loggerFactory.CreateLogger<TranscodingThrottler>(), _serverConfigurationManager, _fileSystem, _mediaEncoder);
            transcodingJob.TranscodingThrottler.Start();
        }
    }

    private static bool EnableThrottling(StreamState state)
        => state.InputProtocol == MediaProtocol.File
           && state.RunTimeTicks.HasValue
           && state.RunTimeTicks.Value >= TimeSpan.FromMinutes(5).Ticks
           && state.IsInputVideo
           && state.VideoType == VideoType.VideoFile;

    private void StartSegmentCleaner(StreamState state, TranscodingJob transcodingJob)
    {
        if (EnableSegmentCleaning(state))
        {
            transcodingJob.TranscodingSegmentCleaner = new TranscodingSegmentCleaner(transcodingJob, _loggerFactory.CreateLogger<TranscodingSegmentCleaner>(), _serverConfigurationManager, _fileSystem, _mediaEncoder, state.SegmentLength);
            transcodingJob.TranscodingSegmentCleaner.Start();
        }
    }

    private static bool EnableSegmentCleaning(StreamState state)
        => state.InputProtocol is MediaProtocol.File or MediaProtocol.Http
           && state.IsInputVideo
           && state.TranscodingType == TranscodingJobType.Hls
           && state.RunTimeTicks.HasValue
           && state.RunTimeTicks.Value >= TimeSpan.FromMinutes(5).Ticks;

    // anemone: process parameter widened to Process? — remote jobs have no local Process at all.
    private TranscodingJob OnTranscodeBeginning(
        string path,
        string? playSessionId,
        string? liveStreamId,
        string transcodingJobId,
        TranscodingJobType type,
        Process? process,
        string? deviceId,
        StreamState state,
        CancellationTokenSource cancellationTokenSource)
    {
        lock (_activeTranscodingJobs)
        {
            var job = new TranscodingJob(_loggerFactory.CreateLogger<TranscodingJob>())
            {
                Type = type,
                Path = path,
                Process = process,
                ActiveRequestCount = 1,
                DeviceId = deviceId,
                CancellationTokenSource = cancellationTokenSource,
                Id = transcodingJobId,
                PlaySessionId = playSessionId,
                LiveStreamId = liveStreamId,
                MediaSource = state.MediaSource
            };

            _activeTranscodingJobs.Add(job);

            ReportTranscodingProgress(job, state, null, null, null, null, null);

            return job;
        }
    }

    /// <inheritdoc />
    public void OnTranscodeEndRequest(TranscodingJob job)
    {
        job.ActiveRequestCount--;
        _logger.LogDebug("OnTranscodeEndRequest job.ActiveRequestCount={ActiveRequestCount}", job.ActiveRequestCount);
        if (job.ActiveRequestCount <= 0)
        {
            PingTimer(job, false);
        }
    }

    private void OnTranscodeFailedToStart(string path, TranscodingJobType type, StreamState state)
    {
        lock (_activeTranscodingJobs)
        {
            var job = _activeTranscodingJobs.FirstOrDefault(j => j.Type == type && string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase));

            if (job is not null)
            {
                _activeTranscodingJobs.Remove(job);
            }
        }

        if (!string.IsNullOrWhiteSpace(state.Request.DeviceId))
        {
            _sessionManager.ClearTranscodingInfo(state.Request.DeviceId);
        }
    }

    // anemone: split into FinishJob (shared by local and remote) + this thin local-only wrapper, so both
    // paths get identical ReportTranscodingProgress/state.Dispose()/job.Dispose() treatment.
    private void OnFfMpegProcessExited(Process process, TranscodingJob job, StreamState state)
    {
        FinishJob(job, state, process.ExitCode);
    }

    // anemone: factored out of upstream's OnFfMpegProcessExited so remote jobs (FinishRemoteJob) get the
    // exact same treatment as local ones — nothing in here touches Process.
    private void FinishJob(TranscodingJob job, StreamState state, int exitCode)
    {
        job.HasExited = true;
        job.ExitCode = exitCode;

        ReportTranscodingProgress(job, state, null, null, null, null, null);

        _logger.LogDebug("Disposing stream resources");
        state.Dispose();

        if (exitCode == 0)
        {
            _logger.LogInformation("FFmpeg exited with code 0");
        }
        else
        {
            _logger.LogError("FFmpeg exited with code {0}", exitCode);
        }

        job.Dispose();
    }

    private async Task AcquireResources(StreamState state, CancellationTokenSource cancellationTokenSource)
    {
        if (state.MediaSource.RequiresOpening && string.IsNullOrWhiteSpace(state.Request.LiveStreamId))
        {
            var liveStreamResponse = await _mediaSourceManager.OpenLiveStream(
                    new LiveStreamRequest { OpenToken = state.MediaSource.OpenToken },
                    cancellationTokenSource.Token)
                .ConfigureAwait(false);
            var encodingOptions = _serverConfigurationManager.GetEncodingOptions();

            _encodingHelper.AttachMediaSourceInfo(state, encodingOptions, liveStreamResponse.MediaSource, state.RequestedUrl);

            if (state.VideoRequest is not null)
            {
                _encodingHelper.TryStreamCopy(state);
            }
        }

        if (state.MediaSource.BufferMs.HasValue)
        {
            await Task.Delay(state.MediaSource.BufferMs.Value, cancellationTokenSource.Token).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public TranscodingJob? OnTranscodeBeginRequest(string path, TranscodingJobType type)
    {
        lock (_activeTranscodingJobs)
        {
            var job = _activeTranscodingJobs
                .FirstOrDefault(j => j.Type == type && string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase));

            if (job is null)
            {
                return null;
            }

            job.ActiveRequestCount++;
            if (string.IsNullOrWhiteSpace(job.PlaySessionId) || job.Type == TranscodingJobType.Progressive)
            {
                job.StopKillTimer();
            }

            return job;
        }
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.PlaySessionId))
        {
            PingTranscodingJob(e.PlaySessionId, e.IsPaused);
        }
    }

    private void DeleteEncodedMediaCache()
    {
        var path = _serverConfigurationManager.GetTranscodePath();
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in _fileSystem.GetFilePaths(path, true))
        {
            try
            {
                _fileSystem.DeleteFile(file);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting encoded media cache file {Path}", path);
            }
        }
    }

    /// <summary>
    /// Transcoding lock.
    /// </summary>
    /// <param name="outputPath">The output path of the transcoded file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An <see cref="IDisposable"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<IDisposable> LockAsync(string outputPath, CancellationToken cancellationToken)
    {
        return _transcodingLocks.LockAsync(outputPath, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStart -= OnPlaybackProgress;
        _transcodingLocks.Dispose();
    }
}
