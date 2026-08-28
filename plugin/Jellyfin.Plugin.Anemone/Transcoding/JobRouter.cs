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

        var agent = _registry.Pick(analysis.Requirements);
        if (agent is null)
        {
            _logger.LogDebug(
                "anemone: not routing {Path}: no agent satisfies requirements (hwaccels=[{Hwaccels}], encoders=[{Encoders}], decoders=[{Decoders}], filters=[{Filters}])",
                outputPath,
                string.Join(',', analysis.Requirements.Hwaccels),
                string.Join(',', analysis.Requirements.Encoders),
                string.Join(',', analysis.Requirements.Decoders),
                string.Join(',', analysis.Requirements.Filters));
            return null;
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

        var rewritten = RoutePlanner.Rewrite(argv, ingestBase, jobId, token);

        var reason = $"agent '{agent.Info.Name}' (server ffmpeg {_mediaEncoder.EncoderVersion}, hwaccels=[{string.Join(',', analysis.Requirements.Hwaccels)}], encoders=[{string.Join(',', analysis.Requirements.Encoders)}])";
        var spec = new RemoteJobSpec(jobId, rewritten, token, $"Transcode {filePrefix}");

        return new RoutePlan(agent, spec, targetDirectory, filePrefix, reason);
    }
}
