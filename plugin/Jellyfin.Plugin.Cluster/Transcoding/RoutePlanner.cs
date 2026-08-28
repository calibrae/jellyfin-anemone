using Jellyfin.Plugin.Cluster.Contracts;

namespace Jellyfin.Plugin.Cluster.Transcoding;

/// <summary>Result of <see cref="RoutePlanner.Analyze"/> — pure argv analysis, no StreamState needed.</summary>
public sealed record RouteAnalysis(
    bool IsHls,
    int InputCount,
    IReadOnlyList<string> InputPaths,
    JobRequirements Requirements,
    int? SegmentFilenameIndex,
    int OutputIndex,
    bool IsRoutable,
    string? NotRoutableReason);

/// <summary>
/// jfc: pure ffmpeg-argv analysis and rewriting, split out of <see cref="JobRouter"/> so it's testable
/// without a live <c>StreamState</c>. See PROTOCOL.md "Argument rewriting".
/// </summary>
public static class RoutePlanner
{
    /// <summary>Inspects an argv list and decides whether it's a candidate for remote routing.</summary>
    /// <param name="argv">The split ffmpeg command line.</param>
    /// <returns>The analysis result.</returns>
    public static RouteAnalysis Analyze(IReadOnlyList<string> argv)
    {
        if (argv.Count == 0)
        {
            return new RouteAnalysis(false, 0, [], new JobRequirements([], [], [], [], []), null, 0, false, "empty command line");
        }

        var isHls = false;
        var concatFound = false;
        var burnInFound = false;
        var inputPaths = new List<string>();
        var hwaccels = new List<string>();
        var decoders = new List<string>();
        var encoders = new List<string>();
        var filters = new List<string>();
        int? segmentFilenameIndex = null;

        var firstInputIndex = -1;
        var lastInputIndex = -1;
        for (var i = 0; i < argv.Count; i++)
        {
            if (argv[i] == "-i")
            {
                if (firstInputIndex < 0)
                {
                    firstInputIndex = i;
                }

                lastInputIndex = i;
            }
        }

        for (var i = 0; i < argv.Count; i++)
        {
            var tok = argv[i];

            if (tok.Contains("subtitles=", StringComparison.Ordinal) || tok.Contains("fontsdir=", StringComparison.Ordinal))
            {
                burnInFound = true;
            }

            switch (tok)
            {
                case "-i" when i + 1 < argv.Count:
                {
                    var raw = argv[i + 1];
                    var path = raw.StartsWith("file:", StringComparison.Ordinal) ? raw["file:".Length..] : raw;
                    inputPaths.Add(path);
                    break;
                }

                case "-f" when i + 1 < argv.Count:
                {
                    var val = argv[i + 1];
                    if (string.Equals(val, "hls", StringComparison.Ordinal))
                    {
                        isHls = true;
                    }
                    else if (string.Equals(val, "concat", StringComparison.Ordinal))
                    {
                        concatFound = true;
                    }

                    break;
                }

                case "-hwaccel" when i + 1 < argv.Count:
                    hwaccels.Add(argv[i + 1]);
                    break;

                case "-init_hw_device" when i + 1 < argv.Count:
                {
                    var val = argv[i + 1];
                    var eq = val.IndexOf('=');
                    hwaccels.Add(eq >= 0 ? val[..eq] : val);
                    break;
                }

                case "-hls_segment_filename" when i + 1 < argv.Count:
                    segmentFilenameIndex = i + 1;
                    break;

                case "-vf" or "-af" or "-filter_complex" when i + 1 < argv.Count:
                {
                    var val = argv[i + 1];
                    filters.AddRange(ExtractFilterNames(val));
                    if (tok == "-filter_complex"
                        && val.Contains("overlay", StringComparison.Ordinal)
                        && val.Contains("subtitles", StringComparison.Ordinal))
                    {
                        burnInFound = true;
                    }

                    break;
                }

                default:
                    if (IsCodecFlag(tok, out var isVideo, out var isAudio) && i + 1 < argv.Count)
                    {
                        var val = argv[i + 1];
                        if (isVideo && firstInputIndex >= 0 && i < firstInputIndex)
                        {
                            decoders.Add(val);
                        }

                        if ((isVideo || isAudio) && lastInputIndex >= 0 && i > lastInputIndex
                            && !string.Equals(val, "copy", StringComparison.Ordinal))
                        {
                            encoders.Add(val);
                        }
                    }

                    break;
            }
        }

        string? reason = null;
        if (!isHls)
        {
            reason = "no -f hls in the command line";
        }
        else if (inputPaths.Count != 1)
        {
            reason = $"expected exactly one -i, found {inputPaths.Count}";
        }
        else if (inputPaths[0].Contains("://", StringComparison.Ordinal))
        {
            reason = "input is a URL, not a local path";
        }
        else if (concatFound)
        {
            reason = "-f concat present";
        }
        else if (burnInFound)
        {
            reason = "subtitle/font burn-in present";
        }

        // jfc: requirements are conceptually a set (e.g. the real HLS transcode command line passes
        // both -init_hw_device videotoolbox=vt and -hwaccel videotoolbox, which would otherwise yield
        // two identical "videotoolbox" entries) — dedupe, preserving first-seen order.
        var requirements = new JobRequirements(
            hwaccels.Distinct(StringComparer.Ordinal).ToList(),
            encoders.Distinct(StringComparer.Ordinal).ToList(),
            decoders.Distinct(StringComparer.Ordinal).ToList(),
            filters.Distinct(StringComparer.Ordinal).ToList(),
            inputPaths);
        var outputIndex = argv.Count - 1;

        return new RouteAnalysis(isHls, inputPaths.Count, inputPaths, requirements, segmentFilenameIndex, outputIndex, reason is null, reason);
    }

    /// <summary>
    /// Rewrites an argv list per PROTOCOL.md "Argument rewriting": the HLS segment filename and the
    /// playlist (last element) are redirected to the ingest endpoint, and PUT/auth options are
    /// inserted immediately before the (now rewritten) last element. Every other token is untouched.
    /// </summary>
    /// <param name="argv">The original ffmpeg argv.</param>
    /// <param name="ingestBase">The base URL the agent should PUT segments to (no trailing slash required).</param>
    /// <param name="jobId">The job id (used in the ingest path).</param>
    /// <param name="token">The ingest bearer token.</param>
    /// <returns>The rewritten argv, ready to send to an agent.</returns>
    public static IReadOnlyList<string> Rewrite(IReadOnlyList<string> argv, string ingestBase, string jobId, string token)
    {
        var result = new List<string>(argv);
        var baseUrl = ingestBase.TrimEnd('/');

        for (var i = 0; i < result.Count - 1; i++)
        {
            if (result[i] == "-hls_segment_filename")
            {
                var basename = Path.GetFileName(result[i + 1]);
                result[i + 1] = $"{baseUrl}/Cluster/ingest/{jobId}/{basename}";
                break;
            }
        }

        var outputIndex = result.Count - 1;
        var playlistBasename = Path.GetFileName(result[outputIndex]);
        result[outputIndex] = $"{baseUrl}/Cluster/ingest/{jobId}/{playlistBasename}";

        result.InsertRange(
            result.Count - 1,
            new[]
            {
                "-method",
                "PUT",
                "-http_persistent",
                "1",
                "-headers",
                $"Authorization: Bearer {token}\r\n",
            });

        return result;
    }

    private static IEnumerable<string> ExtractFilterNames(string filterGraph)
    {
        foreach (var rawSegment in filterGraph.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = rawSegment.Trim();

            // Drop leading [label] pad references.
            while (segment.StartsWith('[') )
            {
                var close = segment.IndexOf(']');
                if (close < 0)
                {
                    break;
                }

                segment = segment[(close + 1)..];
            }

            // Drop trailing [label] pad references.
            while (segment.EndsWith(']'))
            {
                var open = segment.LastIndexOf('[');
                if (open < 0)
                {
                    break;
                }

                segment = segment[..open];
            }

            segment = segment.Trim();
            if (segment.Length == 0)
            {
                continue;
            }

            var eq = segment.IndexOf('=');
            var name = eq >= 0 ? segment[..eq] : segment;

            if (name.Length == 0 || name is "null" or "anull")
            {
                continue;
            }

            yield return name;
        }
    }

    private static bool IsCodecFlag(string tok, out bool isVideo, out bool isAudio)
    {
        isVideo = false;
        isAudio = false;

        string rest;
        if (tok.StartsWith("-c:", StringComparison.Ordinal))
        {
            rest = tok["-c:".Length..];
        }
        else if (tok.StartsWith("-codec:", StringComparison.Ordinal))
        {
            rest = tok["-codec:".Length..];
        }
        else
        {
            return false;
        }

        if (rest.Length == 0)
        {
            return false;
        }

        switch (rest[0])
        {
            case 'v':
                isVideo = true;
                return true;
            case 'a':
                isAudio = true;
                return true;
            default:
                return false;
        }
    }
}
