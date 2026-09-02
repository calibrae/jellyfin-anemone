using Jellyfin.Plugin.Anemone.Contracts;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Anemone.Transcoding;

/// <summary>
/// Decides local-vs-remote and rewrites the ffmpeg command line. Wraps the pure <see cref="RoutePlanner"/>
/// logic with the bits that need live services: agent placement, ingest token issuance, and the server's
/// own base URL.
/// </summary>
public sealed class JobRouter : IJobRouter
{
    private readonly IAgentRegistry _registry;
    private readonly IIngestTokenStore _tokenStore;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<JobRouter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JobRouter"/> class.
    /// </summary>
    /// <param name="registry">The connected-agent registry.</param>
    /// <param name="tokenStore">The ingest token store.</param>
    /// <param name="mediaEncoder">The server's own <see cref="IMediaEncoder"/> (ffmpeg version for diagnostics).</param>
    /// <param name="applicationHost">The server application host (ingest base URL fallback).</param>
    /// <param name="logger">The logger.</param>
    public JobRouter(
        IAgentRegistry registry,
        IIngestTokenStore tokenStore,
        IMediaEncoder mediaEncoder,
        IServerApplicationHost applicationHost,
        ILogger<JobRouter> logger)
    {
        _registry = registry;
        _tokenStore = tokenStore;
        _mediaEncoder = mediaEncoder;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    /// <inheritdoc />
    public RoutePlan? TryPlan(StreamState state, string outputPath, string commandLineArguments, TranscodingJobType jobType)
    {
        try
        {
            return TryPlanCore(state, outputPath, commandLineArguments);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "anemone: not routing {Path}: exception while planning", outputPath);
            return null;
        }
    }

    private RoutePlan? TryPlanCore(StreamState state, string outputPath, string commandLineArguments)
    {
        var argv = ArgumentLine.Split(commandLineArguments);
        var analysis = RoutePlanner.Analyze(argv);

        if (!analysis.IsRoutable)
        {
            _logger.LogDebug("anemone: not routing {Path}: {Reason}", outputPath, analysis.NotRoutableReason);
            return null;
        }

        if (state.MediaSource?.Protocol != MediaProtocol.File)
        {
            _logger.LogDebug("anemone: not routing {Path}: media source protocol is {Protocol}, not File", outputPath, state.MediaSource?.Protocol);
            return null;
        }

        var candidates = _registry.Candidates(analysis.Requirements);
        if (candidates.Count == 0)
        {
            _logger.LogDebug(
                "anemone: not routing {Path}: no candidate agent (connected, alive, free capacity, mounts covering input, ffmpeg-version policy)",
                outputPath);
            return null;
        }

        var allowHwProfileTranslation = Plugin.Instance?.Configuration.AllowHwProfileTranslation ?? true;
        var sourceProfile = HwTranslator.IdentifySourceProfile(argv);

        foreach (var candidate in candidates)
        {
            if (!IsProfileTranslationAllowed(candidate.Info.Hwaccel, sourceProfile, allowHwProfileTranslation))
            {
                _logger.LogDebug(
                    "anemone: rejecting candidate {Name}: hw profile translation disabled and agent profile '{AgentProfile}' != source '{SourceProfile}'",
                    candidate.Info.Name,
                    candidate.Info.Hwaccel,
                    sourceProfile);
                continue;
            }

            if (!MountPathMapper.TryMapInputPaths(argv, candidate.Info.Mounts, out var pathMapped, out var pathReason))
            {
                _logger.LogDebug("anemone: rejecting candidate {Name}: {Reason}", candidate.Info.Name, pathReason);
                continue;
            }

            if (!HwTranslator.TryTranslate(pathMapped, candidate.Info, out var translatedArgv, out var hwReason))
            {
                _logger.LogDebug("anemone: rejecting candidate {Name}: {Reason}", candidate.Info.Name, hwReason);
                continue;
            }

            var jobId = Guid.NewGuid().ToString("N");
            var targetDirectory = Path.GetDirectoryName(outputPath)
                ?? throw new ArgumentException($"Provided path ({outputPath}) is not valid.", nameof(outputPath));
            var filePrefix = Path.GetFileNameWithoutExtension(outputPath);
            var token = _tokenStore.Issue(jobId, targetDirectory, filePrefix);

            var configuredBase = Plugin.Instance?.Configuration.IngestBaseUrl;
            var ingestBase = !string.IsNullOrWhiteSpace(configuredBase)
                ? configuredBase!
                : _applicationHost.GetApiUrlForLocalAccess(null, false);
            ingestBase = ingestBase.TrimEnd('/');

            var rewritten = RoutePlanner.Rewrite(translatedArgv, ingestBase, jobId, token);

            var reason = $"agent '{candidate.Info.Name}' (server ffmpeg {_mediaEncoder.EncoderVersion}, source hw profile '{sourceProfile}' -> agent hw profile '{candidate.Info.Hwaccel}': {hwReason})";
            var spec = new RemoteJobSpec(jobId, rewritten, token, $"Transcode {filePrefix}");

            return new RoutePlan(candidate, spec, targetDirectory, filePrefix, reason);
        }

        _logger.LogDebug("anemone: not routing {Path}: no candidate could run the job (path mapping / hw translation all failed)", outputPath);
        return null;
    }

    /// <summary>
    /// The <see cref="Configuration.PluginConfiguration.AllowHwProfileTranslation"/> gate: when translation
    /// is disallowed, only a candidate whose profile already matches the source is eligible. Factored out
    /// as a pure function (mirrors <see cref="Agents.AgentHub.CandidatesFrom"/>'s split) so it's unit-testable
    /// without a live <see cref="Plugin.Instance"/>.
    /// </summary>
    internal static bool IsProfileTranslationAllowed(string agentProfile, string sourceProfile, bool allowHwProfileTranslation)
        => allowHwProfileTranslation || string.Equals(agentProfile, sourceProfile, StringComparison.OrdinalIgnoreCase);
}
