using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Configuration;
using IApplicationPaths = MediaBrowser.Common.Configuration.IApplicationPaths;
using IConfigurationManager = MediaBrowser.Common.Configuration.IConfigurationManager;

namespace Jellyfin.Plugin.Anemone.TestKit;

/// <summary>
/// Builds a real <see cref="EncodingHelper"/> - it's a concrete class from
/// <c>MediaBrowser.Controller.MediaEncoding</c>, not an interface <see cref="AnemoneTranscodeManager"/>'s
/// constructor can be handed a fake for, so a usable real instance has to be constructed instead. Its own
/// dependencies (<see cref="ISubtitleEncoder"/>, <see cref="IPathManager"/>) are faked here with
/// throwing stubs: <c>AnemoneTranscodeManager.AcquireResources</c> and its subtitle-attachment-extraction
/// branch only reach into <see cref="EncodingHelper"/> when <c>state.MediaSource.RequiresOpening</c> or a
/// subtitle stream is set, both of which <see cref="MediaSourceInfoBuilder"/>/<see cref="StreamStateBuilder"/>
/// leave off by default - so the throwing stubs are never actually invoked by the AnemoneTranscodeManager
/// test surface. A test that specifically exercises subtitle burn-in or live-stream opening should supply
/// its own <paramref name="subtitleEncoder"/>/real behaviour instead of relying on the defaults here.
/// </summary>
public static class EncodingHelperFactory
{
    public static EncodingHelper Create(
        IApplicationPaths appPaths,
        IMediaEncoder mediaEncoder,
        IConfigurationManager configurationManager,
        ISubtitleEncoder? subtitleEncoder = null,
        IPathManager? pathManager = null)
    {
        return new EncodingHelper(
            appPaths,
            mediaEncoder,
            subtitleEncoder ?? new FakeSubtitleEncoder(),
            new ConfigurationBuilder().Build(),
            configurationManager,
            pathManager ?? new FakePathManager());
    }
}

/// <summary>Throwing <see cref="ISubtitleEncoder"/> stub - see <see cref="EncodingHelperFactory"/>.</summary>
public sealed class FakeSubtitleEncoder : ISubtitleEncoder
{
    public Task ExtractAllExtractableSubtitles(MediaSourceInfo mediaSource, CancellationToken cancellationToken) =>
        throw new NotSupportedException("anemone-testkit: FakeSubtitleEncoder is a stub for EncodingHelper construction only");

    public Task<string> GetSubtitleFileCharacterSet(MediaStream subtitleStream, string language, MediaSourceInfo mediaSource, CancellationToken cancellationToken) =>
        throw new NotSupportedException("anemone-testkit: FakeSubtitleEncoder is a stub for EncodingHelper construction only");

    public Task<string> GetSubtitleFilePath(MediaStream subtitleStream, MediaSourceInfo mediaSource, CancellationToken cancellationToken) =>
        throw new NotSupportedException("anemone-testkit: FakeSubtitleEncoder is a stub for EncodingHelper construction only");

    public Task<Stream> GetSubtitles(BaseItem item, string mediaSourceId, int subtitleStreamIndex, string outputFormat, long startTimeTicks, long endTimeTicks, bool preserveOriginalTimestamps, CancellationToken cancellationToken) =>
        throw new NotSupportedException("anemone-testkit: FakeSubtitleEncoder is a stub for EncodingHelper construction only");
}

/// <summary>Throwing <see cref="IPathManager"/> stub - see <see cref="EncodingHelperFactory"/>.</summary>
public sealed class FakePathManager : IPathManager
{
    public string GetAttachmentFolderPath(string mediaPath) =>
        throw new NotSupportedException("anemone-testkit: FakePathManager is a stub for EncodingHelper construction only");

    public string GetAttachmentPath(string mediaPath, string fileName) =>
        throw new NotSupportedException("anemone-testkit: FakePathManager is a stub for EncodingHelper construction only");

    public string GetChapterImageFolderPath(BaseItem item) =>
        throw new NotSupportedException("anemone-testkit: FakePathManager is a stub for EncodingHelper construction only");

    public string GetChapterImagePath(BaseItem item, long chapterPositionTicks) =>
        throw new NotSupportedException("anemone-testkit: FakePathManager is a stub for EncodingHelper construction only");

    public IReadOnlyList<string> GetExtractedDataPaths(BaseItem item) => [];

    public string GetSubtitleFolderPath(string mediaPath) =>
        throw new NotSupportedException("anemone-testkit: FakePathManager is a stub for EncodingHelper construction only");

    public string GetSubtitlePath(string mediaPath, int streamIndex, string extension) =>
        throw new NotSupportedException("anemone-testkit: FakePathManager is a stub for EncodingHelper construction only");

    public string GetTrickplayDirectory(BaseItem item, bool saveWithMedia = false) =>
        throw new NotSupportedException("anemone-testkit: FakePathManager is a stub for EncodingHelper construction only");
}
