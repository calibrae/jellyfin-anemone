using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Anemone.Agents.Protocol;
using Jellyfin.Plugin.Anemone.Contracts;
using Jellyfin.Plugin.Anemone.Transcoding;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Anemone.Agents;

/// <summary>
/// Holds every connected agent and implements placement (<see cref="Candidates"/>). Also owns the
/// hello/welcome handshake for newly accepted WebSockets (<see cref="RunConnectionAsync"/>), called from
/// <see cref="AgentWebSocketController"/>.
/// </summary>
public sealed class AgentHub : IAgentRegistry
{
    private const int PingIntervalSeconds = 10;

    private readonly ConcurrentDictionary<string, AgentConnection> _agents = new();
    private readonly IServerApplicationHost _appHost;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AgentHub> _logger;

    public AgentHub(IServerApplicationHost appHost, IMediaEncoder mediaEncoder, ILoggerFactory loggerFactory, ILogger<AgentHub> logger)
    {
        _appHost = appHost;
        _mediaEncoder = mediaEncoder;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public IReadOnlyList<IAgentConnection> Agents => _agents.Values.ToList();

    /// <summary>
    /// Runs one accepted WebSocket end to end: reads the mandatory first <c>hello</c> frame (10s timeout),
    /// replies <c>welcome</c>/<c>reject</c>, registers the agent (replacing any same-name connection), then
    /// blocks running the connection until it ends. Removes the agent from the registry on return.
    /// </summary>
    public async Task RunConnectionAsync(WebSocket socket, IPAddress? remoteAddress, IPAddress? localAddress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);

        HelloFrame hello;
        try
        {
            using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            helloCts.CancelAfter(TimeSpan.FromSeconds(10));
            var json = await AgentConnection.ReceiveTextMessageAsync(socket, helloCts.Token).ConfigureAwait(false);
            if (json is null)
            {
                _logger.LogInformation("anemone: agent {Remote} disconnected before sending hello", remoteAddress);
                return;
            }

            var frame = Frame.Parse(json);
            if (frame is not HelloFrame h)
            {
                _logger.LogWarning("anemone: agent {Remote} sent '{Type}' instead of hello", remoteAddress, frame.GetType().Name);
                await RejectAsync(socket, "expected hello as the first frame", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrWhiteSpace(h.Name) || string.IsNullOrWhiteSpace(h.Ffmpeg?.Version))
            {
                _logger.LogWarning("anemone: agent {Remote} sent invalid hello (name/ffmpeg.version missing)", remoteAddress);
                await RejectAsync(socket, "name and ffmpeg.version are required", cancellationToken).ConfigureAwait(false);
                return;
            }

            hello = h;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("anemone: agent {Remote} hello timed out", remoteAddress);
            AbortQuietly(socket);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "anemone: agent {Remote} sent a malformed hello", remoteAddress);
            AbortQuietly(socket);
            return;
        }

        var ffmpegHwaccels = hello.Ffmpeg!.Hwaccels ?? Array.Empty<string>();
        var hwaccel = string.IsNullOrWhiteSpace(hello.Hwaccel)
            ? HwTranslator.InferProfile(ffmpegHwaccels, hello.Platform)
            : hello.Hwaccel;

        var info = new AgentInfo(
            hello.Name,
            hello.Version ?? string.Empty,
            hello.Platform ?? string.Empty,
            hello.Ffmpeg.Path ?? string.Empty,
            hello.Ffmpeg.Version,
            ffmpegHwaccels,
            hello.Ffmpeg.Encoders ?? Array.Empty<string>(),
            hello.Ffmpeg.Decoders ?? Array.Empty<string>(),
            hello.Ffmpeg.Filters ?? Array.Empty<string>(),
            (hello.Mounts ?? Array.Empty<AgentMountFrame>()).Select(m => new AgentMount(m.Path, m.Ok, m.ServerPath)).ToList(),
            hello.MaxSessions,
            DateTimeOffset.UtcNow,
            hwaccel,
            hello.HwaccelDevice);

        if (_agents.TryRemove(hello.Name, out var existing))
        {
            _logger.LogInformation("anemone: agent {Name} reconnected from {Remote}, closing previous connection", hello.Name, remoteAddress);
            await existing.CloseAsync("replaced by new connection", CancellationToken.None).ConfigureAwait(false);
        }

        var ingestBase = ResolveIngestBase(localAddress);

        var welcome = new WelcomeFrame(
            new ServerInfo(_appHost.ApplicationVersionString, _mediaEncoder.EncoderVersion?.ToString() ?? string.Empty),
            ingestBase,
            PingIntervalSeconds);

        var connection = new AgentConnection(socket, info, ingestBase, PingIntervalSeconds, _loggerFactory.CreateLogger<AgentConnection>());

        try
        {
            var bytes = Encoding.UTF8.GetBytes(Frame.Serialize(welcome));
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "anemone: agent {Name} disconnected before welcome could be sent", hello.Name);
            return;
        }

        _agents[hello.Name] = connection;
        _logger.LogInformation(
            "anemone: agent {Name} connected from {Remote} (platform={Platform} ffmpeg={Ffmpeg} maxSessions={MaxSessions})",
            hello.Name,
            remoteAddress,
            info.Platform,
            info.FfmpegVersion,
            info.MaxSessions);

        try
        {
            await connection.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ((ICollection<KeyValuePair<string, AgentConnection>>)_agents)
                .Remove(new KeyValuePair<string, AgentConnection>(hello.Name, connection));
            _logger.LogInformation("anemone: agent {Name} disconnected", hello.Name);
        }
    }

    public IReadOnlyList<IAgentConnection> Candidates(JobRequirements requirements)
    {
        ArgumentNullException.ThrowIfNull(requirements);

        var config = Plugin.Instance?.Configuration;
        var deadAfter = TimeSpan.FromSeconds(config?.AgentDeadAfterSeconds ?? 30);
        var requireMatchingFfmpeg = config?.RequireMatchingFfmpeg ?? true;
        var serverFfmpegVersion = _mediaEncoder.EncoderVersion?.ToString();

        return CandidatesFrom(_agents.Values, requirements, DateTimeOffset.UtcNow, deadAfter, requireMatchingFfmpeg, serverFfmpegVersion, _logger);
    }

    /// <summary>
    /// Pure placement algorithm, factored out of <see cref="Candidates"/> so it can be unit-tested against
    /// hand-written fake <see cref="IAgentConnection"/>s without a live registry. See RESEARCH.md §6
    /// "Scheduling v0" and PROTOCOL.md "Path mapping" for the mount-coverage rule this checks.
    ///
    /// Deliberately does NOT filter on hwaccel/encoders/decoders/filters: with per-agent hardware
    /// translation, an agent whose raw capability lists don't match the server's own can still run the job
    /// once <see cref="Transcoding.HwTranslator"/> rewrites it. That's <see cref="Transcoding.JobRouter"/>'s
    /// job, tried in the least-loaded-first order this returns.
    /// </summary>
    internal static IReadOnlyList<IAgentConnection> CandidatesFrom(
        IEnumerable<IAgentConnection> agents,
        JobRequirements requirements,
        DateTimeOffset now,
        TimeSpan deadAfter,
        bool requireMatchingFfmpeg,
        string? serverFfmpegVersion,
        ILogger? logger = null)
    {
        var serverMajorMinor = ParseMajorMinor(serverFfmpegVersion);
        if (requireMatchingFfmpeg && serverMajorMinor is null)
        {
            logger?.LogDebug("anemone: server ffmpeg version unknown ('{Raw}'); skipping ffmpeg version match check", serverFfmpegVersion);
        }

        var candidates = new List<(IAgentConnection Agent, double Ratio)>();

        foreach (var agent in agents)
        {
            if (!agent.IsConnected)
            {
                logger?.LogDebug("anemone: excluding agent {Name}: not connected", agent.Info.Name);
                continue;
            }

            if (now - agent.LastSeen > deadAfter)
            {
                logger?.LogDebug("anemone: excluding agent {Name}: dead (last seen {LastSeen})", agent.Info.Name, agent.LastSeen);
                continue;
            }

            if (agent.Info.MaxSessions <= 0 || agent.ActiveJobs >= agent.Info.MaxSessions)
            {
                logger?.LogDebug("anemone: excluding agent {Name}: at capacity ({Active}/{Max})", agent.Info.Name, agent.ActiveJobs, agent.Info.MaxSessions);
                continue;
            }

            if (!MountsCover(agent.Info.Mounts, requirements.InputPaths, out var uncoveredPath))
            {
                logger?.LogDebug("anemone: excluding agent {Name}: no ok mount covers '{Path}'", agent.Info.Name, uncoveredPath);
                continue;
            }

            if (requireMatchingFfmpeg && serverMajorMinor is not null)
            {
                var agentMajorMinor = ParseMajorMinor(agent.Info.FfmpegVersion);
                if (agentMajorMinor is null || !string.Equals(agentMajorMinor, serverMajorMinor, StringComparison.Ordinal))
                {
                    logger?.LogDebug(
                        "anemone: excluding agent {Name}: ffmpeg '{Agent}' != server '{Server}'",
                        agent.Info.Name,
                        agent.Info.FfmpegVersion,
                        serverFfmpegVersion);
                    continue;
                }
            }

            candidates.Add((agent, (double)agent.ActiveJobs / agent.Info.MaxSessions));
        }

        return candidates
            .OrderBy(c => c.Ratio)
            .ThenBy(c => c.Agent.ActiveJobs)
            .ThenByDescending(c => c.Agent.LastSeen)
            .Select(c => c.Agent)
            .ToList();
    }

    /// <summary>
    /// Absolute base URL an agent should upload segments to, trimmed of a trailing slash.
    /// </summary>
    /// <param name="localAddress">
    /// The local endpoint of that agent's own control connection, i.e. the address it actually reached us
    /// on. A fleet is rarely single-homed: trish reaches this server over a Thunderbolt link (10.240.0.1)
    /// that abbacchio cannot route to at all, while abbacchio arrives on the LAN address. Answering each
    /// agent with the interface it already used keeps every agent on its fastest working path and needs no
    /// per-agent configuration. <see cref="PluginConfiguration.IngestBaseUrl"/> overrides this when the
    /// server is behind NAT or a proxy and the local address is not what agents can reach.
    /// </param>
    public string ResolveIngestBase(IPAddress? localAddress = null)
    {
        var configured = Plugin.Instance?.Configuration.IngestBaseUrl;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        var port = Plugin.Instance?.Configuration.AgentListenPort ?? 0;
        if (localAddress is not null && port > 0 && !IPAddress.IsLoopback(localAddress))
        {
            var host = localAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? $"[{localAddress}]"
                : localAddress.ToString();
            return $"http://{host}:{port}";
        }

        try
        {
            return _appHost.GetApiUrlForLocalAccess(null, false).TrimEnd('/');
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "anemone: GetApiUrlForLocalAccess failed, falling back to GetSmartApiUrl");
            return _appHost.GetSmartApiUrl(IPAddress.Loopback).TrimEnd('/');
        }
    }

    /// <summary>Closes every agent whose last-seen timestamp is older than <paramref name="deadAfterSeconds"/>.</summary>
    internal async Task CloseDeadAgentsAsync(int deadAfterSeconds, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var deadline = TimeSpan.FromSeconds(deadAfterSeconds);

        foreach (var kvp in _agents.ToArray())
        {
            if (now - kvp.Value.LastSeen <= deadline)
            {
                continue;
            }

            if (!((ICollection<KeyValuePair<string, AgentConnection>>)_agents).Remove(kvp))
            {
                continue;
            }

            _logger.LogWarning("anemone: agent {Name} timed out (last seen {LastSeen}), closing", kvp.Key, kvp.Value.LastSeen);
            try
            {
                await kvp.Value.CloseAsync("dead: no heartbeat", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "anemone: error closing dead agent {Name}", kvp.Key);
            }
        }
    }

    /// <summary>Closes every connected agent. Used on plugin/server shutdown.</summary>
    internal async Task CloseAllAsync(string reason)
    {
        var conns = _agents.Values.ToArray();
        _agents.Clear();

        foreach (var c in conns)
        {
            try
            {
                await c.CloseAsync(reason, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "anemone: error closing agent {Name} during shutdown", c.Info.Name);
            }
        }
    }

    private static async Task RejectAsync(WebSocket socket, string reason, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(Frame.Serialize(new RejectFrame(reason)));
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best effort; the agent may already be gone
        }
    }

    private static void AbortQuietly(WebSocket socket)
    {
        try
        {
            socket.Abort();
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>
    /// True when every input path is covered by an <c>ok</c> mount on a path-segment boundary, matched
    /// against each mount's <see cref="AgentMount.EffectiveServerPath"/> (see PROTOCOL.md "Path mapping").
    /// </summary>
    private static bool MountsCover(IReadOnlyList<AgentMount> mounts, IReadOnlyList<string> inputPaths, out string? uncovered)
    {
        uncovered = null;
        foreach (var input in inputPaths)
        {
            if (MountPathMapper.FindLongestMatch(mounts, input) is null)
            {
                uncovered = input;
                return false;
            }
        }

        return true;
    }

    private static string? ParseMajorMinor(string? version)
    {
        if (string.IsNullOrEmpty(version))
        {
            return null;
        }

        var match = Regex.Match(version, @"\d+\.\d+", RegexOptions.None, TimeSpan.FromMilliseconds(200));
        return match.Success ? match.Value : null;
    }
}
