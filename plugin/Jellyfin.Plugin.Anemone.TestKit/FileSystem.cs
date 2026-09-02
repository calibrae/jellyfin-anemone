using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.Anemone.TestKit;

/// <summary>
/// <see cref="IFileSystem"/> backed by real <see cref="System.IO"/> calls rather than an in-memory model -
/// <see cref="AnemoneTranscodeManager"/> uses it to delete real partial-segment files it also created for
/// real (via the real ffmpeg-stand-in process and the real ingest handler in integration tests), so
/// faithfully delegating is both simpler and more correct than reimplementing filesystem semantics.
/// Every test should point this at a <see cref="TempDirectory"/>-backed path; nothing here restricts
/// writes to one, so misuse would touch the real filesystem - that's the caller's responsibility.
/// </summary>
public sealed class RealFileSystem : IFileSystem
{
    public bool AreEqual(string path1, string path2) =>
        string.Equals(Path.GetFullPath(path1), Path.GetFullPath(path2), StringComparison.OrdinalIgnoreCase);

    public bool ContainsSubPath(string parentPath, string path) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(parentPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public void CreateShortcut(string shortcutPath, string target) =>
        throw new NotSupportedException("anemone-testkit: RealFileSystem does not implement shortcuts");

    public void DeleteFile(string path) => File.Delete(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool FileExists(string path) => File.Exists(path);

    public DateTime GetCreationTimeUtc(FileSystemMetadata info) => info.CreationTimeUtc;

    public DateTime GetCreationTimeUtc(string path) => File.GetCreationTimeUtc(path);

    public IEnumerable<FileSystemMetadata> GetDirectories(string path, bool recursive = false) =>
        Directory.EnumerateDirectories(path, "*", RecursionOption(recursive)).Select(ToMetadata);

    public FileSystemMetadata GetDirectoryInfo(string path) => ToMetadata(path);

    public IEnumerable<string> GetDirectoryPaths(string path, bool recursive = false) =>
        Directory.EnumerateDirectories(path, "*", RecursionOption(recursive));

    public IEnumerable<FileSystemMetadata> GetDrives() => [];

    public FileSystemMetadata GetFileInfo(string path) => ToMetadata(path);

    public string GetFileNameWithoutExtension(FileSystemMetadata info) => Path.GetFileNameWithoutExtension(info.FullName);

    public IEnumerable<string> GetFilePaths(string path, bool recursive = false) =>
        Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", RecursionOption(recursive)) : [];

    public IEnumerable<string> GetFilePaths(string path, string[] extensions, bool enableCaseSensitiveExtensions, bool recursive)
    {
        if (!Directory.Exists(path))
        {
            return [];
        }

        var comparison = enableCaseSensitiveExtensions ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return Directory.EnumerateFiles(path, "*", RecursionOption(recursive))
            .Where(f => extensions.Length == 0 || extensions.Any(e => Path.GetExtension(f).Equals(e, comparison)));
    }

    public IEnumerable<FileSystemMetadata> GetFiles(string path, bool recursive = false) => GetFilePaths(path, recursive).Select(ToMetadata);

    public IEnumerable<FileSystemMetadata> GetFiles(string path, string extension, bool recursive) =>
        GetFilePaths(path, [extension], enableCaseSensitiveExtensions: false, recursive).Select(ToMetadata);

    public IEnumerable<FileSystemMetadata> GetFiles(string path, IReadOnlyList<string>? extensions, bool enableCaseSensitiveExtensions, bool recursive) =>
        GetFilePaths(path, extensions?.ToArray() ?? [], enableCaseSensitiveExtensions, recursive).Select(ToMetadata);

    public IEnumerable<FileSystemMetadata> GetFiles(string path, string extension, IReadOnlyList<string>? extensions, bool enableCaseSensitiveExtensions, bool recursive) =>
        GetFiles(path, extensions, enableCaseSensitiveExtensions, recursive);

    public IEnumerable<FileSystemMetadata> GetFileSystemEntries(string path, bool recursive = false) =>
        GetFiles(path, recursive).Concat(GetDirectories(path, recursive));

    public IEnumerable<string> GetFileSystemEntryPaths(string path, bool recursive = false) =>
        GetFilePaths(path, recursive).Concat(GetDirectoryPaths(path, recursive));

    public FileSystemMetadata GetFileSystemInfo(string path) => ToMetadata(path);

    public DateTime GetLastWriteTimeUtc(FileSystemMetadata info) => info.LastWriteTimeUtc;

    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

    public string GetValidFilename(string filename)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(filename.Where(c => Array.IndexOf(invalid, c) < 0).ToArray());
    }

    public bool IsPathFile(string path) => !path.Contains("://", StringComparison.Ordinal);

    public bool IsShortcut(string filename) => false;

    public string MakeAbsolutePath(string folderPath, string filePath) =>
        Path.IsPathRooted(filePath) ? filePath : Path.GetFullPath(Path.Combine(folderPath, filePath));

    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);

    public string ResolveShortcut(string shortcutPath) =>
        throw new NotSupportedException("anemone-testkit: RealFileSystem does not implement shortcuts");

    public void SetAttributes(string path, bool isReadOnly, bool isHidden)
    {
        // Not exercised by anything under test; real attribute manipulation isn't needed.
    }

    public void SetHidden(string path, bool isHidden)
    {
    }

    public void SwapFiles(string file1, string file2)
    {
        var temp = file1 + ".anemone-testkit-swap";
        File.Move(file1, temp);
        File.Move(file2, file1);
        File.Move(temp, file2);
    }

    private static SearchOption RecursionOption(bool recursive) => recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

    private static FileSystemMetadata ToMetadata(string path)
    {
        var isDirectory = Directory.Exists(path);
        var exists = isDirectory || File.Exists(path);
        return new FileSystemMetadata
        {
            FullName = path,
            Name = Path.GetFileName(path),
            Extension = Path.GetExtension(path),
            Exists = exists,
            IsDirectory = isDirectory,
            Length = exists && !isDirectory ? new FileInfo(path).Length : 0,
            CreationTimeUtc = exists ? File.GetCreationTimeUtc(path) : default,
            LastWriteTimeUtc = exists ? File.GetLastWriteTimeUtc(path) : default,
        };
    }
}
