using Jellyfin.Plugin.Anemone.Contracts;

namespace Jellyfin.Plugin.Anemone.Transcoding;

/// <summary>
/// Translates the hardware-acceleration-specific parts of a Jellyfin-built ffmpeg command line from the
/// SERVER's hardware (VideoToolbox on this deployment) to a candidate agent's hardware, per PROTOCOL.md
/// "Hardware acceleration" and its translation table. Pure, static, no I/O.
///
/// Refusing is always correct; guessing never is. Anything this class does not fully model - burn-in,
/// <c>-filter_complex</c>, tonemapping, <c>-f concat</c>, more than one input, an unrecognised filter, a
/// target profile with no table entry, or a translated encoder/filter/hwaccel the agent doesn't actually
/// report - makes <see cref="TryTranslate"/> return false rather than emit something that might not run.
/// </summary>
public static class HwTranslator
{
    /// <summary>Target profiles the translation table (PROTOCOL.md) has an entry for.</summary>
    private static readonly string[] TableProfiles = ["none", "vaapi", "nvenc", "qsv"];

    private static readonly HashSet<string> RecognizedFilterNames = new(StringComparer.Ordinal)
    {
        "scale", "scale_vt", "scale_vaapi", "scale_cuda", "scale_qsv", "format", "volume", "anull", "null",
    };

    private static readonly HashSet<string> ScaleFilterNames = new(StringComparer.Ordinal)
    {
        "scale", "scale_vt", "scale_vaapi", "scale_cuda", "scale_qsv",
    };

    /// <summary>ffmpeg <c>-hwaccel</c>/<c>-init_hw_device</c> device-type token -&gt; our profile name.</summary>
    private static readonly Dictionary<string, string> HwaccelTokenToProfile = new(StringComparer.OrdinalIgnoreCase)
    {
        ["videotoolbox"] = "videotoolbox",
        ["vaapi"] = "vaapi",
        ["cuda"] = "nvenc",
        ["qsv"] = "qsv",
        ["amf"] = "amf",
        ["rkmpp"] = "rkmpp",
    };

    /// <summary>
    /// <c>-codec:v:*</c> encoder suffix -&gt; our profile name. Deliberately separate from
    /// <see cref="HwaccelTokenToProfile"/>: the NVENC encoder is named <c>h264_nvenc</c> (suffix
    /// <c>nvenc</c>) but its ffmpeg hwaccel device type is <c>cuda</c> - the two vocabularies agree for
    /// every other profile but not that one.
    /// </summary>
    private static readonly Dictionary<string, string> EncoderSuffixToProfile = new(StringComparer.OrdinalIgnoreCase)
    {
        ["videotoolbox"] = "videotoolbox",
        ["vaapi"] = "vaapi",
        ["nvenc"] = "nvenc",
        ["qsv"] = "qsv",
        ["amf"] = "amf",
        ["rkmpp"] = "rkmpp",
    };

    /// <summary>Target profile -&gt; (h264 encoder, hevc encoder), per the PROTOCOL.md translation table.</summary>
    private static readonly Dictionary<string, (string H264, string Hevc)> EncoderTable = new(StringComparer.Ordinal)
    {
        ["none"] = ("libx264", "libx265"),
        ["vaapi"] = ("h264_vaapi", "hevc_vaapi"),
        ["nvenc"] = ("h264_nvenc", "hevc_nvenc"),
        ["qsv"] = ("h264_qsv", "hevc_qsv"),
    };

    /// <summary>Target profile -&gt; scale filter name, per the PROTOCOL.md translation table.</summary>
    private static readonly Dictionary<string, string> ScaleFilterTable = new(StringComparer.Ordinal)
    {
        ["none"] = "scale",
        ["vaapi"] = "scale_vaapi",
        ["nvenc"] = "scale_cuda",
        ["qsv"] = "scale_qsv",
    };

    /// <summary>
    /// Infers an agent's hwaccel profile when its <c>hello</c> frame didn't announce one: videotoolbox on
    /// macOS, else vaapi if the agent's ffmpeg reports it, else nvenc (ffmpeg calls it <c>cuda</c>), else
    /// qsv, else none. See PROTOCOL.md "Hardware acceleration".
    /// </summary>
    /// <param name="hwaccels">The agent's <c>ffmpeg.hwaccels</c> list.</param>
    /// <param name="platform">The agent's announced platform (e.g. <c>macos-arm64</c>).</param>
    /// <returns>One of <c>videotoolbox</c>, <c>vaapi</c>, <c>nvenc</c>, <c>qsv</c>, <c>none</c>.</returns>
    public static string InferProfile(IReadOnlyList<string> hwaccels, string? platform)
    {
        ArgumentNullException.ThrowIfNull(hwaccels);

        if (!string.IsNullOrEmpty(platform) && platform.Contains("macos", StringComparison.OrdinalIgnoreCase))
        {
            return "videotoolbox";
        }

        if (ContainsIgnoreCase(hwaccels, "vaapi"))
        {
            return "vaapi";
        }

        if (ContainsIgnoreCase(hwaccels, "cuda"))
        {
            return "nvenc";
        }

        if (ContainsIgnoreCase(hwaccels, "qsv"))
        {
            return "qsv";
        }

        return "none";
    }

    /// <summary>
    /// Identifies the hwaccel profile a command line was built for, from its own tokens: the
    /// <c>-hwaccel X</c>/<c>-init_hw_device X=</c> device type first, falling back to the
    /// <c>-codec:v:*</c> encoder suffix (e.g. <c>h264_videotoolbox</c>) when neither is present. "none"
    /// when nothing hardware-specific is found (a software encode, or a pure stream-copy remux with no
    /// hwaccel setup at all).
    /// </summary>
    /// <param name="argv">The ffmpeg argv.</param>
    /// <returns>One of <c>videotoolbox</c>, <c>vaapi</c>, <c>nvenc</c>, <c>qsv</c>, <c>amf</c>, <c>rkmpp</c>, <c>none</c>.</returns>
    public static string IdentifySourceProfile(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        for (var i = 0; i < argv.Count - 1; i++)
        {
            if (argv[i] == "-hwaccel" && HwaccelTokenToProfile.TryGetValue(argv[i + 1], out var byHwaccel))
            {
                return byHwaccel;
            }

            if (argv[i] == "-init_hw_device")
            {
                var val = argv[i + 1];
                var eq = val.IndexOf('=');
                var token = eq >= 0 ? val[..eq] : val;
                if (HwaccelTokenToProfile.TryGetValue(token, out var byDevice))
                {
                    return byDevice;
                }
            }
        }

        for (var i = 0; i < argv.Count - 1; i++)
        {
            if (!RoutePlanner.IsCodecFlag(argv[i], out var isVideo, out _) || !isVideo)
            {
                continue;
            }

            var value = argv[i + 1];
            if (string.Equals(value, "copy", StringComparison.Ordinal))
            {
                continue;
            }

            var suffix = MapEncoderSuffixToProfile(value);
            if (suffix is not null)
            {
                return suffix;
            }
        }

        return "none";
    }

    /// <summary>
    /// Translates <paramref name="argv"/>'s hardware-specific tokens from whatever profile it was built for
    /// to <paramref name="agent"/>'s profile (<see cref="AgentInfo.Hwaccel"/>). See the class remarks and
    /// PROTOCOL.md "Hardware acceleration" for exactly what is and isn't translated.
    /// </summary>
    /// <param name="argv">The ffmpeg argv (already path-mapped; only hw-related tokens are touched here).</param>
    /// <param name="agent">The candidate agent.</param>
    /// <param name="translated">The translated argv on success; unspecified on failure.</param>
    /// <param name="reason">Why translation succeeded (informational) or refused (why), always set.</param>
    /// <returns>False when the job can't be safely translated for this agent.</returns>
    public static bool TryTranslate(IReadOnlyList<string> argv, AgentInfo agent, out IReadOnlyList<string> translated, out string reason)
    {
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(agent);

        translated = argv;
        var target = string.IsNullOrWhiteSpace(agent.Hwaccel) ? "none" : agent.Hwaccel;
        var source = IdentifySourceProfile(argv);

        // Rule 1 is an unconditional fast path: same profile, zero risk, argv byte-identical - exactly
        // today's (pre-translation) behaviour, checked BEFORE any of the "don't fully model" refusals
        // below so a same-profile agent is never penalized for content that was already safe to run as-is.
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            translated = argv;
            reason = "identity: source profile matches agent profile, argv unchanged";
            return true;
        }

        if (!TryCheckRefusals(argv, out reason))
        {
            return false;
        }

        if (!TableProfiles.Contains(target, StringComparer.OrdinalIgnoreCase))
        {
            reason = $"no translation table entry for target profile '{target}'";
            return false;
        }

        var result = new List<string>(argv);
        var isRemux = IsRemux(result);

        if (!isRemux)
        {
            if (!RewriteDeviceInit(result, target, agent, out reason))
            {
                return false;
            }

            if (!RewriteEncoder(result, target, agent, out reason))
            {
                return false;
            }

            if (!RewriteScaleFilter(result, target, agent, out reason))
            {
                return false;
            }

            DropPrioSpeed(result);
        }

        if (!RewriteAacAt(result, agent, out reason))
        {
            return false;
        }

        translated = result;
        reason = isRemux
            ? $"remux ({source} -> {target}): no video translation needed, audio mapped if aac_at was present"
            : $"translated {source} -> {target}";
        return true;
    }

    private static bool TryCheckRefusals(IReadOnlyList<string> argv, out string reason)
    {
        var inputCount = 0;
        var firstInputIndex = -1;
        for (var i = 0; i < argv.Count; i++)
        {
            if (string.Equals(argv[i], "-i", StringComparison.Ordinal))
            {
                firstInputIndex = i;
                break;
            }
        }

        for (var i = 0; i < argv.Count; i++)
        {
            var tok = argv[i];

            if (firstInputIndex >= 0 && i < firstInputIndex
                && RoutePlanner.IsCodecFlag(tok, out var isPreInputVideo, out _) && isPreInputVideo)
            {
                reason = $"hardware decoder before input not modeled ('{tok} {(i + 1 < argv.Count ? argv[i + 1] : string.Empty)}')";
                return false;
            }

            if (tok.Contains("subtitles=", StringComparison.Ordinal) || tok.Contains("fontsdir=", StringComparison.Ordinal))
            {
                reason = "subtitle/font burn-in present";
                return false;
            }

            if (string.Equals(tok, "-filter_complex", StringComparison.Ordinal))
            {
                reason = "-filter_complex present";
                return false;
            }

            if (string.Equals(tok, "-i", StringComparison.Ordinal))
            {
                inputCount++;
            }

            if (string.Equals(tok, "-f", StringComparison.Ordinal) && i + 1 < argv.Count
                && string.Equals(argv[i + 1], "concat", StringComparison.Ordinal))
            {
                reason = "-f concat present";
                return false;
            }

            if ((string.Equals(tok, "-vf", StringComparison.Ordinal) || string.Equals(tok, "-af", StringComparison.Ordinal))
                && i + 1 < argv.Count)
            {
                var value = argv[i + 1];

                // Check the more specific reasons (burn-in, tonemapping) before the generic "unrecognized
                // filter" catch-all, so e.g. "subtitles=..." is reported as burn-in, not as an unknown filter.
                if (value.Contains("subtitles=", StringComparison.Ordinal) || value.Contains("fontsdir=", StringComparison.Ordinal))
                {
                    reason = "subtitle/font burn-in present";
                    return false;
                }

                if (value.Contains("tonemap", StringComparison.Ordinal) || value.Contains("zscale", StringComparison.Ordinal))
                {
                    reason = $"tonemapping filter present in {tok} ('{value}')";
                    return false;
                }

                foreach (var name in RoutePlanner.ExtractFilterNames(value))
                {
                    if (!RecognizedFilterNames.Contains(name))
                    {
                        reason = $"unrecognized filter '{name}' in {tok}";
                        return false;
                    }
                }
            }
        }

        if (inputCount > 1)
        {
            reason = $"more than one -i ({inputCount})";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool IsRemux(IReadOnlyList<string> argv)
    {
        for (var i = 0; i < argv.Count - 1; i++)
        {
            if (RoutePlanner.IsCodecFlag(argv[i], out var isVideo, out _) && isVideo)
            {
                return string.Equals(argv[i + 1], "copy", StringComparison.Ordinal);
            }
        }

        return false;
    }

    private static bool RewriteDeviceInit(List<string> argv, string target, AgentInfo agent, out string reason)
    {
        var firstIndex = -1;
        for (var i = argv.Count - 2; i >= 0; i--)
        {
            var flag = argv[i];
            if (flag is "-init_hw_device" or "-hwaccel" or "-hwaccel_output_format")
            {
                firstIndex = i;
                argv.RemoveRange(i, 2);
            }
        }

        IReadOnlyList<string> replacement;
        string? emittedHwaccel;
        switch (target)
        {
            case "none":
                replacement = [];
                emittedHwaccel = null;
                break;
            case "vaapi":
                if (string.IsNullOrEmpty(agent.HwaccelDevice))
                {
                    reason = "agent has no hwaccel_device for vaapi";
                    return false;
                }

                replacement =
                [
                    "-init_hw_device", $"vaapi=va:{agent.HwaccelDevice}",
                    "-hwaccel", "vaapi",
                    "-hwaccel_output_format", "vaapi",
                ];
                emittedHwaccel = "vaapi";
                break;
            case "nvenc":
                replacement = ["-hwaccel", "cuda", "-hwaccel_output_format", "cuda"];
                emittedHwaccel = "cuda";
                break;
            case "qsv":
                if (string.IsNullOrEmpty(agent.HwaccelDevice))
                {
                    reason = "agent has no hwaccel_device for qsv";
                    return false;
                }

                replacement =
                [
                    "-init_hw_device", $"qsv=qs:{agent.HwaccelDevice}",
                    "-hwaccel", "qsv",
                    "-hwaccel_output_format", "qsv",
                ];
                emittedHwaccel = "qsv";
                break;
            default:
                reason = $"no translation table entry for target profile '{target}'";
                return false;
        }

        if (emittedHwaccel is not null && !ContainsIgnoreCase(agent.Hwaccels, emittedHwaccel))
        {
            reason = $"agent does not report hwaccel '{emittedHwaccel}'";
            return false;
        }

        var insertAt = firstIndex >= 0 ? firstIndex : FindInputIndex(argv);
        argv.InsertRange(insertAt, replacement);

        reason = string.Empty;
        return true;
    }

    private static int FindInputIndex(List<string> argv)
    {
        var idx = argv.IndexOf("-i");
        return idx >= 0 ? idx : argv.Count;
    }

    private static bool RewriteEncoder(List<string> argv, string target, AgentInfo agent, out string reason)
    {
        for (var i = 0; i < argv.Count - 1; i++)
        {
            if (!RoutePlanner.IsCodecFlag(argv[i], out var isVideo, out _) || !isVideo)
            {
                continue;
            }

            var value = argv[i + 1];
            if (string.Equals(value, "copy", StringComparison.Ordinal))
            {
                reason = string.Empty;
                return true;
            }

            var codecBase = ExtractCodecBase(value);
            if (codecBase is null)
            {
                reason = $"unrecognized video encoder '{value}'";
                return false;
            }

            var (h264, hevc) = EncoderTable[target];
            var translated = codecBase == "h264" ? h264 : hevc;

            if (!ContainsIgnoreCase(agent.Encoders, translated))
            {
                reason = $"agent does not report encoder '{translated}'";
                return false;
            }

            argv[i + 1] = translated;
            reason = string.Empty;
            return true;
        }

        reason = string.Empty;
        return true;
    }

    private static bool RewriteScaleFilter(List<string> argv, string target, AgentInfo agent, out string reason)
    {
        for (var i = 0; i < argv.Count - 1; i++)
        {
            if (!string.Equals(argv[i], "-vf", StringComparison.Ordinal))
            {
                continue;
            }

            var value = argv[i + 1];
            var segments = value.Split(',');
            var touched = false;

            for (var s = 0; s < segments.Length; s++)
            {
                var segment = segments[s];
                var eq = segment.IndexOf('=');
                var name = eq >= 0 ? segment[..eq] : segment;

                if (!ScaleFilterNames.Contains(name))
                {
                    continue;
                }

                touched = true;
                var targetName = ScaleFilterTable[target];

                if (!ContainsIgnoreCase(agent.Filters, targetName))
                {
                    reason = $"agent does not report filter '{targetName}'";
                    return false;
                }

                var (w, h) = ExtractWidthHeight(eq >= 0 ? segment[(eq + 1)..] : string.Empty);
                segments[s] = w is not null && h is not null
                    ? $"{targetName}=w={w}:h={h}"
                    : targetName;
            }

            if (touched)
            {
                argv[i + 1] = string.Join(',', segments);
            }

            reason = string.Empty;
            return true;
        }

        reason = string.Empty;
        return true;
    }

    private static (string? W, string? H) ExtractWidthHeight(string filterOptions)
    {
        string? w = null;
        string? h = null;

        foreach (var part in filterOptions.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }

            var key = part[..eq];
            var value = part[(eq + 1)..];
            if (string.Equals(key, "w", StringComparison.Ordinal))
            {
                w = value;
            }
            else if (string.Equals(key, "h", StringComparison.Ordinal))
            {
                h = value;
            }
        }

        return (w, h);
    }

    private static void DropPrioSpeed(List<string> argv)
    {
        for (var i = argv.Count - 2; i >= 0; i--)
        {
            if (string.Equals(argv[i], "-prio_speed", StringComparison.Ordinal))
            {
                argv.RemoveRange(i, 2);
            }
        }
    }

    private static bool RewriteAacAt(List<string> argv, AgentInfo agent, out string reason)
    {
        for (var i = 0; i < argv.Count - 1; i++)
        {
            if (!RoutePlanner.IsCodecFlag(argv[i], out _, out var isAudio) || !isAudio)
            {
                continue;
            }

            if (!string.Equals(argv[i + 1], "aac_at", StringComparison.Ordinal))
            {
                continue;
            }

            if (!ContainsIgnoreCase(agent.Encoders, "aac"))
            {
                reason = "agent does not report encoder 'aac'";
                return false;
            }

            argv[i + 1] = "aac";
        }

        reason = string.Empty;
        return true;
    }

    private static string? ExtractCodecBase(string encoderValue)
    {
        if (encoderValue.StartsWith("h264", StringComparison.Ordinal) || string.Equals(encoderValue, "libx264", StringComparison.Ordinal))
        {
            return "h264";
        }

        if (encoderValue.StartsWith("hevc", StringComparison.Ordinal) || string.Equals(encoderValue, "libx265", StringComparison.Ordinal))
        {
            return "hevc";
        }

        return null;
    }

    private static string? MapEncoderSuffixToProfile(string encoderValue)
    {
        foreach (var (suffix, profile) in EncoderSuffixToProfile)
        {
            if (encoderValue.EndsWith("_" + suffix, StringComparison.OrdinalIgnoreCase))
            {
                return profile;
            }
        }

        return null;
    }

    private static bool ContainsIgnoreCase(IReadOnlyList<string> values, string target)
    {
        foreach (var v in values)
        {
            if (string.Equals(v, target, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
