using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Anemone.Agents;

/// <summary>Startup banner + periodic dead-agent reaper. Registered via <c>services.AddHostedService&lt;T&gt;()</c>.</summary>
public sealed class AnemoneHostedService : IHostedService, IDisposable
{
    private static readonly TimeSpan ReapInterval = TimeSpan.FromSeconds(5);

    private readonly AgentHub _hub;
    private readonly ILogger<AnemoneHostedService> _logger;

    private CancellationTokenSource? _cts;
    private Task? _reaperTask;

    public AnemoneHostedService(AgentHub hub, ILogger<AnemoneHostedService> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        var pluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown";
        var ingestBase = _hub.ResolveIngestBase();
        var secretSet = !string.IsNullOrEmpty(config?.SharedSecret);

        _logger.LogInformation(
            "anemone: Anemone plugin {Version} loaded — enabled={Enabled} dryRun={DryRun} ingestBase={IngestBase} sharedSecret={SecretState}",
            pluginVersion,
            config?.Enabled ?? false,
            config?.DryRun ?? false,
            ingestBase,
            secretSet ? "set" : "NOT SET");

        if (!secretSet)
        {
            _logger.LogWarning(
                "anemone: SharedSecret is not configured — no agent will be able to authenticate. " +
                "Set it on the Anemone plugin config page.");
        }

        _cts = new CancellationTokenSource();
        _reaperTask = ReaperLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();

        if (_reaperTask is not null)
        {
            try
            {
                await _reaperTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
        }

        await _hub.CloseAllAsync("server shutting down").ConfigureAwait(false);
    }

    public void Dispose() => _cts?.Dispose();

    private async Task ReaperLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(ReapInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var deadAfterSeconds = Plugin.Instance?.Configuration.AgentDeadAfterSeconds ?? 30;
                await _hub.CloseDeadAgentsAsync(deadAfterSeconds, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
    }
}
