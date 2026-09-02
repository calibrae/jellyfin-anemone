using Jellyfin.Plugin.Anemone.Agents;
using Jellyfin.Plugin.Anemone.Configuration;
using Jellyfin.Plugin.Anemone.Ingest;
using Jellyfin.Plugin.Anemone.TestKit;

namespace Jellyfin.Plugin.Anemone.IntegrationTests;

/// <summary>
/// Boots the REAL <see cref="AnemoneListener"/> (real Kestrel, real <see cref="AgentHub"/>, real
/// <see cref="IngestHandler"/>/<see cref="IngestTokenStore"/>) on an OS-assigned ephemeral port - never
/// the production default (8097): PROTOCOL.md and DEPLOY.md are explicit that this plugin runs live on
/// this machine, so a hardcoded port would either collide with it or, worse, silently interact with it.
/// Everything below the listener (Kestrel, WebSocket upgrade handling, chunked HTTP PUT) is the genuine
/// production stack; only the Jellyfin-host-facing dependencies (<see cref="IServerApplicationHost"/>,
/// <see cref="IMediaEncoder"/>) are TestKit fakes.
/// </summary>
public sealed class AnemoneIntegrationHarness : IAsyncDisposable
{
    private AnemoneIntegrationHarness(
        TempDirectory root,
        PluginInstanceScope pluginScope,
        FakeLoggerFactory loggerFactory,
        FakeServerApplicationHost appHost,
        FakeMediaEncoder mediaEncoder,
        AgentHub hub,
        IngestTokenStore tokenStore,
        AnemoneListener listener,
        int port)
    {
        Root = root;
        PluginScope = pluginScope;
        LoggerFactory = loggerFactory;
        AppHost = appHost;
        MediaEncoder = mediaEncoder;
        Hub = hub;
        TokenStore = tokenStore;
        Listener = listener;
        Port = port;
    }

    public TempDirectory Root { get; }

    public PluginInstanceScope PluginScope { get; }

    public PluginConfiguration Configuration => PluginScope.Plugin.Configuration;

    public FakeLoggerFactory LoggerFactory { get; }

    public FakeServerApplicationHost AppHost { get; }

    public FakeMediaEncoder MediaEncoder { get; }

    public AgentHub Hub { get; }

    public IngestTokenStore TokenStore { get; }

    public AnemoneListener Listener { get; }

    public int Port { get; }

    public string WebSocketUrl => $"ws://127.0.0.1:{Port}/Anemone/agents/ws";

    public string HttpBaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>Starts the real listener stack on a freshly-found ephemeral port.</summary>
    /// <param name="configure">Applied to the real <see cref="PluginConfiguration"/> before the listener starts (SharedSecret is already set; override/add to it here).</param>
    public static async Task<AnemoneIntegrationHarness> StartAsync(Action<PluginConfiguration>? configure = null)
    {
        // Real Kestrel + real WebSocket read/write/ping loops (per connection) + JobLogger's own
        // synchronous-on-first-evaluation StreamReader.EndOfStream (see FakeFfmpegScript/
        // FakeAgentConnection's remarks on the same thing) all compete for thread pool workers here, more
        // so than a single unit test ever would. Raising the floor avoids waiting out the runtime's
        // default (slow, ~1-2 new threads/sec under sustained demand) growth curve in these tests.
        ThreadPool.SetMinThreads(64, 64);

        var root = TempDirectory.Create("anemone-integration");
        var port = FreePort.Find();
        var httpBaseUrl = $"http://127.0.0.1:{port}";

        var pluginScope = new PluginInstanceScope(cfg =>
        {
            cfg.Enabled = true;
            cfg.SharedSecret = "integration-test-secret";
            cfg.AgentListenPort = port;

            // AgentHub.ResolveIngestBase's "the address the agent actually reached us on" branch
            // deliberately excludes loopback (a real agent is never on the same host as the server - see
            // its own remarks), so every test agent here (always loopback) would otherwise fall through
            // to IServerApplicationHost.GetApiUrlForLocalAccess. Defaulting IngestBaseUrl to the address
            // this harness's own listener is actually bound to means a REAL client (a real polyp, in
            // particular) can really reach it - the whole point of EndToEndTests. A test that wants to
            // exercise the GetApiUrlForLocalAccess fallback or the local-address branch instead can clear
            // this (or call AgentHub.ResolveIngestBase directly) via `configure` below, which runs after.
            cfg.IngestBaseUrl = httpBaseUrl;

            configure?.Invoke(cfg);
        });

        var loggerFactory = new FakeLoggerFactory();
        var appHost = new FakeServerApplicationHost { UrlToReturn = httpBaseUrl };
        var mediaEncoder = new FakeMediaEncoder { EncoderVersion = new Version(7, 1, 4) };

        var hub = new AgentHub(appHost, mediaEncoder, loggerFactory, loggerFactory.CreateLogger<AgentHub>());
        var tokenStore = new IngestTokenStore();
        var ingest = new IngestHandler(tokenStore, loggerFactory.CreateLogger<IngestHandler>());
        var wsEndpoint = new AgentWebSocketEndpoint(hub, loggerFactory.CreateLogger<AgentWebSocketEndpoint>());
        var listener = new AnemoneListener(wsEndpoint, ingest, loggerFactory, loggerFactory.CreateLogger<AnemoneListener>());

        await listener.StartAsync(CancellationToken.None).ConfigureAwait(false);

        return new AnemoneIntegrationHarness(root, pluginScope, loggerFactory, appHost, mediaEncoder, hub, tokenStore, listener, port);
    }

    public async ValueTask DisposeAsync()
    {
        // Order matters, and the bounded StopAsync token matters: Kestrel's graceful shutdown waits for
        // every in-flight "request" to finish, and a WebSocket upgrade counts as one for its whole
        // lifetime - AnemoneListener.StopAsync(CancellationToken.None) (what plain DisposeAsync() below
        // would call) would then wait FOREVER for a still-open agent connection a test forgot to close.
        // Closing every AgentConnection first (which cancels its read loop and best-effort closes the
        // socket) makes that graceful wait resolve immediately in the common case; the bounded token here
        // is the backstop for whatever doesn't. Once the token fires, Kestrel force-closes what's left
        // rather than continuing to wait - this is a test-harness-only workaround, not a claim that
        // production shutdown (which gets a real timeout from Jellyfin's own host, not
        // CancellationToken.None) has the same problem.
        await Hub.CloseAllAsync("test teardown").ConfigureAwait(false);

        try
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Listener.StopAsync(stopCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Best-effort: a connection that didn't close within the grace period shouldn't block the
            // rest of the test suite.
        }

        await Listener.DisposeAsync().ConfigureAwait(false);
        PluginScope.Dispose();
        Root.Dispose();
    }
}
