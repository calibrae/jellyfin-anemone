using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;

namespace Jellyfin.Plugin.Anemone.TestKit;

/// <summary>
/// <see cref="IServerConfigurationManager"/> backed by a real <see cref="ServerConfiguration"/> and a real,
/// mutable <see cref="EncodingOptions"/> - both genuine Jellyfin model POCOs, not further faked, per the
/// task's ask for "a real EncodingOptions/ServerConfiguration". <see cref="EncodingOptions"/> is what
/// <c>IConfigurationManager.GetEncodingOptions()</c> (an extension method that calls
/// <see cref="GetConfiguration"/> and casts) resolves to; <c>GetTranscodePath()</c> (also an extension
/// method) is exercised by <see cref="AnemoneTranscodeManager"/>'s constructor via
/// <c>DeleteEncodedMediaCache</c> and works unmodified against this fake as long as
/// <see cref="ApplicationPaths"/> resolves to real (temp) directories.
/// </summary>
public sealed class FakeServerConfigurationManager : IServerConfigurationManager
{
    private readonly Dictionary<string, object> _namedConfigurations = new(StringComparer.Ordinal);

#pragma warning disable CS0067 // required by IConfigurationManager, never raised by this fake
    public event EventHandler<EventArgs>? ConfigurationUpdated;

    public event EventHandler<ConfigurationUpdateEventArgs>? NamedConfigurationUpdated;

    public event EventHandler<ConfigurationUpdateEventArgs>? NamedConfigurationUpdating;
#pragma warning restore CS0067

    public FakeServerConfigurationManager(IServerApplicationPaths applicationPaths, IApplicationPaths commonApplicationPaths)
    {
        ApplicationPaths = applicationPaths;
        CommonApplicationPaths = commonApplicationPaths;
        EncodingOptions = new EncodingOptions();
        _namedConfigurations["encoding"] = EncodingOptions;
    }

    public IServerApplicationPaths ApplicationPaths { get; }

    public IApplicationPaths CommonApplicationPaths { get; }

    public ServerConfiguration Configuration { get; set; } = new();

    public BaseApplicationConfiguration CommonConfiguration => Configuration;

    /// <summary>
    /// The instance <c>GetConfiguration("encoding")</c> returns, i.e. what
    /// <c>IConfigurationManager.GetEncodingOptions()</c> resolves to. Mutate freely from a test (e.g.
    /// <c>EnableSegmentDeletion</c>, <c>HardwareAccelerationType</c>) - it's the live instance, not a copy.
    /// </summary>
    public EncodingOptions EncodingOptions { get; }

    public object GetConfiguration(string key) => _namedConfigurations.TryGetValue(key, out var value)
        ? value
        : throw new KeyNotFoundException($"anemone-testkit: no fake configuration registered for key '{key}'. Use SetConfiguration to add one.");

    /// <summary>Registers (or replaces) a named configuration object, for extension methods this fake doesn't special-case.</summary>
    public void SetConfiguration(string key, object value) => _namedConfigurations[key] = value;

    public void SaveConfiguration()
    {
    }

    public void SaveConfiguration(string key, object configuration) => _namedConfigurations[key] = configuration;

    public void AddParts(IEnumerable<IConfigurationFactory> factories)
    {
    }

    public ConfigurationStore[] GetConfigurationStores() => [];

    public Type? GetConfigurationType(string key) => _namedConfigurations.TryGetValue(key, out var value) ? value.GetType() : null;

    public void RegisterConfiguration<T>()
        where T : IConfigurationFactory
    {
    }

    public void ReplaceConfiguration(BaseApplicationConfiguration newConfiguration)
    {
        if (newConfiguration is ServerConfiguration serverConfiguration)
        {
            Configuration = serverConfiguration;
        }
    }
}
