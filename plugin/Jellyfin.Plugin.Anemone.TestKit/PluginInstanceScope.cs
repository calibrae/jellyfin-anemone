using System.Reflection;
using Jellyfin.Plugin.Anemone.Configuration;

namespace Jellyfin.Plugin.Anemone.TestKit;

/// <summary>
/// Constructs a real <see cref="Plugin"/> and installs it as <see cref="Plugin.Instance"/> for the
/// lifetime of this scope, restoring whatever was there before on <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Plugin.Instance"/> is how <see cref="AnemoneTranscodeManager"/>, <see cref="JobRouter"/> and
/// <see cref="Agents.AgentHub"/> all read the live <see cref="PluginConfiguration"/> (<c>Enabled</c>,
/// <c>DryRun</c>, <c>AgentStartTimeoutSeconds</c>, ...) - there is no DI seam for it, it's a bare static
/// property with a private setter, set once by the real <see cref="Plugin"/> constructor. Every routing
/// decision in <c>AnemoneTranscodeManager.StartFfMpeg</c> (including whether the router is consulted at
/// all: <c>cfg is { Enabled: true }</c>) falls back to "off"/defaults when <see cref="Plugin.Instance"/> is
/// null, which is why the rest of this test suite never needs to touch it - but that also means it is
/// null when nothing has, so any test that wants routing to actually happen (or wants a short
/// <c>AgentStartTimeoutSeconds</c> instead of the 15s default) needs a real instance installed first.
/// </para>
/// <para>
/// <see cref="Plugin.Instance"/> is process-wide state. Every test that uses this type must run with test
/// parallelization disabled for the assembly (see the <c>CollectionBehavior</c> attribute in
/// <c>AssemblyInfo.cs</c>) - two tests racing to install/restore a static field would be flaky in ways
/// that have nothing to do with the code under test.
/// </para>
/// </remarks>
public sealed class PluginInstanceScope : IDisposable
{
    private static readonly PropertyInfo InstanceProperty =
        typeof(Plugin).GetProperty(nameof(Plugin.Instance), BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException("anemone-testkit: Plugin.Instance property not found - did the plugin rename it?");

    private readonly Plugin? _previous;
    private bool _disposed;

    /// <summary>
    /// Installs a fresh <see cref="Plugin"/> (backed by <paramref name="appPaths"/>, or a throwaway
    /// <see cref="TempDirectory"/>-backed one) as <see cref="Plugin.Instance"/>, then applies
    /// <paramref name="configure"/> to its <see cref="Plugin.Configuration"/>.
    /// </summary>
    public PluginInstanceScope(Action<PluginConfiguration>? configure = null, MediaBrowser.Common.Configuration.IApplicationPaths? appPaths = null)
    {
        _previous = Plugin.Instance;

        var paths = appPaths ?? new FakeApplicationPaths(TempDirectory.Create("anemone-plugin-instance"));
        Plugin = new Plugin(paths, new FakeXmlSerializer());
        configure?.Invoke(Plugin.Configuration);
    }

    /// <summary>The <see cref="Plugin"/> instance now installed as <see cref="Plugin.Instance"/>.</summary>
    public Plugin Plugin { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        InstanceProperty.SetValue(null, _previous);
    }
}
