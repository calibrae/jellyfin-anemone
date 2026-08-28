using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Anemone.Ingest;

/// <summary>
/// Validates an ingest upload's filename against a job's playlist prefix (see PROTOCOL.md
/// "Valid filenames"). Never throws on malformed input.
/// </summary>
public static class IngestNames
{
    /// <summary>
    /// True when <paramref name="name"/> is <c>&lt;prefix&gt;-?[0-9]+.(ts|mp4|m4s)</c> or <c>&lt;prefix&gt;.m3u8</c>,
    /// contains no path separators, and contains no <c>..</c>.
    /// </summary>
    public static bool IsValid(string prefix, string name)
    {
        if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (name.Contains('/', StringComparison.Ordinal)
            || name.Contains('\\', StringComparison.Ordinal)
            || name.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        var pattern = $"^{Regex.Escape(prefix)}(-?[0-9]+\\.(ts|mp4|m4s)|\\.m3u8)$";
        return Regex.IsMatch(name, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200));
    }
}
