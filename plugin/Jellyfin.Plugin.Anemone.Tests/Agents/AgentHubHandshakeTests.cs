using System.Net;
using Jellyfin.Plugin.Anemone.Agents;
using Jellyfin.Plugin.Anemone.Agents.Protocol;
using Jellyfin.Plugin.Anemone.TestKit;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Jellyfin.Plugin.Anemone.Tests.Agents;

public class AgentHubHandshakeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static AgentHub MakeHub(string ingestBase = "http://10.240.0.1:8096", string appVersion = "10.11.0", Version? ffmpegVersion = null)
    {
        var appHost = Substitute.For<IServerApplicationHost>();
        appHost.ApplicationVersionString.Returns(appVersion);
        appHost.GetApiUrlForLocalAccess(Arg.Any<IPAddress>(), Arg.Any<bool>()).Returns(ingestBase);

        var mediaEncoder = Substitute.For<IMediaEncoder>();
        mediaEncoder.EncoderVersion.Returns(ffmpegVersion ?? new Version(7, 1, 2));

        return new AgentHub(appHost, mediaEncoder, NullLoggerFactory.Instance, NullLogger<AgentHub>.Instance);
    }

    [Fact]
    public async Task RunConnectionAsync_HandshakesHelloAndWelcome()
    {
        var hub = MakeHub();
        var socket = new FakeAgentWebSocket();

        var hello = new HelloFrame(
            "trish",
            "0.1.0",
            "macos-arm64",
            new FfmpegInfoFrame("/opt/anemone/ffmpeg", "7.1.2-Jellyfin", ["videotoolbox"], ["h264_videotoolbox"], ["h264"], ["scale_vt"]),
            [new AgentMountFrame("/Volumes/data", true)],
            3);
        socket.EnqueueIncoming(Frame.Serialize(hello));

        using var cts = new CancellationTokenSource();
        var runTask = hub.RunConnectionAsync(socket, IPAddress.Loopback, IPAddress.Parse("10.10.0.2"), cts.Token);

        using var readCts = new CancellationTokenSource(Timeout);
        var welcomeJson = await socket.Outgoing.ReadAsync(readCts.Token);
        var welcome = Assert.IsType<WelcomeFrame>(Frame.Parse(welcomeJson));

        Assert.Equal("10.11.0", welcome.Server.Version);
        Assert.Equal(new Version(7, 1, 2).ToString(), welcome.Server.FfmpegVersion);
        Assert.Equal("http://10.240.0.1:8096", welcome.IngestBase);
        Assert.Equal(10, welcome.PingIntervalS);

        // The agent should now be registered in the hub, past the handshake.
        await WaitUntilAsync(() => hub.Agents.Count == 1);
        var agent = Assert.Single(hub.Agents);
        Assert.Equal("trish", agent.Info.Name);
        Assert.Equal(3, agent.Info.MaxSessions);
        Assert.True(agent.IsConnected);

        // hello omitted hwaccel - the hub must infer it (macos platform -> videotoolbox, PROTOCOL.md).
        Assert.Equal("videotoolbox", agent.Info.Hwaccel);
        Assert.Null(agent.Info.HwaccelDevice);
        Assert.Equal("/Volumes/data", agent.Info.Mounts[0].EffectiveServerPath);

        cts.Cancel();
        await Task.WhenAny(runTask, Task.Delay(Timeout));
    }

    [Fact]
    public async Task RunConnectionAsync_UsesAnnouncedHwaccelAndMountServerPath_WhenPresent()
    {
        var hub = MakeHub();
        var socket = new FakeAgentWebSocket();

        var hello = new HelloFrame(
            "linux-box",
            "0.2.0",
            "linux-x86_64",
            new FfmpegInfoFrame("/opt/anemone/ffmpeg", "7.1.2-Jellyfin", ["vaapi"], ["h264_vaapi", "aac"], ["h264"], ["scale_vaapi"]),
            [new AgentMountFrame("/mnt/media", true, "/Volumes/data")],
            2,
            "vaapi",
            "/dev/dri/renderD128");
        socket.EnqueueIncoming(Frame.Serialize(hello));

        using var cts = new CancellationTokenSource();
        var runTask = hub.RunConnectionAsync(socket, IPAddress.Loopback, IPAddress.Parse("10.10.0.2"), cts.Token);

        using var readCts = new CancellationTokenSource(Timeout);
        await socket.Outgoing.ReadAsync(readCts.Token);

        await WaitUntilAsync(() => hub.Agents.Count == 1);
        var agent = Assert.Single(hub.Agents);

        Assert.Equal("vaapi", agent.Info.Hwaccel);
        Assert.Equal("/dev/dri/renderD128", agent.Info.HwaccelDevice);
        Assert.Equal("/mnt/media", agent.Info.Mounts[0].Path);
        Assert.Equal("/Volumes/data", agent.Info.Mounts[0].EffectiveServerPath);

        cts.Cancel();
        await Task.WhenAny(runTask, Task.Delay(Timeout));
    }

    [Fact]
    public async Task RunConnectionAsync_InfersVaapi_WhenNonMacosPlatformReportsVaapiHwaccel()
    {
        var hub = MakeHub();
        var socket = new FakeAgentWebSocket();

        var hello = new HelloFrame(
            "linux-box-2",
            "0.2.0",
            "linux-x86_64",
            new FfmpegInfoFrame("/opt/anemone/ffmpeg", "7.1.2-Jellyfin", ["vaapi"], ["h264_vaapi"], ["h264"], ["scale_vaapi"]),
            [new AgentMountFrame("/Volumes/data", true)],
            2); // hwaccel omitted - must infer from platform + ffmpeg.hwaccels
        socket.EnqueueIncoming(Frame.Serialize(hello));

        using var cts = new CancellationTokenSource();
        var runTask = hub.RunConnectionAsync(socket, IPAddress.Loopback, IPAddress.Parse("10.10.0.2"), cts.Token);

        using var readCts = new CancellationTokenSource(Timeout);
        await socket.Outgoing.ReadAsync(readCts.Token);

        await WaitUntilAsync(() => hub.Agents.Count == 1);
        var agent = Assert.Single(hub.Agents);

        Assert.Equal("vaapi", agent.Info.Hwaccel);

        cts.Cancel();
        await Task.WhenAny(runTask, Task.Delay(Timeout));
    }

    [Fact]
    public async Task RunConnectionAsync_RejectsNonHelloFirstFrame()
    {
        var hub = MakeHub();
        var socket = new FakeAgentWebSocket();
        socket.EnqueueIncoming(Frame.Serialize(new PingFrame())); // not a hello

        await hub.RunConnectionAsync(socket, IPAddress.Loopback, IPAddress.Parse("10.10.0.2"), CancellationToken.None).WaitAsync(Timeout);

        Assert.Empty(hub.Agents);
    }

    [Fact]
    public async Task RunConnectionAsync_RejectsHelloMissingName()
    {
        var hub = MakeHub();
        var socket = new FakeAgentWebSocket();
        var hello = new HelloFrame(
            string.Empty,
            "0.1.0",
            "macos-arm64",
            new FfmpegInfoFrame("/opt/anemone/ffmpeg", "7.1.2-Jellyfin"),
            null,
            3);
        socket.EnqueueIncoming(Frame.Serialize(hello));

        await hub.RunConnectionAsync(socket, IPAddress.Loopback, IPAddress.Parse("10.10.0.2"), CancellationToken.None).WaitAsync(Timeout);

        Assert.Empty(hub.Agents);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition was not met in time");
            }

            await Task.Delay(10);
        }
    }
}
