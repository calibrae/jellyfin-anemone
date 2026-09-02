using System.Net;
using System.Net.WebSockets;
using Jellyfin.Plugin.Anemone.Agents.Protocol;
using Jellyfin.Plugin.Anemone.TestKit;

namespace Jellyfin.Plugin.Anemone.IntegrationTests;

/// <summary>
/// The real hello/welcome handshake end to end: a real <see cref="ClientWebSocket"/> against the real
/// <see cref="Agents.AnemoneListener"/>/<see cref="Agents.AgentWebSocketEndpoint"/>/<see cref="Agents.AgentHub"/>
/// stack on an ephemeral port. See PROTOCOL.md "Control" - the upgrade request itself carries the bearer,
/// and Jellyfin's own websocket middleware is why this listener exists on its own port at all (see
/// PROTOCOL.md "Why both channels live on the plugin's own port").
/// </summary>
public class HandshakeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RealClientWebSocket_HandshakesHelloAndWelcome()
    {
        await using var harness = await AnemoneIntegrationHarness.StartAsync();

        using var client = new ClientWebSocket();
        client.Options.SetRequestHeader("Authorization", "Bearer " + harness.Configuration.SharedSecret);

        using var connectCts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync(new Uri(harness.WebSocketUrl), connectCts.Token);

        var hello = new HelloFrameBuilder().WithName("trish-integration").Build();
        await SendAsync(client, Frame.Serialize(hello));

        var welcomeJson = await ReceiveAsync(client);
        var welcome = Assert.IsType<WelcomeFrame>(Frame.Parse(welcomeJson));

        Assert.Equal("10.11.0", welcome.Server.Version);
        Assert.Equal(harness.MediaEncoder.EncoderVersion.ToString(), welcome.Server.FfmpegVersion);
        Assert.Equal(10, welcome.PingIntervalS);

        // The harness defaults Configuration.IngestBaseUrl to its own real, reachable base URL (see its
        // own remarks - loopback is excluded from AgentHub.ResolveIngestBase's "address the agent
        // actually reached us on" branch, same as a real agent never being on the server's own host), so
        // this is the configured-override branch, not GetApiUrlForLocalAccess. That other branch's
        // address+port composition is covered directly below
        // (ResolveIngestBase_NonLoopbackAddress_UsesItWithTheConfiguredPort).
        Assert.Equal(harness.HttpBaseUrl, welcome.IngestBase);
        Assert.Contains(harness.Port.ToString(), welcome.IngestBase, StringComparison.Ordinal);

        await Waiting.UntilAsync(() => harness.Hub.Agents.Count == 1);
        var agent = Assert.Single(harness.Hub.Agents);
        Assert.Equal("trish-integration", agent.Info.Name);
        Assert.True(agent.IsConnected);

        await CloseQuietlyAsync(client);
    }

    [Fact]
    public async Task ResolveIngestBase_NonLoopbackAddress_UsesItWithTheConfiguredPort()
    {
        // AnemoneIntegrationHarness's own handshake test above can only observe the configured-override
        // branch (see its remarks) - a real non-loopback agent address, with IngestBaseUrl cleared so the
        // "address the agent actually reached us on" branch is what actually runs, is exercised here
        // directly against the real AgentHub instance the listener above is also using, which is the same
        // ResolveIngestBase a real agent handshake calls into.
        await using var harness = await AnemoneIntegrationHarness.StartAsync(cfg => cfg.IngestBaseUrl = string.Empty);

        var resolved = harness.Hub.ResolveIngestBase(IPAddress.Parse("10.240.0.1"));

        Assert.Equal($"http://10.240.0.1:{harness.Port}", resolved);
    }

    [Fact]
    public async Task Connect_MissingBearer_IsRejected()
    {
        await using var harness = await AnemoneIntegrationHarness.StartAsync();

        using var client = new ClientWebSocket();
        client.Options.CollectHttpResponseDetails = true;
        using var connectCts = new CancellationTokenSource(Timeout);

        var ex = await Assert.ThrowsAsync<WebSocketException>(
            () => client.ConnectAsync(new Uri(harness.WebSocketUrl), connectCts.Token));

        Assert.Equal(HttpStatusCode.Unauthorized, client.HttpStatusCode);
        Assert.NotNull(ex);
    }

    [Fact]
    public async Task Connect_BadBearer_IsRejected()
    {
        await using var harness = await AnemoneIntegrationHarness.StartAsync();

        using var client = new ClientWebSocket();
        client.Options.CollectHttpResponseDetails = true;
        client.Options.SetRequestHeader("Authorization", "Bearer wrong-secret");
        using var connectCts = new CancellationTokenSource(Timeout);

        await Assert.ThrowsAsync<WebSocketException>(
            () => client.ConnectAsync(new Uri(harness.WebSocketUrl), connectCts.Token));

        Assert.Equal(HttpStatusCode.Unauthorized, client.HttpStatusCode);
    }

    [Fact]
    public async Task Connect_EmptySharedSecret_Yields503()
    {
        await using var harness = await AnemoneIntegrationHarness.StartAsync(cfg => cfg.SharedSecret = string.Empty);

        using var client = new ClientWebSocket();
        client.Options.CollectHttpResponseDetails = true;
        client.Options.SetRequestHeader("Authorization", "Bearer whatever");
        using var connectCts = new CancellationTokenSource(Timeout);

        await Assert.ThrowsAsync<WebSocketException>(
            () => client.ConnectAsync(new Uri(harness.WebSocketUrl), connectCts.Token));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, client.HttpStatusCode);
    }

    internal static async Task SendAsync(ClientWebSocket client, string json)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        using var cts = new CancellationTokenSource(Timeout);
        await client.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token);
    }

    internal static async Task<string> ReceiveAsync(ClientWebSocket client)
    {
        var buffer = new byte[64 * 1024];
        using var cts = new CancellationTokenSource(Timeout);
        var result = await client.ReceiveAsync(buffer, cts.Token);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    internal static async Task CloseQuietlyAsync(ClientWebSocket client)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
        }
        catch
        {
            // best effort
        }
    }
}
