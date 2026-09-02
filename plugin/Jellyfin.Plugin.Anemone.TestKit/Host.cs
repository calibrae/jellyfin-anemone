using System.Net;
using MediaBrowser.Common;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Anemone.TestKit;

/// <summary>
/// <see cref="IServerApplicationHost"/> fake. <see cref="JobRouter"/>/<see cref="AgentHub"/> only ever
/// call <see cref="GetApiUrlForLocalAccess"/> (and read <see cref="ApplicationVersionString"/>) - every
/// call to it is recorded in <see cref="LastGetApiUrlForLocalAccessCall"/> so a test can assert on the
/// address the hub resolved an agent's ingest base from.
/// </summary>
public sealed class FakeServerApplicationHost : IServerApplicationHost
{
    public string? UrlToReturn { get; set; } = "http://10.10.0.1:8096";

    public (IPAddress? IpAddress, bool AllowHttps)? LastGetApiUrlForLocalAccessCall { get; private set; }

    public bool CoreStartupHasCompleted => true;

    public int HttpPort => 8096;

    public int HttpsPort => 8920;

    public bool ListenWithHttps => false;

    public string FriendlyName => "anemone-testkit-server";

    public string? RestoreBackupPath { get; set; }

    public string Name => "Jellyfin Server";

    public string SystemId => "anemone-testkit-system";

    public bool HasPendingRestart => false;

    public bool ShouldRestart { get; set; }

    public Version ApplicationVersion { get; set; } = new(10, 11, 0);

    public IServiceProvider? ServiceProvider { get; set; }

    public string ApplicationVersionString { get; set; } = "10.11.0";

    public string ApplicationUserAgent => "Jellyfin/10.11.0";

    public string ApplicationUserAgentAddress => "https://jellyfin.org";

#pragma warning disable CS0067 // required by IApplicationHost, never raised by this fake
    public event EventHandler? HasPendingRestartChanged;
#pragma warning restore CS0067

    public string GetSmartApiUrl(HttpRequest request) => throw new NotSupportedException("anemone-testkit: FakeServerApplicationHost does not implement GetSmartApiUrl");

    public string GetSmartApiUrl(IPAddress remoteAddr) => throw new NotSupportedException("anemone-testkit: FakeServerApplicationHost does not implement GetSmartApiUrl");

    public string GetSmartApiUrl(string hostname) => throw new NotSupportedException("anemone-testkit: FakeServerApplicationHost does not implement GetSmartApiUrl");

    public string GetApiUrlForLocalAccess(IPAddress? ipAddress = null, bool allowHttps = true)
    {
        LastGetApiUrlForLocalAccessCall = (ipAddress, allowHttps);
        return UrlToReturn ?? throw new InvalidOperationException("anemone-testkit: set UrlToReturn before calling GetApiUrlForLocalAccess");
    }

    public string GetLocalApiUrl(string hostname, string? scheme = null, int? port = null) =>
        throw new NotSupportedException("anemone-testkit: FakeServerApplicationHost does not implement GetLocalApiUrl");

    public string ExpandVirtualPath(string path) => path;

    public string ReverseVirtualPath(string path) => path;

    public IEnumerable<System.Reflection.Assembly> GetApiPluginAssemblies() => [];

    public void NotifyPendingRestart()
    {
    }

    public IReadOnlyCollection<T> GetExports<T>(bool manageLifetime = true) => [];

    public IReadOnlyCollection<T> GetExports<T>(CreationDelegateFactory defaultFunc, bool manageLifetime = true) => [];

    public IEnumerable<Type> GetExportTypes<T>() => [];

    public T Resolve<T>() => throw new NotSupportedException("anemone-testkit: FakeServerApplicationHost does not implement DI resolution");

    public void Init(IServiceCollection serviceCollection)
    {
    }
}

/// <summary>
/// <see cref="MediaBrowser.Model.Serialization.IXmlSerializer"/> no-op fake, needed only to construct a real
/// <see cref="Plugin"/> instance (see <see cref="PluginInstanceScope"/>). Every "deserialize" call returns a
/// fresh default instance of the requested type (via <see cref="Activator.CreateInstance(Type)"/>) rather
/// than throwing or reading anything real: whether or not <c>BasePlugin&lt;T&gt;</c>'s constructor checks
/// for an existing configuration file before calling <see cref="DeserializeFromFile"/> is not something
/// this project can see the IL of, so returning a harmless default either way is safer than guessing.
/// </summary>
public sealed class FakeXmlSerializer : MediaBrowser.Model.Serialization.IXmlSerializer
{
    public object DeserializeFromBytes(Type type, byte[] buffer) => CreateDefault(type);

    public object DeserializeFromFile(Type type, string file) => CreateDefault(type);

    public object DeserializeFromStream(Type type, Stream stream) => CreateDefault(type);

    private static object CreateDefault(Type type) =>
        Activator.CreateInstance(type)
        ?? throw new InvalidOperationException($"anemone-testkit: FakeXmlSerializer could not construct a default '{type}' (no parameterless constructor?)");

    public void SerializeToFile(object obj, string file)
    {
        // No-op: PluginInstanceScope doesn't need the default configuration persisted to disk.
    }

    public void SerializeToStream(object obj, Stream stream)
    {
    }
}
