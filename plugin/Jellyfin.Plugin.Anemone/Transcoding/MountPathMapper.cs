using Jellyfin.Plugin.Anemone.Contracts;

namespace Jellyfin.Plugin.Anemone.Transcoding;

/// <summary>
/// Path-segment-boundary matching and prefix rewriting between a job's server-side input path and an
/// agent's mount table. See PROTOCOL.md "Path mapping". Shared by <see cref="Agents.AgentHub"/> (candidate
/// mount coverage) and <see cref="JobRouter"/> (input-path rewriting), so the boundary rule can't drift
/// between the two call sites.
/// </summary>
public static class MountPathMapper
{
    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="mountServerPath"/> itself or lies under it on a
    /// path-segment boundary - <c>/Volumes/data</c> covers <c>/Volumes/data/x.mkv</c> but never
    /// <c>/Volumes/database/x.mkv</c>.
    /// </summary>
    public static bool IsUnderMount(string path, string mountServerPath)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(mountServerPath);

        var mount = mountServerPath.TrimEnd('/', '\\');
        if (mount.Length == 0 || !path.StartsWith(mount, StringComparison.Ordinal))
        {
            return false;
        }

        if (path.Length == mount.Length)
        {
            return true;
        }

        var next = path[mount.Length];
        return next is '/' or '\\';
    }

    /// <summary>
    /// The <c>ok</c> mount whose <see cref="AgentMount.EffectiveServerPath"/> covers <paramref name="path"/>,
    /// preferring the longest matching <c>server_path</c> when several overlap (e.g. <c>/Volumes/data</c> and
    /// <c>/Volumes/data/4k</c> both covering a path under <c>/Volumes/data/4k</c>). Null when none cover it.
    /// </summary>
    public static AgentMount? FindLongestMatch(IReadOnlyList<AgentMount> mounts, string path)
    {
        ArgumentNullException.ThrowIfNull(mounts);
        ArgumentNullException.ThrowIfNull(path);

        AgentMount? best = null;
        foreach (var mount in mounts)
        {
            if (!mount.Ok || !IsUnderMount(path, mount.EffectiveServerPath))
            {
                continue;
            }

            if (best is null || mount.EffectiveServerPath.Length > best.EffectiveServerPath.Length)
            {
                best = mount;
            }
        }

        return best;
    }

    /// <summary>
    /// Rewrites every <c>-i</c> argument's path prefix from the matching mount's
    /// <see cref="AgentMount.EffectiveServerPath"/> to its <see cref="AgentMount.Path"/> (where the agent
    /// actually has the tree mounted), preserving an <c>file:</c> prefix exactly as it appeared. Only inputs
    /// are touched; every other token is copied byte-for-byte. Fails (no mount covers some input) rather
    /// than guessing.
    /// </summary>
    /// <param name="argv">The ffmpeg argv (already through <see cref="RoutePlanner.Analyze"/>'s routability check).</param>
    /// <param name="mounts">The candidate agent's announced mounts.</param>
    /// <param name="mapped">The rewritten argv on success; the original <paramref name="argv"/> on failure.</param>
    /// <param name="reason">Why no mount covered an input, when this returns false.</param>
    /// <returns>False when some <c>-i</c> input isn't covered by any <c>ok</c> mount.</returns>
    public static bool TryMapInputPaths(
        IReadOnlyList<string> argv,
        IReadOnlyList<AgentMount> mounts,
        out IReadOnlyList<string> mapped,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(mounts);

        var result = new List<string>(argv);

        for (var i = 0; i < result.Count - 1; i++)
        {
            if (result[i] != "-i")
            {
                continue;
            }

            var raw = result[i + 1];
            var hasFilePrefix = raw.StartsWith("file:", StringComparison.Ordinal);
            var path = hasFilePrefix ? raw["file:".Length..] : raw;

            var match = FindLongestMatch(mounts, path);
            if (match is null)
            {
                mapped = argv;
                reason = $"no mount covers input path '{path}'";
                return false;
            }

            var rewrittenPath = RewritePrefix(path, match.EffectiveServerPath, match.Path);
            result[i + 1] = hasFilePrefix ? "file:" + rewrittenPath : rewrittenPath;
        }

        mapped = result;
        reason = null;
        return true;
    }

    private static string RewritePrefix(string path, string serverPath, string agentPath)
    {
        var trimmedServer = serverPath.TrimEnd('/', '\\');
        var trimmedAgent = agentPath.TrimEnd('/', '\\');

        return path.Length == trimmedServer.Length
            ? trimmedAgent
            : trimmedAgent + path[trimmedServer.Length..];
    }
}
