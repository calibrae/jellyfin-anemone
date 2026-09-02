using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Plugin.Anemone.TestKit;

/// <summary>
/// Finds an OS-assigned free TCP port for a test to bind a real listener to, instead of a hardcoded
/// port (the production server may already be running on this machine - see DEPLOY.md/PROTOCOL.md's own
/// warnings about port 8097). <see cref="Agents.AnemoneListener"/>'s own <c>AgentListenPort</c> config
/// treats 0 as "disabled" rather than "pick an ephemeral port" (it's a deliberate off-switch for real
/// deployments), so it can't be handed 0 directly - this pre-allocates a real port instead, the standard
/// (if very slightly TOCTOU-racy) way to get one before a component that itself takes a fixed port number
/// to listen on.
/// </summary>
public static class FreePort
{
    public static int Find()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
