using System.Net.WebSockets;
using Jellyfin.Plugin.Anemone.Agents.Protocol;
using Jellyfin.Plugin.Anemone.Contracts;
using Jellyfin.Plugin.Anemone.TestKit;

namespace Jellyfin.Plugin.Anemone.IntegrationTests;

/// <summary>
/// Drives two scripted agents through the real <see cref="Agents.AgentHub"/> (real WebSocket handshake,
/// real <c>hello</c>/<c>status</c>/<c>stderr</c> frames) and asserts which one a job would land on - the
/// end-to-end version of what <see cref="Jellyfin.Plugin.Anemone.Tests.Agents.AgentRankerTests"/> and
/// <see cref="Jellyfin.Plugin.Anemone.Tests.Agents.AgentHubPickTests"/> already cover as pure functions.
/// </summary>
public class AgentRankingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static async Task<ClientWebSocket> ConnectAndHelloAsync(AnemoneIntegrationHarness harness, HelloFrame hello)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + harness.Configuration.SharedSecret);
        using (var connectCts = new CancellationTokenSource(Timeout))
        {
            await socket.ConnectAsync(new Uri(harness.WebSocketUrl), connectCts.Token).ConfigureAwait(false);
        }

        await HandshakeTests.SendAsync(socket, Frame.Serialize(hello)).ConfigureAwait(false);
        Assert.IsType<WelcomeFrame>(Frame.Parse(await HandshakeTests.ReceiveAsync(socket).ConfigureAwait(false)));

        await Waiting.UntilAsync(() => harness.Hub.Agents.Any(a => a.Info.Name == hello.Name)).ConfigureAwait(false);
        return socket;
    }

    [Fact]
    public async Task Candidates_FavorsAMuchFasterRemoteAgent_OverAModestlyBetterLocalOne()
    {
        await using var harness = await AnemoneIntegrationHarness.StartAsync();

        // Same job input on both sides: the server calls it /Volumes/data/show/ep.mkv either way.
        const string InputPath = "/Volumes/data/show/ep.mkv";
        var requirements = new JobRequirements([], [], [], [], [InputPath]);

        // local-slow: media is on its own disk (local: true), never measured (assumed baseline 1.0x).
        // WithMounts REPLACES the builder's default single mount - WithMount would instead ADD to it,
        // leaving two overlapping mounts on "/Volumes/data" (the anonymous default plus this one) and
        // making FindLongestMatch's tie-break (first-registered wins a length tie) pick the wrong one.
        var localSlow = new HelloFrameBuilder()
            .WithName("local-slow")
            .WithMounts(new AgentMountFrame("/Volumes/data", true, Local: true))
            .WithMaxSessions(3)
            .Build();

        // fast-remote: the SAME tree over the network (local: false), but measured much faster than
        // real-time once its stderr starts flowing back.
        var fastRemote = new HelloFrameBuilder()
            .WithName("fast-remote")
            .WithMounts(new AgentMountFrame("/mnt/media", true, "/Volumes/data", false))
            .WithMaxSessions(3)
            .Build();

        using var localSocket = await ConnectAndHelloAsync(harness, localSlow);
        using var remoteSocket = await ConnectAndHelloAsync(harness, fastRemote);

        // Before fast-remote has proven itself, its remote-mount penalty dominates: local-slow wins.
        var beforeMeasurement = harness.Hub.Candidates(requirements);
        Assert.Equal("local-slow", beforeMeasurement[0].Info.Name);

        // Real ffmpeg progress lines, exactly as they'd arrive during a real job - no job needs to
        // actually be in flight, matching PROTOCOL.md's "server measures throughput itself from data it
        // already receives" (it doesn't ask; it just watches stderr).
        await HandshakeTests.SendAsync(
            remoteSocket,
            Frame.Serialize(new StderrFrame("probe-job", "frame=100 fps=125 q=-1.0 size=2048KiB time=00:00:04.00 bitrate=4194kbits/s speed=5.0x")));

        var remoteAgent = harness.Hub.Agents.Single(a => a.Info.Name == "fast-remote");
        await Waiting.UntilAsync(() => remoteAgent.MeasuredSpeed is >= 5.0, because: "the stderr speed=5.0x line should have been parsed and folded into the rolling average by now");

        // Now the measured throughput gap outweighs the locality edge: fast-remote wins.
        var afterMeasurement = harness.Hub.Candidates(requirements);
        Assert.Equal("fast-remote", afterMeasurement[0].Info.Name);
        Assert.Equal("local-slow", afterMeasurement[1].Info.Name);

        await HandshakeTests.CloseQuietlyAsync(localSocket);
        await HandshakeTests.CloseQuietlyAsync(remoteSocket);
    }
}
