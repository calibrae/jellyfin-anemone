namespace Jellyfin.Plugin.Anemone.TestKit;

/// <summary>
/// A unique directory under <see cref="Path.GetTempPath"/>, deleted recursively on <see cref="Dispose"/>.
/// Every TestKit fake that needs "somewhere to write files" (transcode output, plugin data folders, ingest
/// targets) should live under one of these rather than a hardcoded or shared path, so tests never touch
/// real Jellyfin data and never collide with each other when run in parallel.
/// </summary>
public sealed class TempDirectory : IDisposable
{
    private bool _disposed;

    private TempDirectory(string path)
    {
        Path = path;
    }

    /// <summary>Gets the absolute path of the directory. Already created on disk.</summary>
    public string Path { get; }

    /// <summary>Creates a new empty directory under the OS temp path.</summary>
    /// <param name="prefix">A short label included in the directory name, for easier debugging of leftovers.</param>
    public static TempDirectory Create(string prefix = "anemone-test")
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return new TempDirectory(path);
    }

    /// <summary>Combines a relative path under this directory, creating parent directories as needed.</summary>
    public string CreateSubdirectory(string relativePath)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>Combines a relative path under this directory without creating anything.</summary>
    public string Combine(string relativePath) => System.IO.Path.Combine(Path, relativePath);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort - a lingering handle (e.g. a fake-ffmpeg child process not yet reaped) shouldn't
            // fail the test run; OS temp cleanup will get it eventually.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
