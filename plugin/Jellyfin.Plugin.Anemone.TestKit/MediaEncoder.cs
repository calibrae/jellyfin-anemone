using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.Anemone.TestKit;

/// <summary>
/// <see cref="IMediaEncoder"/> fake. Every property is settable so a test can shape "the server's own
/// ffmpeg" (version, pkey-pause support, ...); members <see cref="AnemoneTranscodeManager"/>/
/// <see cref="JobRouter"/>/<see cref="EncodingHelper"/> never call throw, so a missing behaviour is loud
/// rather than silently wrong.
/// </summary>
public sealed class FakeMediaEncoder : IMediaEncoder
{
    /// <summary>
    /// Defaults to a <see cref="FakeFfmpegScript"/> that just creates its output file and waits for a
    /// quit key on stdin - a realistic-enough stand-in so the LOCAL transcode path in
    /// <c>AnemoneTranscodeManager.StartFfMpeg</c> can actually run end to end in a unit test. Point it at
    /// a differently-scripted <see cref="FakeFfmpegScript"/> (or any real executable) to change that.
    /// </summary>
    public string EncoderPath { get; set; } = string.Empty;

    public string ProbePath { get; set; } = string.Empty;

    public Version EncoderVersion { get; set; } = new(7, 1);

    public bool IsPkeyPauseSupported { get; set; } = true;

    public bool IsVaapiDeviceAmd { get; set; }

    public bool IsVaapiDeviceInteliHD { get; set; }

    public bool IsVaapiDeviceInteli965 { get; set; }

    public bool IsVaapiDeviceSupportVulkanDrmModifier { get; set; }

    public bool IsVaapiDeviceSupportVulkanDrmInterop { get; set; }

    public bool IsVideoToolboxAv1DecodeAvailable { get; set; }

    public bool SupportsEncoder(string encoder) => true;

    public bool SupportsDecoder(string decoder) => true;

    public bool SupportsHwaccel(string hwaccel) => true;

    public bool SupportsFilter(string filter) => true;

    public bool SupportsFilterWithOption(FilterOptionType option) => true;

    public bool SupportsBitStreamFilterWithOption(BitStreamFilterOptionType option) => true;

    public bool CanEncodeToAudioCodec(string codec) => true;

    public bool CanEncodeToSubtitleCodec(string codec) => true;

    public bool CanExtractSubtitles(string codec) => true;

    public Task<string> ExtractAudioImage(string path, int? imageStreamIndex, CancellationToken cancellationToken) =>
        throw new NotSupportedException("anemone-testkit: FakeMediaEncoder does not implement image extraction");

    public Task<string> ExtractVideoImage(string inputFile, string container, MediaSourceInfo mediaSource, MediaStream videoStream, Video3DFormat? threedFormat, TimeSpan? offset, CancellationToken cancellationToken) =>
        throw new NotSupportedException("anemone-testkit: FakeMediaEncoder does not implement image extraction");

    public Task<string> ExtractVideoImage(string inputFile, string container, MediaSourceInfo mediaSource, MediaStream imageStream, int? imageStreamIndex, MediaBrowser.Model.Drawing.ImageFormat? targetFormat, CancellationToken cancellationToken) =>
        throw new NotSupportedException("anemone-testkit: FakeMediaEncoder does not implement image extraction");

    public Task<string> ExtractVideoImagesOnIntervalAccelerated(string inputFile, string container, MediaSourceInfo mediaSource, MediaStream imageStream, int maxWidth, TimeSpan interval, bool allowHwAccel, bool enableHwEncoding, int? threads, int? qualityScale, System.Diagnostics.ProcessPriorityClass? priority, bool enableKeyFrameOnlyExtraction, EncodingHelper encodingHelper, CancellationToken cancellationToken) =>
        throw new NotSupportedException("anemone-testkit: FakeMediaEncoder does not implement image extraction");

    public Task<MediaBrowser.Model.MediaInfo.MediaInfo> GetMediaInfo(MediaInfoRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("anemone-testkit: FakeMediaEncoder does not implement probing");

    public string GetInputArgument(string inputFile, MediaSourceInfo mediaSource) => $"file:{inputFile}";

    public string GetInputArgument(IReadOnlyList<string> inputFiles, MediaSourceInfo mediaSource) => $"file:{inputFiles[0]}";

    public string GetExternalSubtitleInputArgument(string inputFile) => inputFile;

    public string GetTimeParameter(long ticks) => TimeSpan.FromTicks(ticks).ToString();

    public Task ConvertImage(string inputPath, string outputPath) => Task.CompletedTask;

    public string EscapeSubtitleFilterPath(string path) => path;

    public bool SetFFmpegPath() => true;

    public IReadOnlyList<string> GetPrimaryPlaylistVobFiles(string path, uint? titleNumber) => [];

    public IReadOnlyList<string> GetPrimaryPlaylistM2tsFiles(string path) => [];

    public string GetInputPathArgument(EncodingJobInfo state) => $"file:{state.MediaPath}";

    public string GetInputPathArgument(string path, MediaSourceInfo mediaSource) => $"file:{path}";

    public void GenerateConcatConfig(MediaSourceInfo source, string concatFilePath)
    {
    }
}
