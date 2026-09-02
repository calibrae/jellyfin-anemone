using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.Anemone.TestKit;

/// <summary>
/// Builds a real, usable <see cref="StreamState"/> - the awkward one. <see cref="StreamState"/> derives
/// from the sealed-shape (but not literally sealed) <see cref="EncodingJobInfo"/>, and every property this
/// builder touches has a real public setter on the real Jellyfin types, so no reflection or shim subclass
/// is needed. A handful of properties are read-only/computed and CANNOT be set directly - see the remarks
/// below; drive them indirectly through <see cref="MediaSourceInfoBuilder"/>/<see cref="Request"/> instead.
/// </summary>
/// <remarks>
/// Properties this builder found to be unsettable (computed from other state, per <see cref="EncodingJobInfo"/>/
/// <see cref="StreamState"/>'s own source):
/// <list type="bullet">
/// <item><c>VideoRequest</c> - computed as <c>Request as VideoRequestDto</c>; build with a
/// <see cref="VideoRequestDto"/> (this builder's default) rather than the base <see cref="StreamingRequestDto"/>
/// to get a non-null value.</item>
/// <item><c>IsOutputVideo</c> - computed as <c>Request is VideoRequestDto</c>, same story.</item>
/// <item><c>ActualOutputAudioCodec</c>/<c>ActualOutputVideoCodec</c>/<c>IsSegmentedLiveStream</c>/
/// <c>SegmentLength</c>/<c>MinSegments</c> and the <c>TargetVideo*</c>/<c>TargetAudio*</c> family - all
/// derived by <see cref="EncodingJobInfo"/>/<see cref="StreamState"/> from streams/request/output-codec
/// fields that ARE settable; set those instead of trying to set the derived value.</item>
/// </list>
/// </remarks>
public sealed class StreamStateBuilder
{
    private IMediaSourceManager _mediaSourceManager = new FakeMediaSourceManager();
    private ITranscodeManager? _transcodeManager;
    private TranscodingJobType _jobType = TranscodingJobType.Hls;
    private VideoRequestDto _request = new()
    {
        DeviceId = "device-1",
        PlaySessionId = "play-session-1",
        MediaSourceId = "media-source-1",
    };
    private MediaSourceInfo _mediaSource = new MediaSourceInfoBuilder().Build();
    private bool _isInputVideo = true;
    private VideoType _videoType = VideoType.VideoFile;
    private MediaProtocol _inputProtocol = MediaProtocol.File;
    private string? _waitForPath;
    private string _outputVideoCodec = "h264_videotoolbox";
    private string _outputAudioCodec = "aac_at";
    private string _outputContainer = "hls";
    private long? _runTimeTicks = TimeSpan.FromMinutes(42).Ticks;
    private MediaStream? _subtitleStream;
    private SubtitleDeliveryMethod _subtitleDeliveryMethod = SubtitleDeliveryMethod.Drop;

    /// <summary>
    /// Required: <see cref="StreamState.ReportTranscodingProgress"/> calls straight back into this -
    /// pass the <see cref="AnemoneTranscodeManager"/> instance under test so progress reported through the
    /// real <c>JobLogger</c> parsing path round-trips into it, the same way it does in production.
    /// </summary>
    public StreamStateBuilder WithTranscodeManager(ITranscodeManager transcodeManager)
    {
        _transcodeManager = transcodeManager;
        return this;
    }

    public StreamStateBuilder WithMediaSourceManager(IMediaSourceManager mediaSourceManager)
    {
        _mediaSourceManager = mediaSourceManager;
        return this;
    }

    public StreamStateBuilder WithJobType(TranscodingJobType jobType)
    {
        _jobType = jobType;
        return this;
    }

    /// <summary>Replaces the whole request DTO (device id / play session id / media source id / codecs / ...).</summary>
    public StreamStateBuilder WithRequest(VideoRequestDto request)
    {
        _request = request;
        return this;
    }

    public StreamStateBuilder WithDeviceId(string? deviceId)
    {
        _request.DeviceId = deviceId!;
        return this;
    }

    public StreamStateBuilder WithPlaySessionId(string? playSessionId)
    {
        _request.PlaySessionId = playSessionId!;
        return this;
    }

    public StreamStateBuilder WithMediaSource(MediaSourceInfo mediaSource)
    {
        _mediaSource = mediaSource;
        return this;
    }

    public StreamStateBuilder WithIsInputVideo(bool isInputVideo)
    {
        _isInputVideo = isInputVideo;
        return this;
    }

    public StreamStateBuilder WithVideoType(VideoType videoType)
    {
        _videoType = videoType;
        return this;
    }

    public StreamStateBuilder WithInputProtocol(MediaProtocol protocol)
    {
        _inputProtocol = protocol;
        return this;
    }

    /// <summary>The path <c>StartFfMpeg</c>/<c>TryStartRemoteAsync</c> poll for existence (<c>state.WaitForPath ?? outputPath</c>). Leave null to poll <c>outputPath</c> itself.</summary>
    public StreamStateBuilder WithWaitForPath(string? waitForPath)
    {
        _waitForPath = waitForPath;
        return this;
    }

    public StreamStateBuilder WithOutputVideoCodec(string codec)
    {
        _outputVideoCodec = codec;
        return this;
    }

    public StreamStateBuilder WithOutputAudioCodec(string codec)
    {
        _outputAudioCodec = codec;
        return this;
    }

    /// <summary>
    /// <see cref="EncodingJobInfo.RunTimeTicks"/> itself (distinct from <c>MediaSource.RunTimeTicks</c>) -
    /// what <c>EnableThrottling</c>/<c>EnableSegmentCleaning</c> actually check. Defaults to 42 minutes so
    /// both gates (&gt;= 5 min) are satisfied unless overridden.
    /// </summary>
    public StreamStateBuilder WithRunTimeTicks(long? ticks)
    {
        _runTimeTicks = ticks;
        return this;
    }

    /// <summary>Set together with a delivery method of <c>Encode</c> to exercise the subtitle-burn-in attachment-extraction branch.</summary>
    public StreamStateBuilder WithSubtitleStream(MediaStream? subtitleStream, SubtitleDeliveryMethod method = SubtitleDeliveryMethod.Encode)
    {
        _subtitleStream = subtitleStream;
        _subtitleDeliveryMethod = method;
        return this;
    }

    public StreamState Build()
    {
        var transcodeManager = _transcodeManager
            ?? throw new InvalidOperationException("anemone-testkit: StreamStateBuilder.WithTranscodeManager(...) is required - StreamState.ReportTranscodingProgress calls back into it.");

        var state = new StreamState(_mediaSourceManager, _jobType, transcodeManager)
        {
            Request = _request,
            MediaSource = _mediaSource,
            IsInputVideo = _isInputVideo,
            VideoType = _videoType,
            InputProtocol = _inputProtocol,
            WaitForPath = _waitForPath,
            OutputVideoCodec = _outputVideoCodec,
            OutputAudioCodec = _outputAudioCodec,
            OutputContainer = _outputContainer,
            RunTimeTicks = _runTimeTicks,
            SubtitleStream = _subtitleStream,
            SubtitleDeliveryMethod = _subtitleDeliveryMethod,
        };

        return state;
    }
}
