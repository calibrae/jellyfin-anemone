using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.Anemone.TestKit;

/// <summary>
/// Builds a <see cref="MediaSourceInfo"/> (a plain, fully-settable Jellyfin model POCO - no faking
/// needed). Defaults describe a local file input that needs no live-stream opening and no closing, which
/// is what almost every <see cref="AnemoneTranscodeManager"/> test wants; override what a specific
/// scenario needs.
/// </summary>
public sealed class MediaSourceInfoBuilder
{
    private string _id = "media-source-1";
    private string _path = "/Volumes/data/_tvshows/Show Name/Season 01/Show Name - S01E01 - Pilot.mkv";
    private MediaProtocol _protocol = MediaProtocol.File;
    private bool _requiresOpening;
    private bool _requiresClosing;
    private string? _liveStreamId;
    private string? _openToken;
    private long? _runTimeTicks = TimeSpan.FromMinutes(42).Ticks;
    private int? _bufferMs;

    public MediaSourceInfoBuilder WithId(string id)
    {
        _id = id;
        return this;
    }

    public MediaSourceInfoBuilder WithPath(string path)
    {
        _path = path;
        return this;
    }

    public MediaSourceInfoBuilder WithProtocol(MediaProtocol protocol)
    {
        _protocol = protocol;
        return this;
    }

    public MediaSourceInfoBuilder WithRunTime(TimeSpan? runTime)
    {
        _runTimeTicks = runTime?.Ticks;
        return this;
    }

    public MediaSourceInfoBuilder WithBufferMs(int? bufferMs)
    {
        _bufferMs = bufferMs;
        return this;
    }

    /// <summary>Models a live stream needing <c>OpenLiveStream</c> before the transcode starts.</summary>
    public MediaSourceInfoBuilder WithRequiresOpening(bool requiresOpening = true)
    {
        _requiresOpening = requiresOpening;
        return this;
    }

    /// <summary>Models a live stream needing <c>CloseLiveStream</c> on <see cref="StreamState.Dispose"/>.</summary>
    public MediaSourceInfoBuilder WithRequiresClosing(bool requiresClosing = true, string? liveStreamId = "live-1")
    {
        _requiresClosing = requiresClosing;
        _liveStreamId = liveStreamId;
        return this;
    }

    public MediaSourceInfoBuilder WithOpenToken(string? openToken)
    {
        _openToken = openToken;
        return this;
    }

    public MediaSourceInfo Build() => new()
    {
        Id = _id,
        Path = _path,
        Protocol = _protocol,
        RequiresOpening = _requiresOpening,
        RequiresClosing = _requiresClosing,
        LiveStreamId = _liveStreamId,
        OpenToken = _openToken,
        RunTimeTicks = _runTimeTicks,
        BufferMs = _bufferMs,
        SupportsDirectStream = true,
        SupportsProbing = true,
    };
}
