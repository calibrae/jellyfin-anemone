// anemone: this file is a derivative of Jellyfin's own
// MediaBrowser.Controller.MediaEncoding/TranscodingThrottler.cs @ v10.11.0 (jellyfin-server,
// GPL-2.0-or-later) - kept as close to verbatim as possible, with every deviation marked by a
// "// anemone:" comment so this can be re-based against the next Jellyfin minor. The unmodified upstream
// file this was forked from is kept alongside it at docs/upstream-10.11.0/TranscodingThrottler.cs for that
// rebase, and its presence is why this repository as a whole is licensed GPL-2.0-or-later.
//
// Why a fork was needed: upstream's PauseTranscoding/UnpauseTranscoding write the pause/resume key
// straight to `job.Process!.StandardInput` - unconditionally dereferencing Process, which is always null
// for a remote job (see AnemoneTranscodeManager's own "process parameter widened to Process?" remark).
// This fork replaces that with an injected `Func<string, Task> sendKey` delegate, so the exact same
// policy (5s timer, EnableThrottling/ThrottleDelaySeconds gate, HLS time-based + progressive byte-based
// gap math, unpause-on-stop) serves both a local job (write to the process's own stdin) and a remote one
// (Contracts.IRemoteJob.SendStdinAsync, over the control channel - see PROTOCOL.md "Throttling (v2.2)").
//
// TranscodingJob.TranscodingThrottler is typed to upstream's own (non-forked) TranscodingThrottler class,
// so an instance of this fork cannot be stored there - AnemoneTranscodeManager keeps its own map, keyed by
// job id, instead. See RESEARCH.md and PROTOCOL.md for why/what.
using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Anemone.Transcoding;

/// <summary>
/// anemone: fork of upstream's <c>TranscodingThrottler</c> - see the file-level remarks for why. Pauses or
/// resumes a transcode by writing a single key through <see cref="_sendKey"/> on a 5s timer, based on how
/// far transcoding has raced ahead of what the viewer has actually downloaded.
/// </summary>
public sealed class AnemoneTranscodingThrottler : IDisposable
{
    private readonly TranscodingJob _job;
    private readonly ILogger<AnemoneTranscodingThrottler> _logger;
    private readonly IConfigurationManager _config;
    private readonly IFileSystem _fileSystem;

    // anemone: upstream took an IMediaEncoder here and read _mediaEncoder.IsPkeyPauseSupported directly -
    // that's the SERVER's ffmpeg, which says nothing about whichever machine actually runs this job (see
    // PROTOCOL.md "Throttling (v2.2)"). The caller now resolves that once, per job, and hands the answer
    // in: for a local job it's still _mediaEncoder.IsPkeyPauseSupported (unchanged upstream behaviour, "c"
    // fallback and all), for a remote job it's the AGENT's own hello.ffmpeg.pause_keys, and this class is
    // simply never constructed for a remote job whose agent didn't report that as true (see
    // AnemoneTranscodeManager.StartRemoteThrottler, which logs why at Debug instead).
    private readonly bool _pkeyPauseSupported;

    // anemone: replaces job.Process!.StandardInput.WriteAsync(key). A local caller passes
    // `key => job.Process!.StandardInput.WriteAsync(key)`; a remote caller passes
    // `key => remoteJob.SendStdinAsync(key)`.
    private readonly Func<string, Task> _sendKey;

    private Timer? _timer;
    private bool _isPaused;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnemoneTranscodingThrottler"/> class.
    /// </summary>
    /// <param name="job">Transcoding job dto.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{AnemoneTranscodingThrottler}"/> interface.</param>
    /// <param name="config">Instance of the <see cref="IConfigurationManager"/> interface.</param>
    /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
    /// <param name="pkeyPauseSupported">anemone: whether the ffmpeg that will actually run this job supports <c>p</c>/<c>u</c> - see <see cref="_pkeyPauseSupported"/>.</param>
    /// <param name="sendKey">anemone: writes one key to that ffmpeg's stdin - see <see cref="_sendKey"/>.</param>
    public AnemoneTranscodingThrottler(
        TranscodingJob job,
        ILogger<AnemoneTranscodingThrottler> logger,
        IConfigurationManager config,
        IFileSystem fileSystem,
        bool pkeyPauseSupported,
        Func<string, Task> sendKey)
    {
        _job = job;
        _logger = logger;
        _config = config;
        _fileSystem = fileSystem;
        _pkeyPauseSupported = pkeyPauseSupported;
        _sendKey = sendKey ?? throw new ArgumentNullException(nameof(sendKey));
    }

    /// <summary>
    /// Start timer.
    /// </summary>
    public void Start()
    {
        _timer = new Timer(TimerCallback, null, 5000, 5000);
    }

    /// <summary>
    /// Unpause transcoding.
    /// </summary>
    /// <returns>A <see cref="Task"/>.</returns>
    public async Task UnpauseTranscoding()
    {
        if (_isPaused)
        {
            _logger.LogDebug("Sending resume command to ffmpeg");

            try
            {
                // anemone: was _mediaEncoder.IsPkeyPauseSupported - see _pkeyPauseSupported above.
                var resumeKey = _pkeyPauseSupported ? "u" : Environment.NewLine;
                await _sendKey(resumeKey).ConfigureAwait(false);
                _isPaused = false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resuming transcoding");
            }
        }
    }

    /// <summary>
    /// Stop throttler.
    /// </summary>
    /// <returns>A <see cref="Task"/>.</returns>
    public async Task Stop()
    {
        DisposeTimer();
        await UnpauseTranscoding().ConfigureAwait(false);
    }

    /// <summary>
    /// Dispose throttler.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Dispose throttler.
    /// </summary>
    /// <param name="disposing">Disposing.</param>
    private void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeTimer();
        }
    }

    /// <summary>anemone: whether this throttler currently believes ffmpeg is paused - read by the status API/dashboard.</summary>
    public bool IsPaused => _isPaused;

    private EncodingOptions GetOptions()
    {
        return _config.GetEncodingOptions();
    }

    private async void TimerCallback(object? state)
    {
        await RunThrottleCheckAsync().ConfigureAwait(false);
    }

    // anemone: TimerCallback's body extracted verbatim into its own internal method - purely a test seam
    // (see InternalsVisibleTo in AssemblyInfo.cs, same pattern as AgentHub.PickFrom) so a unit test can
    // drive one throttle decision synchronously instead of waiting on the real 5s Timer, whose due-time and
    // period are kept verbatim in Start() below. Behaviour is unchanged either way - the Timer calls this
    // exact same code every 5s.
    internal async Task RunThrottleCheckAsync()
    {
        if (_job.HasExited)
        {
            DisposeTimer();
            return;
        }

        var options = GetOptions();

        if (options.EnableThrottling && IsThrottleAllowed(_job, Math.Max(options.ThrottleDelaySeconds, 60)))
        {
            await PauseTranscoding().ConfigureAwait(false);
        }
        else
        {
            await UnpauseTranscoding().ConfigureAwait(false);
        }
    }

    private async Task PauseTranscoding()
    {
        if (!_isPaused)
        {
            // anemone: was _mediaEncoder.IsPkeyPauseSupported ? "p" : "c". A remote job must never fall back
            // to "c" - on a jellyfin-ffmpeg build without the pause patch that key opens ffmpeg's
            // filtergraph-command prompt instead of pausing, which would mislead rather than throttle (see
            // PROTOCOL.md "Throttling (v2.2)"). This class is only ever constructed with
            // _pkeyPauseSupported=false for a LOCAL job whose own ffmpeg lacks the patch - upstream's real
            // (if older) "c" fallback - never for a remote one; a remote job without pause-key support never
            // gets a throttler at all (see AnemoneTranscodeManager.StartRemoteThrottler).
            var pauseKey = _pkeyPauseSupported ? "p" : "c";

            _logger.LogDebug("Sending pause command [{Key}] to ffmpeg", pauseKey);

            try
            {
                await _sendKey(pauseKey).ConfigureAwait(false);
                _isPaused = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error pausing transcoding");
            }
        }
    }

    private bool IsThrottleAllowed(TranscodingJob job, int thresholdSeconds)
    {
        var bytesDownloaded = job.BytesDownloaded;
        var transcodingPositionTicks = job.TranscodingPositionTicks ?? 0;
        var downloadPositionTicks = job.DownloadPositionTicks ?? 0;

        var path = job.Path ?? throw new ArgumentException("Path can't be null.");

        var gapLengthInTicks = TimeSpan.FromSeconds(thresholdSeconds).Ticks;

        if (downloadPositionTicks > 0 && transcodingPositionTicks > 0)
        {
            // HLS - time-based consideration

            var targetGap = gapLengthInTicks;
            var gap = transcodingPositionTicks - downloadPositionTicks;

            if (gap < targetGap)
            {
                _logger.LogDebug("Not throttling transcoder gap {0} target gap {1}", gap, targetGap);
                return false;
            }

            _logger.LogDebug("Throttling transcoder gap {0} target gap {1}", gap, targetGap);
            return true;
        }

        if (bytesDownloaded > 0 && transcodingPositionTicks > 0)
        {
            // Progressive Streaming - byte-based consideration

            try
            {
                var bytesTranscoded = job.BytesTranscoded ?? _fileSystem.GetFileInfo(path).Length;

                // Estimate the bytes the transcoder should be ahead
                double gapFactor = gapLengthInTicks;
                gapFactor /= transcodingPositionTicks;
                var targetGap = bytesTranscoded * gapFactor;

                var gap = bytesTranscoded - bytesDownloaded;

                if (gap < targetGap)
                {
                    _logger.LogDebug("Not throttling transcoder gap {0} target gap {1} bytes downloaded {2}", gap, targetGap, bytesDownloaded);
                    return false;
                }

                _logger.LogDebug("Throttling transcoder gap {0} target gap {1} bytes downloaded {2}", gap, targetGap, bytesDownloaded);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting output size");
                return false;
            }
        }

        _logger.LogDebug("No throttle data for {Path}", path);
        return false;
    }

    private void DisposeTimer()
    {
        if (_timer is not null)
        {
            _timer.Dispose();
            _timer = null;
        }
    }
}
