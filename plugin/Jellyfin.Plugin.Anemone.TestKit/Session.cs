using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.SyncPlay;

namespace Jellyfin.Plugin.Anemone.TestKit;

/// <summary>
/// <see cref="ISessionManager"/> fake. <see cref="AnemoneTranscodeManager"/>'s constructor subscribes to
/// <see cref="PlaybackProgress"/>/<see cref="PlaybackStart"/> (use <see cref="RaisePlaybackProgress"/> to
/// drive <c>PingTranscodingJob</c> indirectly, the way a real client heartbeat would) and calls
/// <see cref="ReportTranscodingInfo"/>/<see cref="ClearTranscodingInfo"/>/
/// <see cref="CloseLiveStreamIfNeededAsync"/> during the job lifecycle - all three are recorded here so
/// tests can assert on them directly instead of reaching into the manager's private state.
/// </summary>
public sealed class FakeSessionManager : ISessionManager
{
    public List<(string DeviceId, TranscodingInfo Info)> ReportedTranscodingInfo { get; } = [];

    public List<string> ClearedTranscodingInfoDeviceIds { get; } = [];

    public List<(string LiveStreamId, string? PlaySessionId)> ClosedLiveStreams { get; } = [];

    public event EventHandler<PlaybackProgressEventArgs>? PlaybackProgress;

    public event EventHandler<PlaybackProgressEventArgs>? PlaybackStart;

#pragma warning disable CS0067 // required by ISessionManager, never raised by this fake
    public event EventHandler<PlaybackStopEventArgs>? PlaybackStopped;

    public event EventHandler<SessionEventArgs>? SessionActivity;

    public event EventHandler<SessionEventArgs>? CapabilitiesChanged;

    public event EventHandler<SessionEventArgs>? SessionStarted;

    public event EventHandler<SessionEventArgs>? SessionEnded;

    public event EventHandler<SessionEventArgs>? SessionControllerConnected;
#pragma warning restore CS0067

    public IEnumerable<SessionInfo> Sessions => [];

    /// <summary>Fires <see cref="PlaybackProgress"/>, exactly as a client's periodic progress ping would.</summary>
    public void RaisePlaybackProgress(PlaybackProgressEventArgs args) => PlaybackProgress?.Invoke(this, args);

    /// <summary>Fires <see cref="PlaybackStart"/>.</summary>
    public void RaisePlaybackStart(PlaybackProgressEventArgs args) => PlaybackStart?.Invoke(this, args);

    public void ReportTranscodingInfo(string deviceId, TranscodingInfo info) => ReportedTranscodingInfo.Add((deviceId, info));

    public void ClearTranscodingInfo(string deviceId) => ClearedTranscodingInfoDeviceIds.Add(deviceId);

    public void ReportNowViewingItem(string sessionId, string itemId)
    {
    }

    public void ReportCapabilities(string sessionId, ClientCapabilities capabilities)
    {
    }

    public Task CloseLiveStreamIfNeededAsync(string liveStreamId, string? playSessionId)
    {
        ClosedLiveStreams.Add((liveStreamId, playSessionId));
        return Task.CompletedTask;
    }

    public void AddAdditionalUser(string sessionId, Guid userId)
    {
    }

    public void RemoveAdditionalUser(string sessionId, Guid userId)
    {
    }

    public Task<AuthenticationResult> AuthenticateDirect(AuthenticationRequest request) =>
        throw new NotSupportedException("anemone-testkit: FakeSessionManager does not implement authentication");

    public Task<AuthenticationResult> AuthenticateNewSession(AuthenticationRequest request) =>
        throw new NotSupportedException("anemone-testkit: FakeSessionManager does not implement authentication");

    public Task CloseIfNeededAsync(SessionInfo session) => Task.CompletedTask;

    public SessionInfo? GetSession(string sessionId, string client, string version) => null;

    public Task<SessionInfo> GetSessionByAuthenticationToken(string token, string deviceId, string remoteEndpoint) =>
        throw new NotSupportedException("anemone-testkit: FakeSessionManager does not implement token lookup");

    public Task<SessionInfo> GetSessionByAuthenticationToken(Device info, string? deviceId, string remoteEndpoint, string? appVersion) =>
        throw new NotSupportedException("anemone-testkit: FakeSessionManager does not implement token lookup");

    public IReadOnlyList<SessionInfoDto> GetSessions(Guid userId, string? deviceId, int? activeWithinSeconds, Guid? controllableUserToCheck, bool isApiKey) => [];

    public Task Logout(string accessToken) => Task.CompletedTask;

    public Task Logout(Device device) => Task.CompletedTask;

    public Task<SessionInfo> LogSessionActivity(string appName, string appVersion, string deviceId, string deviceName, string remoteEndPoint, User? user) =>
        throw new NotSupportedException("anemone-testkit: FakeSessionManager does not implement session activity logging");

    public Task OnPlaybackProgress(PlaybackProgressInfo info) => Task.CompletedTask;

    public Task OnPlaybackProgress(PlaybackProgressInfo info, bool isAutomated) => Task.CompletedTask;

    public Task OnPlaybackStart(PlaybackStartInfo info) => Task.CompletedTask;

    public Task OnPlaybackStopped(PlaybackStopInfo info) => Task.CompletedTask;

    public void OnSessionControllerConnected(SessionInfo info)
    {
    }

    public ValueTask ReportSessionEnded(string sessionId) => ValueTask.CompletedTask;

    public Task RevokeUserTokens(Guid userId, string? currentAccessToken) => Task.CompletedTask;

    public Task SendBrowseCommand(string controllingSessionId, string sessionId, BrowseRequest command, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendGeneralCommand(string controllingSessionId, string sessionId, GeneralCommand command, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendMessageCommand(string controllingSessionId, string sessionId, MessageCommand command, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendMessageToAdminSessions<T>(SessionMessageType name, T data, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendMessageToUserDeviceSessions<T>(string deviceId, SessionMessageType name, T data, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendMessageToUserSessions<T>(List<Guid> userIds, SessionMessageType name, T data, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendMessageToUserSessions<T>(List<Guid> userIds, SessionMessageType name, Func<T> dataFn, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendPlayCommand(string controllingSessionId, string sessionId, PlayRequest command, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendPlaystateCommand(string controllingSessionId, string sessionId, PlaystateRequest command, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendRestartRequiredNotification(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendSyncPlayCommand(string sessionId, SendCommand command, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendSyncPlayGroupUpdate<T>(string sessionId, GroupUpdate<T> command, CancellationToken cancellationToken) => Task.CompletedTask;

    public void UpdateDeviceName(string sessionId, string deviceName)
    {
    }
}
