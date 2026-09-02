using Jellyfin.Plugin.Anemone.Configuration;
using Jellyfin.Plugin.Anemone.Transcoding;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.Anemone.TestKit;

/// <summary>
/// Wires up a real <see cref="AnemoneTranscodeManager"/> with every dependency this TestKit fakes, so a
/// test can construct one call and get straight to the scenario under test. Every fake is exposed as a
/// property for direct scripting/assertions. Owns a <see cref="TestKit.TempDirectory"/> and a real
/// <see cref="PluginInstanceScope"/> (see there for why one is required) - dispose the harness when done.
/// </summary>
public sealed class AnemoneTranscodeManagerHarness : IDisposable
{
    public AnemoneTranscodeManagerHarness(Action<PluginConfiguration>? configurePlugin = null)
    {
        Root = TempDirectory.Create("anemone-manager");
        AppPaths = new FakeApplicationPaths(Root);
        ServerAppPaths = new FakeServerApplicationPaths(AppPaths);
        ConfigManager = new FakeServerConfigurationManager(ServerAppPaths, AppPaths);
        LoggerFactory = new FakeLoggerFactory();
        FileSystem = new RealFileSystem();
        UserManager = new FakeUserManager();
        SessionManager = new FakeSessionManager();
        // StartFfMpeg requires a non-empty EncoderPath unconditionally, even on a route that never
        // touches it (the remote path never spawns a local process) - ArgumentException.ThrowIfNullOrEmpty
        // runs before the routing decision. UseFakeFfmpeg overrides this with a real, runnable script for
        // any test that exercises the LOCAL path; this placeholder just satisfies the guard for tests that
        // only exercise remote routing and never expect a local process to actually be spawned.
        MediaEncoder = new FakeMediaEncoder { EncoderPath = "/opt/anemone/ffmpeg-placeholder-not-runnable" };
        MediaSourceManager = new FakeMediaSourceManager();
        AttachmentExtractor = new FakeAttachmentExtractor();
        EncodingHelper = EncodingHelperFactory.Create(AppPaths, MediaEncoder, ConfigManager);
        Router = new FakeJobRouter();
        TokenStore = new FakeIngestTokenStore();

        // A real Plugin.Instance is required: AnemoneTranscodeManager.StartFfMpeg only even consults the
        // router when Plugin.Instance is non-null and Enabled - see PluginInstanceScope's remarks.
        // AgentStartTimeoutSeconds is set low (not zero: Math.Max(1, ...) floors it at 1s in the manager)
        // so a test that deliberately exercises the "agent never acks" timeout doesn't wait 15 real seconds.
        PluginScope = new PluginInstanceScope(
            cfg =>
            {
                cfg.Enabled = true;
                cfg.DryRun = false;
                cfg.AgentStartTimeoutSeconds = 1;
                configurePlugin?.Invoke(cfg);
            },
            AppPaths);

        Manager = new AnemoneTranscodeManager(
            LoggerFactory,
            FileSystem,
            AppPaths,
            ConfigManager,
            UserManager,
            SessionManager,
            EncodingHelper,
            MediaEncoder,
            MediaSourceManager,
            AttachmentExtractor,
            Router,
            TokenStore);
    }

    public TempDirectory Root { get; }

    public FakeApplicationPaths AppPaths { get; }

    public FakeServerApplicationPaths ServerAppPaths { get; }

    public FakeServerConfigurationManager ConfigManager { get; }

    public FakeLoggerFactory LoggerFactory { get; }

    public RealFileSystem FileSystem { get; }

    public FakeUserManager UserManager { get; }

    public FakeSessionManager SessionManager { get; }

    public FakeMediaEncoder MediaEncoder { get; }

    public FakeMediaSourceManager MediaSourceManager { get; }

    public FakeAttachmentExtractor AttachmentExtractor { get; }

    public EncodingHelper EncodingHelper { get; }

    public FakeJobRouter Router { get; }

    public FakeIngestTokenStore TokenStore { get; }

    public PluginInstanceScope PluginScope { get; }

    public PluginConfiguration Configuration => PluginScope.Plugin.Configuration;

    public AnemoneTranscodeManager Manager { get; }

    /// <summary>A <see cref="StreamStateBuilder"/> pre-wired to this harness's <see cref="Manager"/>/<see cref="MediaSourceManager"/>.</summary>
    public StreamStateBuilder NewState() => new StreamStateBuilder()
        .WithTranscodeManager(Manager)
        .WithMediaSourceManager(MediaSourceManager);

    /// <summary>A path under this harness's temp root (parent directories need not exist yet - <c>StartFfMpeg</c> creates them).</summary>
    public string OutputPath(string fileName) => Path.Combine(Root.Path, "transcodes", fileName);

    /// <summary>
    /// Points <see cref="MediaEncoder"/>'s <c>EncoderPath</c> at a freshly-written <see cref="FakeFfmpegScript"/>
    /// that creates <paramref name="outputPath"/> - wires up the LOCAL transcode path of <c>StartFfMpeg</c>
    /// for a test. Returns the script path.
    /// </summary>
    public string UseFakeFfmpeg(
        string outputPath,
        IReadOnlyList<string>? stderrLines = null,
        int exitCode = 0,
        bool waitForStdinQuit = true,
        TimeSpan? delay = null)
    {
        var scriptPath = Path.Combine(Root.Path, $"fake-ffmpeg-{Guid.NewGuid():N}.sh");
        FakeFfmpegScript.Write(scriptPath, outputPath, stderrLines, exitCode, waitForStdinQuit, delay);
        MediaEncoder.EncoderPath = scriptPath;
        return scriptPath;
    }

    public void Dispose()
    {
        Manager.Dispose();
        PluginScope.Dispose();
        Root.Dispose();
    }
}
