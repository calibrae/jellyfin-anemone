using System.Diagnostics;
using Jellyfin.Plugin.Anemone.Contracts;
using Jellyfin.Plugin.Anemone.TestKit;
using Jellyfin.Plugin.Anemone.Transcoding;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.Anemone.IntegrationTests;

/// <summary>
/// The real thing, crossing both stacks: a real <c>polyp</c> binary connects to the real
/// <see cref="Agents.AnemoneListener"/>, and real ffmpeg on "the agent" (still this machine, just a
/// separate process) PUTs real HLS segments back into the real ingest endpoint, driven by the real
/// <see cref="AnemoneTranscodeManager"/>. Opt-in: tagged <c>[Trait("Category", "EndToEnd")]</c> so CI can
/// select it explicitly, and every test here uses <c>[SkippableFact]</c> to skip (not fail) when the
/// polyp binary or ffmpeg isn't available, so the rest of the suite stays green on a machine with neither.
/// </summary>
[Trait("Category", "EndToEnd")]
public class EndToEndTests
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Walks up from the test assembly's own location looking for the repo root (a directory containing
    /// both <c>agent/</c> and <c>plugin/</c>), then returns <c>agent/target/release/polyp</c> if it
    /// exists. Deliberately not <c>agent/target/debug/...</c> - DEPLOY.md/the task both call for the real
    /// release build via <c>cargo build --release</c>.
    /// </summary>
    private static string? LocatePolyp()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "agent")) && Directory.Exists(Path.Combine(dir.FullName, "plugin")))
            {
                var candidate = Path.Combine(dir.FullName, "agent", "target", "release", "polyp");
                return File.Exists(candidate) ? candidate : null;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>Any ffmpeg on PATH is enough for this test (it only needs -f lavfi testsrc + libx264/aac, not jellyfin-ffmpeg's hardware encoders).</summary>
    private static string? LocateFfmpeg()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, "ffmpeg");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static (AnemoneTranscodeManager Manager, FakeJobRouter Router, FakeMediaSourceManager MediaSourceManager) BuildManager(AnemoneIntegrationHarness harness)
    {
        var loggerFactory = new FakeLoggerFactory();
        var appPaths = new FakeApplicationPaths(harness.Root);
        var mediaEncoder = new FakeMediaEncoder { EncoderPath = "/opt/anemone/ffmpeg-placeholder-not-runnable", EncoderVersion = harness.MediaEncoder.EncoderVersion };
        var configManager = new FakeServerConfigurationManager(new FakeServerApplicationPaths(appPaths), appPaths);
        var encodingHelper = EncodingHelperFactory.Create(appPaths, mediaEncoder, configManager);
        var router = new FakeJobRouter();
        var mediaSourceManager = new FakeMediaSourceManager();

        var manager = new AnemoneTranscodeManager(
            loggerFactory,
            new RealFileSystem(),
            appPaths,
            configManager,
            new FakeUserManager(),
            new FakeSessionManager(),
            encodingHelper,
            mediaEncoder,
            mediaSourceManager,
            new FakeAttachmentExtractor(),
            router,
            harness.TokenStore);

        return (manager, router, mediaSourceManager);
    }

    private static Process StartPolyp(AnemoneIntegrationHarness harness, string polypPath, string ffmpegPath, string agentName, string configPath)
    {
        File.WriteAllText(
            configPath,
            $"""
             server_url = "{harness.WebSocketUrl}"
             secret = "{harness.Configuration.SharedSecret}"
             name = "{agentName}"
             ffmpeg = "{ffmpegPath}"
             max_sessions = 2
             log_level = "info"
             """);

        var psi = new ProcessStartInfo
        {
            FileName = polypPath,
            ArgumentList = { "--config", configPath },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        // polyp logs via tracing to stderr - drain both streams so the child never blocks on a full pipe
        // buffer. Nothing needs the content in the passing case; a failure's Assert message is what a
        // reader actually needs, so there's nothing captured here on purpose.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task StopPolypAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        catch
        {
            // best effort teardown
        }
        finally
        {
            process.Dispose();
        }
    }

    [SkippableFact]
    public async Task RealPolypAndFfmpeg_TranscodesTestSourceToRealSegmentsAndExitsZero()
    {
        var polypPath = LocatePolyp();
        Skip.If(polypPath is null, "polyp binary not found - build it first: cd agent && cargo build --release --bin polyp");
        var ffmpegPath = LocateFfmpeg();
        Skip.If(ffmpegPath is null, "no ffmpeg found on PATH - install ffmpeg to run EndToEnd tests");

        await using var harness = await AnemoneIntegrationHarness.StartAsync();
        var configPath = harness.Root.Combine("polyp-complete.toml");
        using var polyp = StartPolyp(harness, polypPath!, ffmpegPath!, "e2e-complete", configPath);

        try
        {
            await Waiting.UntilAsync(
                () => harness.Hub.Agents.Any(a => a.Info.Name == "e2e-complete"),
                StartupTimeout,
                because: "real polyp should have connected and sent hello");
            var agentConnection = harness.Hub.Agents.Single(a => a.Info.Name == "e2e-complete");

            var (manager, router, mediaSourceManager) = BuildManager(harness);

            var targetDir = harness.Root.CreateSubdirectory("e2e-complete");
            var jobId = Guid.NewGuid().ToString("N");
            const string Prefix = "e2e";
            var outputPath = Path.Combine(targetDir, Prefix + ".m3u8");
            var token = harness.TokenStore.Issue(jobId, targetDir, Prefix);

            // A short, finite real source: -f lavfi testsrc, real libx264+aac, real HLS muxing - exactly
            // the shape of ffmpeg command line Jellyfin itself would build, just not captured from a live
            // TryPlan (JobRouterTests/RoutePlannerTests already cover that argv-rewriting logic).
            var rawArgv = new[]
            {
                // -re (read input at native framerate) matters for realism (Jellyfin's own remote-input
                // jobs are never faster than real time either) and -g/-keyint_min matter for correctness:
                // libx264's default keyframe interval (250 frames) is far longer than this whole clip, so
                // without it the HLS muxer never finds a keyframe to cut a segment boundary on and writes
                // the entire source as one giant segment regardless of -hls_time.
                "-re", "-f", "lavfi", "-i", "testsrc=duration=4:size=320x240:rate=10",
                "-re", "-f", "lavfi", "-i", "sine=frequency=440:duration=4",
                "-c:v", "libx264", "-preset", "veryfast", "-g", "10", "-keyint_min", "10", "-c:a", "aac",
                "-f", "hls", "-hls_time", "1", "-hls_list_size", "0",
                "-hls_segment_filename", Path.Combine(targetDir, Prefix + "%d.ts"),
                outputPath,
            };
            var rewrittenArgv = RoutePlanner.Rewrite(rawArgv, agentConnection.IngestBase, jobId, token);
            var spec = new RemoteJobSpec(jobId, rewrittenArgv, token, "e2e complete job");
            router.PlanToReturn = new RoutePlan(agentConnection, spec, targetDir, Prefix, "e2e complete plan");

            var state = new StreamStateBuilder().WithMediaSourceManager(mediaSourceManager).WithTranscodeManager(manager).Build();
            using var cts = new CancellationTokenSource();

            var job = await manager.StartFfMpeg(state, outputPath, "raw-argv-unused-for-remote", Guid.Empty, TranscodingJobType.Hls, cts)
                .WaitAsync(StartupTimeout);

            Assert.Equal(jobId, job.Id);
            Assert.True(File.Exists(outputPath), "anemone-e2e: the real polyp/ffmpeg should have PUT a playlist that satisfied StartFfMpeg's wait");
            Assert.NotEmpty(Directory.GetFiles(targetDir, Prefix + "*.ts"));
            Assert.Empty(Directory.GetFiles(targetDir, "*.part"));

            // The 4s test source finishes on its own; give real ffmpeg real wall-clock time to encode and
            // real polyp time to report the exit frame.
            await Waiting.UntilAsync(() => job.HasExited, TimeSpan.FromSeconds(30), because: "the finite test source should make ffmpeg exit on its own");
            Assert.Equal(0, job.ExitCode);
        }
        finally
        {
            await StopPolypAsync(polyp);
        }
    }

    [SkippableFact]
    public async Task RealPolypAndFfmpeg_QuitStopsItEarly()
    {
        var polypPath = LocatePolyp();
        Skip.If(polypPath is null, "polyp binary not found - build it first: cd agent && cargo build --release --bin polyp");
        var ffmpegPath = LocateFfmpeg();
        Skip.If(ffmpegPath is null, "no ffmpeg found on PATH - install ffmpeg to run EndToEnd tests");

        await using var harness = await AnemoneIntegrationHarness.StartAsync();
        var configPath = harness.Root.Combine("polyp-stop.toml");
        using var polyp = StartPolyp(harness, polypPath!, ffmpegPath!, "e2e-stop", configPath);

        try
        {
            await Waiting.UntilAsync(
                () => harness.Hub.Agents.Any(a => a.Info.Name == "e2e-stop"),
                StartupTimeout,
                because: "real polyp should have connected and sent hello");
            var agentConnection = harness.Hub.Agents.Single(a => a.Info.Name == "e2e-stop");

            var (manager, router, mediaSourceManager) = BuildManager(harness);

            var targetDir = harness.Root.CreateSubdirectory("e2e-stop");
            var jobId = Guid.NewGuid().ToString("N");
            const string Prefix = "e2estop";
            var outputPath = Path.Combine(targetDir, Prefix + ".m3u8");
            var token = harness.TokenStore.Issue(jobId, targetDir, Prefix);

            // Long enough (60s) that it would never finish on its own within this test's timeout - only
            // the "q" stop below should end it.
            var rawArgv = new[]
            {
                "-re", "-f", "lavfi", "-i", "testsrc=duration=60:size=320x240:rate=10",
                "-c:v", "libx264", "-preset", "veryfast", "-g", "10", "-keyint_min", "10",
                "-f", "hls", "-hls_time", "1", "-hls_list_size", "0",
                "-hls_segment_filename", Path.Combine(targetDir, Prefix + "%d.ts"),
                outputPath,
            };
            var rewrittenArgv = RoutePlanner.Rewrite(rawArgv, agentConnection.IngestBase, jobId, token);
            var spec = new RemoteJobSpec(jobId, rewrittenArgv, token, "e2e stop job");
            router.PlanToReturn = new RoutePlan(agentConnection, spec, targetDir, Prefix, "e2e stop plan");

            var state = new StreamStateBuilder().WithMediaSourceManager(mediaSourceManager).WithTranscodeManager(manager).Build();
            using var cts = new CancellationTokenSource();

            var job = await manager.StartFfMpeg(state, outputPath, "raw-argv-unused-for-remote", Guid.Empty, TranscodingJobType.Hls, cts)
                .WaitAsync(StartupTimeout);

            Assert.False(job.HasExited);

            var sw = Stopwatch.StartNew();
            await manager.KillTranscodingJobs(state.Request.DeviceId, state.Request.PlaySessionId, _ => false).WaitAsync(TimeSpan.FromSeconds(15));
            sw.Stop();

            Assert.True(job.HasExited);

            // Well under the 60s source duration and under StopRemoteJobAsync's own 5s "q then kill" grace
            // period plus real process/network overhead.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"anemone-e2e: stopping took {sw.Elapsed} - expected a prompt quit, not the 5s kill grace period or a hang");
        }
        finally
        {
            await StopPolypAsync(polyp);
        }
    }
}
