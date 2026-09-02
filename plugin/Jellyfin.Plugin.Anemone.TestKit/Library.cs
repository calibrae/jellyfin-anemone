using Jellyfin.Data.Events;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Users;

namespace Jellyfin.Plugin.Anemone.TestKit;

/// <summary>
/// <see cref="IUserManager"/> fake. <see cref="AnemoneTranscodeManager.StartFfMpeg"/> only calls
/// <see cref="GetUserById"/>, and only when the caller passes a non-empty <c>userId</c> - pass
/// <see cref="Guid.Empty"/> from a test to skip user lookup/permission-check entirely (the production
/// code does the same for anonymous/system-initiated transcodes). <see cref="UsersById"/> lets a test
/// script a specific user for the permission-check path.
/// </summary>
public sealed class FakeUserManager : IUserManager
{
#pragma warning disable CS0067 // required by IUserManager, never raised by this fake
    public event EventHandler<GenericEventArgs<User>>? OnUserUpdated;
#pragma warning restore CS0067

    public Dictionary<Guid, User> UsersById { get; } = [];

    public IEnumerable<User> Users => UsersById.Values;

    public IEnumerable<Guid> UsersIds => UsersById.Keys;

    public User? GetUserById(Guid id) => UsersById.GetValueOrDefault(id);

    public User? GetUserByName(string name) => Users.FirstOrDefault(u => string.Equals(u.Username, name, StringComparison.OrdinalIgnoreCase));

    public Task<User> AuthenticateUser(string username, string password, string remoteEndPoint, bool isUserSession) =>
        throw new NotSupportedException("anemone-testkit: FakeUserManager does not implement authentication");

    public Task ChangePassword(User user, string newPassword) => Task.CompletedTask;

    public Task ClearProfileImageAsync(User user) => Task.CompletedTask;

    public Task<User> CreateUserAsync(string name) => throw new NotSupportedException("anemone-testkit: FakeUserManager does not implement user creation");

    public Task DeleteUserAsync(Guid userId) => Task.CompletedTask;

    public NameIdPair[] GetAuthenticationProviders() => [];

    public NameIdPair[] GetPasswordResetProviders() => [];

    public UserDto GetUserDto(User user, string? remoteEndPoint = null) => throw new NotSupportedException("anemone-testkit: FakeUserManager does not implement DTO projection");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task<PinRedeemResult> RedeemPasswordResetPin(string pin) => throw new NotSupportedException("anemone-testkit: FakeUserManager does not implement password reset");

    public Task RenameUser(User user, string newName) => Task.CompletedTask;

    public Task ResetPassword(User user) => Task.CompletedTask;

    public Task<ForgotPasswordResult> StartForgotPasswordProcess(string enteredUsername, bool isInNetwork) =>
        throw new NotSupportedException("anemone-testkit: FakeUserManager does not implement password reset");

    public Task UpdateConfigurationAsync(Guid userId, UserConfiguration config) => Task.CompletedTask;

    public Task UpdatePolicyAsync(Guid userId, UserPolicy policy) => Task.CompletedTask;

    public Task UpdateUserAsync(User user) => Task.CompletedTask;
}

/// <summary>
/// <see cref="IMediaSourceManager"/> fake. <see cref="StreamState"/> only stores this reference (its own
/// <c>Dispose</c> calls <see cref="CloseLiveStream"/> when <c>MediaSource.RequiresClosing</c> is set, which
/// <see cref="MediaSourceInfoBuilder"/> defaults to false) - so nothing here needs real behaviour for the
/// AnemoneTranscodeManager test surface, but calls are recorded in case a test wants to assert on them.
/// </summary>
public sealed class FakeMediaSourceManager : IMediaSourceManager
{
    public List<string> ClosedLiveStreamIds { get; } = [];

    public LiveStreamResponse? OpenLiveStreamResponseToReturn { get; set; }

    public void AddParts(IEnumerable<IMediaSourceProvider> providers)
    {
    }

    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId) => [];

    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query) => [];

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId) => [];

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query) => [];

    public Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(BaseItem item, User? user, bool allowMediaProbe, bool enablePathSubstitution, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MediaSourceInfo>>([]);

    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User? user = null) => [];

    public Task<MediaSourceInfo> GetMediaSource(BaseItem item, string mediaSourceId, string? liveStreamId, bool enablePathSubstitution, CancellationToken cancellationToken) =>
        throw new NotSupportedException("anemone-testkit: FakeMediaSourceManager does not implement media source lookup");

    public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(OpenLiveStreamResponseToReturn ?? throw new InvalidOperationException("anemone-testkit: set OpenLiveStreamResponseToReturn before calling OpenLiveStream"));

    public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(LiveStreamRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("anemone-testkit: FakeMediaSourceManager does not implement direct-stream providers");

    public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken) =>
        throw new NotSupportedException("anemone-testkit: FakeMediaSourceManager does not implement live streams");

    public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken) =>
        throw new NotSupportedException("anemone-testkit: FakeMediaSourceManager does not implement direct-stream providers");

    public ILiveStream GetLiveStreamInfo(string id) => throw new NotSupportedException("anemone-testkit: FakeMediaSourceManager does not implement live streams");

    public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId) => throw new NotSupportedException("anemone-testkit: FakeMediaSourceManager does not implement live streams");

    public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(ActiveRecordingInfo info, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MediaSourceInfo>>([]);

    public Task CloseLiveStream(string id)
    {
        ClosedLiveStreamIds.Add(id);
        return Task.CompletedTask;
    }

    public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken) =>
        throw new NotSupportedException("anemone-testkit: FakeMediaSourceManager does not implement live streams");

    public bool SupportsDirectStream(string path, MediaProtocol protocol) => false;

    public MediaProtocol GetPathProtocol(string path) => MediaProtocol.File;

    public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User user)
    {
    }

    public Task AddMediaInfoWithProbe(MediaSourceInfo mediaSource, bool isAudio, string cacheKey, bool addProbeDelay, bool isLiveStream, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>
/// <see cref="IAttachmentExtractor"/> fake. Only exercised by <c>AnemoneTranscodeManager.StartFfMpeg</c>
/// when <c>state.SubtitleStream is not null &amp;&amp; SubtitleDeliveryMethod == Encode</c> -
/// <see cref="StreamStateBuilder"/> leaves the subtitle stream null by default, so most tests never touch
/// this. Calls are recorded for the tests that do.
/// </summary>
public sealed class FakeAttachmentExtractor : IAttachmentExtractor
{
    public List<string> ExtractedFor { get; } = [];

    public Task ExtractAllAttachments(string inputFile, MediaSourceInfo mediaSource, CancellationToken cancellationToken)
    {
        ExtractedFor.Add(inputFile);
        return Task.CompletedTask;
    }

    public Task<(MediaAttachment Attachment, Stream Stream)> GetAttachment(BaseItem item, string mediaSourceId, int attachmentStreamIndex, CancellationToken cancellationToken) =>
        throw new NotSupportedException("anemone-testkit: FakeAttachmentExtractor does not implement attachment reads");
}
