using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Cluster.Agents.Protocol;
using Jellyfin.Plugin.Cluster.Contracts;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Cluster.Agents;

/// <summary>
/// Holds every connected agent and implements placement (<see cref="Pick"/>). Also owns the hello/welcome
/// handshake for newly accepted WebSockets (<see cref="RunConnectionAsync"/>), called from
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
    public async Task RunConnectionAsync(WebSocket socket, IPAddress? remoteAddress, CancellationToken cancellationToken)
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
                _logger.LogInformation("jfc: agent {Remote} disconnected before sending hello", remoteAddress);
                return;
            }

            var frame = Frame.Parse(json);
            if (frame is not HelloFrame h)
            {
                _logger.LogWarning("jfc: agent {Remote} sent '{Type}' instead of hello", remoteAddress, frame.GetType().Name);
                await RejectAsync(socket, "expected hello as the first frame", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrWhiteSpace(h.Name) || string.IsNullOrWhiteSpace(h.Ffmpeg?.Version))
            {
                _logger.LogWarning("jfc: agent {Remote} sent invalid hello (name/ffmpeg.version missing)", remoteAddress);
                await RejectAsync(socket, "name and ffmpeg.version are required", cancellationToken).ConfigureAwait(false);
                return;
            }

            hello = h;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("jfc: agent {Remote} hello timed out", remoteAddress);
            AbortQuietly(socket);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "jfc: agent {Remote} sent a malformed hello", remoteAddress);
            AbortQuietly(socket);
            return;
        }

        var info = new AgentInfo(
            hello.Name,
            hello.Version ?? string.Empty,
            hello.Platform ?? string.Empty,
            hello.Ffmpeg!.Path ?? string.Empty,
            hello.Ffmpeg.Version,
            hello.Ffmpeg.Hwaccels ?? Array.Empty<string>(),
            hello.Ffmpeg.Encoders ?? Array.Empty<string>(),
            hello.Ffmpeg.Decoders ?? Array.Empty<string>(),
            hello.Ffmpeg.Filters ?? Array.Empty<string>(),
            (hello.Mounts ?? Array.Empty<AgentMountFrame>()).Select(m => new AgentMount(m.Path, m.Ok)).ToList(),
            hello.MaxSessions,
            DateTimeOffset.UtcNow);

        if (_agents.TryRemove(hello.Name, out var existing))
        {
            _logger.LogInformation("jfc: agent {Name} reconnected from {Remote}, closing previous connection", hello.Name, remoteAddress);
            await existing.CloseAsync("replaced by new connection", CancellationToken.None).ConfigureAwait(false);
        }

        var welcome = new WelcomeFrame(
            new ServerInfo(_appHost.ApplicationVersionString, _mediaEncoder.EncoderVersion?.ToString() ?? string.Empty),
            ResolveIngestBase(),
            PingIntervalSeconds);

        var connection = new AgentConnection(socket, info, PingIntervalSeconds, _loggerFactory.CreateLogger<AgentConnection>());

        try
        {
            var bytes = Encoding.UTF8.GetBytes(Frame.Serialize(welcome));
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "jfc: agent {Name} disconnected before welcome could be sent", hello.Name);
            return;
        }

        _agents[hello.Name] = connection;
        _logger.LogInformation(
            "jfc: agent {Name} connected from {Remote} (platform={Platform} ffmpeg={Ffmpeg} maxSessions={MaxSessions})",
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
            _logger.LogInformation("jfc: agent {Name} disconnected", hello.Name);
        }
    }

    public IAgentConnection? Pick(JobRequirements requirements)
    {
        ArgumentNullException.ThrowIfNull(requirements);

        var config = Plugin.Instance?.Configuration;
        var deadAfter = TimeSpan.FromSeconds(config?.AgentDeadAfterSeconds ?? 30);
        var requireMatchingFfmpeg = config?.RequireMatchingFfmpeg ?? true;
        var serverFfmpegVersion = _mediaEncoder.EncoderVersion?.ToString();

        return PickFrom(_agents.Values, requirements, DateTimeOffset.UtcNow, deadAfter, requireMatchingFfmpeg, serverFfmpegVersion, _logger);
    }

    /// <summary>
    /// Pure placement algorithm, factored out of <see cref="Pick"/> so it can be unit-tested against
    /// hand-written fake <see cref="IAgentConnection"/>s without a live registry. See RESEARCH.md §6
    /// "Scheduling v0" and PROTOCOL.md's argument-rewriting rule 4/5 for the requirements this checks.
    /// </summary>
    internal static IAgentConnection? PickFrom(
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
            logger?.LogDebug("jfc: server ffmpeg version unknown ('{Raw}'); skipping ffmpeg version match check", serverFfmpegVersion);
        }

        IAgentConnection? best = null;
        var bestRatio = double.MaxValue;

        foreach (var agent in agents)
        {
            if (!agent.IsConnected)
            {
                logger?.LogDebug("jfc: excluding agent {Name}: not connected", agent.Info.Name);
                continue;
            }

            if (now - agent.LastSeen > deadAfter)
            {
                logger?.LogDebug("jfc: excluding agent {Name}: dead (last seen {LastSeen})", agent.Info.Name, agent.LastSeen);
                continue;
            }

            if (agent.Info.MaxSessions <= 0 || agent.ActiveJobs >= agent.Info.MaxSessions)
            {
                logger?.LogDebug("jfc: excluding agent {Name}: at capacity ({Active}/{Max})", agent.Info.Name, agent.ActiveJobs, agent.Info.MaxSessions);
                continue;
            }

            if (!HasAll(agent.Info.Hwaccels, requirements.Hwaccels, out var missingHw))
            {
                logger?.LogDebug("jfc: excluding agent {Name}: missing hwaccel '{Missing}'", agent.Info.Name, missingHw);
                continue;
            }

            if (!HasAll(agent.Info.Encoders, requirements.Encoders, out var missingEnc))
            {
                logger?.LogDebug("jfc: excluding agent {Name}: missing encoder '{Missing}'", agent.Info.Name, missingEnc);
                continue;
            }

            if (!HasAll(agent.Info.Decoders, requirements.Decoders, out var missingDec))
            {
                logger?.LogDebug("jfc: excluding agent {Name}: missing decoder '{Missing}'", agent.Info.Name, missingDec);
                continue;
            }

            if (!HasAll(agent.Info.Filters, requirements.Filters, out var missingFilter))
            {
                logger?.LogDebug("jfc: excluding agent {Name}: missing filter '{Missing}'", agent.Info.Name, missingFilter);
                continue;
            }

            if (!MountsCover(agent.Info.Mounts, requirements.InputPaths, out var uncoveredPath))
            {
                logger?.LogDebug("jfc: excluding agent {Name}: no ok mount covers '{Path}'", agent.Info.Name, uncoveredPath);
                continue;
            }

            if (requireMatchingFfmpeg && serverMajorMinor is not null)
            {
                var agentMajorMinor = ParseMajorMinor(agent.Info.FfmpegVersion);
                if (agentMajorMinor is null || !string.Equals(agentMajorMinor, serverMajorMinor, StringComparison.Ordinal))
                {
                    logger?.LogDebug(
                        "jfc: excluding agent {Name}: ffmpeg '{Agent}' != server '{Server}'",
                        agent.Info.Name,
                        agent.Info.FfmpegVersion,
                        serverFfmpegVersion);
                    continue;
                }
            }

            var ratio = (double)agent.ActiveJobs / agent.Info.MaxSessions;
            if (best is null
                || ratio < bestRatio
                || (ratio == bestRatio && agent.ActiveJobs < best.ActiveJobs)
                || (ratio == bestRatio && agent.ActiveJobs == best.ActiveJobs && agent.LastSeen > best.LastSeen))
            {
                best = agent;
                bestRatio = ratio;
            }
        }

        return best;
    }

    /// <summary>Absolute base URL agents should reach this server on, trimmed of a trailing slash.</summary>
    public string ResolveIngestBase()
    {
        var configured = Plugin.Instance?.Configuration.IngestBaseUrl;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        try
        {
            return _appHost.GetApiUrlForLocalAccess(null, false).TrimEnd('/');
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "jfc: GetApiUrlForLocalAccess failed, falling back to GetSmartApiUrl");
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

            _logger.LogWarning("jfc: agent {Name} timed out (last seen {LastSeen}), closing", kvp.Key, kvp.Value.LastSeen);
            try
            {
                await kvp.Value.CloseAsync("dead: no heartbeat", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "jfc: error closing dead agent {Name}", kvp.Key);
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
                _logger.LogDebug(ex, "jfc: error closing agent {Name} during shutdown", c.Info.Name);
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

    private static bool HasAll(IReadOnlyList<string> available, IReadOnlyList<string> required, out string? missing)
    {
        missing = null;
        foreach (var r in required)
        {
            var found = false;
            foreach (var a in available)
            {
                if (string.Equals(a, r, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                missing = r;
                return false;
            }
        }

        return true;
    }

    private static bool MountsCover(IReadOnlyList<AgentMount> mounts, IReadOnlyList<string> inputPaths, out string? uncovered)
    {
        uncovered = null;
        foreach (var input in inputPaths)
        {
            var covered = false;
            foreach (var mount in mounts)
            {
                if (mount.Ok && IsUnderMount(input, mount.Path))
                {
                    covered = true;
                    break;
                }
            }

            if (!covered)
            {
                uncovered = input;
                return false;
            }
        }

        return true;
    }

    private static bool IsUnderMount(string path, string mountPath)
    {
        var mount = mountPath.TrimEnd('/', '\\');
        if (mount.Length == 0 || !path.StartsWith(mount, StringComparison.Ordinal))
        {
            return false;
        }

        if (path.Length == mount.Length)
        {
            return true;
        }

        var next = path[mount.Length];
        return next is '/' or '\\';
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
