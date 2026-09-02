using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;

namespace Jellyfin.Plugin.Anemone.TestKit;

/// <summary>
/// <see cref="IApplicationPaths"/> (the base, plugin-facing one - <c>MediaBrowser.Common.Configuration</c>)
/// backed by a <see cref="TestKit.TempDirectory"/>. Every path is a distinct subdirectory under it so
/// nothing a test writes can collide with another test or with real Jellyfin data.
/// </summary>
public sealed class FakeApplicationPaths : IApplicationPaths
{
    public FakeApplicationPaths(TempDirectory root)
    {
        Root = root;
        ProgramDataPath = root.CreateSubdirectory("data");
        WebPath = root.CreateSubdirectory("web");
        ProgramSystemPath = root.CreateSubdirectory("system");
        DataPath = root.CreateSubdirectory("data/library");
        ImageCachePath = root.CreateSubdirectory("data/image-cache");
        PluginsPath = root.CreateSubdirectory("plugins");
        PluginConfigurationsPath = root.CreateSubdirectory("plugins/configurations");
        LogDirectoryPath = root.CreateSubdirectory("log");
        ConfigurationDirectoryPath = root.CreateSubdirectory("config");
        SystemConfigurationFilePath = Path.Combine(ConfigurationDirectoryPath, "system.xml");
        CachePath = root.CreateSubdirectory("cache");
        TempDirectory = root.CreateSubdirectory("temp");
        BackupPath = root.CreateSubdirectory("backups");
        TrickplayPath = root.CreateSubdirectory("data/trickplay");
        VirtualDataPath = root.CreateSubdirectory("data/virtual");
    }

    /// <summary>The backing <see cref="TestKit.TempDirectory"/> - dispose it (or the harness that owns it) to clean up.</summary>
    public TempDirectory Root { get; }

    public string ProgramDataPath { get; }

    public string WebPath { get; }

    public string ProgramSystemPath { get; }

    public string DataPath { get; }

    public string ImageCachePath { get; }

    public string PluginsPath { get; }

    public string PluginConfigurationsPath { get; }

    public string LogDirectoryPath { get; }

    public string ConfigurationDirectoryPath { get; }

    public string SystemConfigurationFilePath { get; }

    public string CachePath { get; }

    public string TempDirectory { get; }

    public string BackupPath { get; }

    public string TrickplayPath { get; }

    public string VirtualDataPath { get; }

    public void CreateAndCheckMarker(string path, string markerName, bool overwrite = false)
    {
        // Real behaviour isn't needed by anything AnemoneTranscodeManager/JobRouter/AgentHub touch.
    }

    public void MakeSanityCheckOrThrow()
    {
    }
}

/// <summary>
/// <see cref="IServerApplicationPaths"/> (<c>MediaBrowser.Controller</c>, extends the common
/// <see cref="IApplicationPaths"/> above) backed by the same <see cref="TestKit.TempDirectory"/>.
/// <see cref="AnemoneTranscodeManager"/> only reads <see cref="LogDirectoryPath"/> (inherited) through
/// this interface; the rest exist to satisfy the type.
/// </summary>
public sealed class FakeServerApplicationPaths : IServerApplicationPaths
{
    private readonly FakeApplicationPaths _common;

    public FakeServerApplicationPaths(FakeApplicationPaths common)
    {
        _common = common;
        RootFolderPath = common.Root.CreateSubdirectory("root");
        DefaultUserViewsPath = common.Root.CreateSubdirectory("default-views");
        InternalMetadataPath = common.Root.CreateSubdirectory("metadata");
        VirtualInternalMetadataPath = InternalMetadataPath;
        DefaultInternalMetadataPath = InternalMetadataPath;
        ArtistsPath = common.Root.CreateSubdirectory("artists");
        GenrePath = common.Root.CreateSubdirectory("genres");
        MusicGenrePath = common.Root.CreateSubdirectory("music-genres");
        StudioPath = common.Root.CreateSubdirectory("studios");
        PeoplePath = common.Root.CreateSubdirectory("people");
        YearPath = common.Root.CreateSubdirectory("years");
        UserConfigurationDirectoryPath = common.Root.CreateSubdirectory("users");
    }

    public string RootFolderPath { get; }

    public string DefaultUserViewsPath { get; }

    public string InternalMetadataPath { get; }

    public string VirtualInternalMetadataPath { get; }

    public string DefaultInternalMetadataPath { get; }

    public string ArtistsPath { get; }

    public string GenrePath { get; }

    public string MusicGenrePath { get; }

    public string StudioPath { get; }

    public string PeoplePath { get; }

    public string YearPath { get; }

    public string UserConfigurationDirectoryPath { get; }

    // -- inherited from IApplicationPaths, forwarded to the shared common instance --
    public string ProgramDataPath => _common.ProgramDataPath;

    public string WebPath => _common.WebPath;

    public string ProgramSystemPath => _common.ProgramSystemPath;

    public string DataPath => _common.DataPath;

    public string ImageCachePath => _common.ImageCachePath;

    public string PluginsPath => _common.PluginsPath;

    public string PluginConfigurationsPath => _common.PluginConfigurationsPath;

    public string LogDirectoryPath => _common.LogDirectoryPath;

    public string ConfigurationDirectoryPath => _common.ConfigurationDirectoryPath;

    public string SystemConfigurationFilePath => _common.SystemConfigurationFilePath;

    public string CachePath => _common.CachePath;

    public string TempDirectory => _common.TempDirectory;

    public string BackupPath => _common.BackupPath;

    public string TrickplayPath => _common.TrickplayPath;

    public string VirtualDataPath => _common.VirtualDataPath;

    public void CreateAndCheckMarker(string path, string markerName, bool overwrite = false) =>
        _common.CreateAndCheckMarker(path, markerName, overwrite);

    public void MakeSanityCheckOrThrow() => _common.MakeSanityCheckOrThrow();
}
