using System.Net;
using Jellyfin.Plugin.Anemone.Contracts;
using MediaBrowser.Common;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Anemone.Tests.Transcoding;

/// <summary>
/// Hand-written fakes for the interfaces JobRouter/StreamState depend on. No mocking library, per
/// project convention. Members that TryPlan never touches just throw - they exist so these classes
/// compile against the real Jellyfin.Controller interfaces.
/// </summary>
internal sealed class FakeAgentConnection : IAgentConnection
{
    public FakeAgentConnection(AgentInfo info)
    {
        Info = info;
    }

    public AgentInfo Info { get; }

    public int ActiveJobs { get; init; }

    public bool IsConnected { get; init; } = true;

    public DateTimeOffset LastSeen { get; init; } = DateTimeOffset.UtcNow;

    public Task<IRemoteJob> StartJobAsync(RemoteJobSpec spec, IRemoteJobSink sink, CancellationToken cancellationToken)
        => throw new NotImplementedException();
}

internal sealed class FakeAgentRegistry : IAgentRegistry
{
    public IAgentConnection? AgentToReturn { get; set; }

    public JobRequirements? LastRequirements { get; private set; }

    public IReadOnlyList<IAgentConnection> Agents => AgentToReturn is null ? [] : [AgentToReturn];

    public IAgentConnection? Pick(JobRequirements requirements)
    {
        LastRequirements = requirements;
        return AgentToReturn;
    }
}

internal sealed class FakeIngestTokenStore : IIngestTokenStore
{
    public List<(string JobId, string TargetDirectory, string FilePrefix)> Issued { get; } = new();

    public List<string> Revoked { get; } = new();

    public string TokenToReturn { get; set; } = "test-token";

    public string Issue(string jobId, string targetDirectory, string filePrefix)
    {
        Issued.Add((jobId, targetDirectory, filePrefix));
        return TokenToReturn;
    }

    public bool TryValidate(string jobId, string bearerToken, out IngestGrant grant)
    {
        grant = new IngestGrant(jobId, "/tmp", "prefix");
        return true;
    }

    public void Revoke(string jobId) => Revoked.Add(jobId);
}

internal sealed class FakeMediaEncoder : IMediaEncoder
{
    public string EncoderPath { get; set; } = "/opt/jellyfin-ffmpeg/ffmpeg";

    public string ProbePath => "/opt/jellyfin-ffmpeg/ffprobe";

    public Version EncoderVersion { get; set; } = new(7, 1);

    public bool IsPkeyPauseSupported => true;

    public bool IsVaapiDeviceAmd => false;

    public bool IsVaapiDeviceInteliHD => false;

    public bool IsVaapiDeviceInteli965 => false;

    public bool IsVaapiDeviceSupportVulkanDrmModifier => false;

    public bool IsVaapiDeviceSupportVulkanDrmInterop => false;

    public bool IsVideoToolboxAv1DecodeAvailable => false;

    public bool SupportsEncoder(string encoder) => throw new NotImplementedException();

    public bool SupportsDecoder(string decoder) => throw new NotImplementedException();

    public bool SupportsHwaccel(string hwaccel) => throw new NotImplementedException();

    public bool SupportsFilter(string filter) => throw new NotImplementedException();

    public bool SupportsFilterWithOption(FilterOptionType option) => throw new NotImplementedException();

    public bool SupportsBitStreamFilterWithOption(BitStreamFilterOptionType option) => throw new NotImplementedException();

    public bool CanEncodeToAudioCodec(string codec) => throw new NotImplementedException();

    public bool CanEncodeToSubtitleCodec(string codec) => throw new NotImplementedException();

    public bool CanExtractSubtitles(string codec) => throw new NotImplementedException();

    public Task<string> ExtractAudioImage(string path, int? imageStreamIndex, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<string> ExtractVideoImage(string inputFile, string container, MediaSourceInfo mediaSource, MediaStream videoStream, Video3DFormat? threedFormat, TimeSpan? offset, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<string> ExtractVideoImage(string inputFile, string container, MediaSourceInfo mediaSource, MediaStream imageStream, int? imageStreamIndex, MediaBrowser.Model.Drawing.ImageFormat? targetFormat, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<string> ExtractVideoImagesOnIntervalAccelerated(string inputFile, string container, MediaSourceInfo mediaSource, MediaStream imageStream, int maxWidth, TimeSpan interval, bool allowHwAccel, bool enableHwEncoding, int? threads, int? qualityScale, System.Diagnostics.ProcessPriorityClass? priority, bool enableKeyFrameOnlyExtraction, EncodingHelper encodingHelper, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<MediaBrowser.Model.MediaInfo.MediaInfo> GetMediaInfo(MediaInfoRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();

    public string GetInputArgument(string inputFile, MediaSourceInfo mediaSource) => throw new NotImplementedException();

    public string GetInputArgument(IReadOnlyList<string> inputFiles, MediaSourceInfo mediaSource) => throw new NotImplementedException();

    public string GetExternalSubtitleInputArgument(string inputFile) => throw new NotImplementedException();

    public string GetTimeParameter(long ticks) => throw new NotImplementedException();

    public Task ConvertImage(string inputPath, string outputPath) => throw new NotImplementedException();

    public string EscapeSubtitleFilterPath(string path) => throw new NotImplementedException();

    public bool SetFFmpegPath() => throw new NotImplementedException();

    public IReadOnlyList<string> GetPrimaryPlaylistVobFiles(string path, uint? titleNumber) => throw new NotImplementedException();

    public IReadOnlyList<string> GetPrimaryPlaylistM2tsFiles(string path) => throw new NotImplementedException();

    public string GetInputPathArgument(EncodingJobInfo state) => throw new NotImplementedException();

    public string GetInputPathArgument(string path, MediaSourceInfo mediaSource) => throw new NotImplementedException();

    public void GenerateConcatConfig(MediaSourceInfo source, string concatFilePath) => throw new NotImplementedException();
}

internal sealed class FakeServerApplicationHost : IServerApplicationHost
{
    public string? UrlToReturn { get; set; } = "http://10.240.0.1:8096";

    public (IPAddress? IpAddress, bool AllowHttps)? LastGetApiUrlForLocalAccessCall { get; private set; }

    public bool CoreStartupHasCompleted => true;

    public int HttpPort => 8096;

    public int HttpsPort => 8920;

    public bool ListenWithHttps => false;

    public string FriendlyName => "test-server";

    public string? RestoreBackupPath { get; set; }

    public string Name => "Jellyfin Server";

    public string SystemId => "test-system";

    public bool HasPendingRestart => false;

    public bool ShouldRestart { get; set; }

    public Version ApplicationVersion => new(10, 11, 0);

    public IServiceProvider? ServiceProvider { get; set; }

    public string ApplicationVersionString => "10.11.0";

    public string ApplicationUserAgent => "Jellyfin/10.11.0";

    public string ApplicationUserAgentAddress => "https://jellyfin.org";

#pragma warning disable CS0067 // required by IApplicationHost, never raised by this fake
    public event EventHandler? HasPendingRestartChanged;
#pragma warning restore CS0067

    public string GetSmartApiUrl(HttpRequest request) => throw new NotImplementedException();

    public string GetSmartApiUrl(IPAddress remoteAddr) => throw new NotImplementedException();

    public string GetSmartApiUrl(string hostname) => throw new NotImplementedException();

    public string GetApiUrlForLocalAccess(IPAddress? ipAddress = null, bool allowHttps = true)
    {
        LastGetApiUrlForLocalAccessCall = (ipAddress, allowHttps);
        return UrlToReturn ?? throw new InvalidOperationException("UrlToReturn not set");
    }

    public string GetLocalApiUrl(string hostname, string? scheme = null, int? port = null) => throw new NotImplementedException();

    public string ExpandVirtualPath(string path) => throw new NotImplementedException();

    public string ReverseVirtualPath(string path) => throw new NotImplementedException();

    public IEnumerable<System.Reflection.Assembly> GetApiPluginAssemblies() => throw new NotImplementedException();

    public void NotifyPendingRestart() => throw new NotImplementedException();

    public IReadOnlyCollection<T> GetExports<T>(bool manageLifetime = true) => throw new NotImplementedException();

    public IReadOnlyCollection<T> GetExports<T>(CreationDelegateFactory defaultFunc, bool manageLifetime = true) => throw new NotImplementedException();

    public IEnumerable<Type> GetExportTypes<T>() => throw new NotImplementedException();

    public T Resolve<T>() => throw new NotImplementedException();

    public void Init(IServiceCollection serviceCollection) => throw new NotImplementedException();
}

/// <summary>Fake for StreamState's ITranscodeManager dependency - StreamState only stores the reference.</summary>
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

/// <summary>Fake for StreamState's IMediaSourceManager dependency - unused by anything JobRouter.TryPlan touches.</summary>
internal sealed class FakeMediaSourceManager : IMediaSourceManager
{
    public void AddParts(IEnumerable<IMediaSourceProvider> providers) => throw new NotImplementedException();

    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId) => throw new NotImplementedException();

    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query) => throw new NotImplementedException();

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId) => throw new NotImplementedException();

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query) => throw new NotImplementedException();

    public Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(BaseItem item, Jellyfin.Database.Implementations.Entities.User? user, bool allowMediaProbe, bool enablePathSubstitution, CancellationToken cancellationToken) => throw new NotImplementedException();

    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, Jellyfin.Database.Implementations.Entities.User? user = null) => throw new NotImplementedException();

    public Task<MediaSourceInfo> GetMediaSource(BaseItem item, string mediaSourceId, string? liveStreamId, bool enablePathSubstitution, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(LiveStreamRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken) => throw new NotImplementedException();

    public ILiveStream GetLiveStreamInfo(string id) => throw new NotImplementedException();

    public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId) => throw new NotImplementedException();

    public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(ActiveRecordingInfo info, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task CloseLiveStream(string id) => throw new NotImplementedException();

    public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken) => throw new NotImplementedException();

    public bool SupportsDirectStream(string path, MediaProtocol protocol) => throw new NotImplementedException();

    public MediaProtocol GetPathProtocol(string path) => throw new NotImplementedException();

    public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, Jellyfin.Database.Implementations.Entities.User user) => throw new NotImplementedException();

    public Task AddMediaInfoWithProbe(MediaSourceInfo mediaSource, bool isAudio, string cacheKey, bool addProbeDelay, bool isLiveStream, CancellationToken cancellationToken) => throw new NotImplementedException();
}
