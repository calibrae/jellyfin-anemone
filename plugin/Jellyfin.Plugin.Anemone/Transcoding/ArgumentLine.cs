using System.Text;

namespace Jellyfin.Plugin.Anemone.Transcoding;

/// <summary>
/// Splits/joins a single ffmpeg command-line string into argv tokens using the same rules as .NET's
/// Unix <c>ProcessStartInfo.Arguments</c> parser (dotnet/runtime
/// <c>src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.Unix.cs</c>,
/// <c>ParseArgumentsIntoList</c>): whitespace separates tokens, double quotes group a run of
/// characters (including whitespace) into the current token without themselves appearing in the
/// output, a quoted segment may sit directly next to an unquoted one and both contribute to the same
/// token, and a backslash only has escaping meaning when it is followed (possibly through a run of
/// other backslashes) by a double quote — a run of N backslashes immediately before a quote collapses
/// to N/2 literal backslashes, and if N is odd the quote itself becomes a literal character instead of
/// toggling quoted mode. A backslash followed by anything else is emitted verbatim.
/// </summary>
public static class ArgumentLine
{
    /// <summary>Splits a command-line string into argv tokens.</summary>
    /// <param name="arguments">The raw command-line string (as Jellyfin builds it for ffmpeg).</param>
    /// <returns>The argv tokens, in order.</returns>
    public static List<string> Split(string arguments)
    {
        var results = new List<string>();
        if (string.IsNullOrEmpty(arguments))
        {
            return results;
        }

        var i = 0;
        var length = arguments.Length;

        while (true)
        {
            while (i < length && char.IsWhiteSpace(arguments[i]))
            {
                i++;
            }

            if (i == length)
            {
                break;
            }

            results.Add(ReadToken(arguments, ref i));
        }

        return results;
    }

    /// <summary>Joins argv tokens back into a display-friendly command line, quoting tokens that need it. For logging only.</summary>
    /// <param name="argv">The argv tokens.</param>
    /// <returns>A single string suitable for a log line.</returns>
    public static string Join(IEnumerable<string> argv)
    {
        return string.Join(' ', argv.Select(QuoteIfNeeded));
    }

    private static string ReadToken(string arguments, ref int i)
    {
        var length = arguments.Length;
        var sb = new StringBuilder();
        var inQuotes = false;

        while (i < length && (inQuotes || !char.IsWhiteSpace(arguments[i])))
        {
            var c = arguments[i];

            if (c == '\\')
            {
                var backslashCount = 0;
                while (i < length && arguments[i] == '\\')
                {
                    backslashCount++;
                    i++;
                }

                if (i < length && arguments[i] == '"')
                {
                    sb.Append('\\', backslashCount / 2);
                    if (backslashCount % 2 == 1)
                    {
                        sb.Append('"');
                        i++;
                    }

                    // else: even number of backslashes escape each other pairwise; the quote that
                    // follows is untouched and gets processed as a normal quote on the next loop turn.
                }
                else
                {
                    sb.Append('\\', backslashCount);
                }
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                i++;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        return sb.ToString();
    }

    private static string QuoteIfNeeded(string arg)
    {
        var needsQuoting = arg.Length == 0;
        if (!needsQuoting)
        {
            foreach (var c in arg)
            {
                if (char.IsWhiteSpace(c) || c == '"')
                {
                    needsQuoting = true;
                    break;
                }
            }
        }

        if (!needsQuoting)
        {
            return arg;
        }

        var sb = new StringBuilder(arg.Length + 2);
        sb.Append('"');
        foreach (var c in arg)
        {
            if (c == '"')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        sb.Append('"');
        return sb.ToString();
    }
}
