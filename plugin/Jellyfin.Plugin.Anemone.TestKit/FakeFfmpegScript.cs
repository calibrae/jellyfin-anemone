using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.Anemone.TestKit;

/// <summary>
/// Writes a small POSIX shell script that stands in for ffmpeg on the LOCAL transcode path of
/// <see cref="AnemoneTranscodeManager.StartFfMpeg"/>. <see cref="AnemoneTranscodeManager"/> spawns a real
/// <see cref="System.Diagnostics.Process"/> for that path - there is no interface seam to fake instead -
/// so exercising it end to end (as opposed to routing to a <see cref="FakeAgentConnection"/>) needs a real,
/// tiny, deterministic executable. The script never inspects its own argv: the file it creates and the
/// exit behaviour are baked in at write time, so it stays correct regardless of the exact ffmpeg command
/// line a test passes as <c>commandLineArguments</c>.
/// </summary>
/// <remarks>
/// <para>Requires <c>/bin/bash</c> and Unix file permissions - not for Windows. CI only runs this on ubuntu-latest/macos-latest, matching the plugin's own supported platforms.</para>
/// <para>
/// The script always writes at least one line to stderr before doing anything else (a synthetic
/// <c>anemone-testkit: fake ffmpeg started</c> line, ahead of any caller-supplied <paramref name="stderrLines"/>
/// found in <see cref="Write"/>) - found the hard way: upstream's own
/// <c>MediaBrowser.Controller.MediaEncoding.JobLogger.StartStreamingLog</c>, which
/// <c>AnemoneTranscodeManager.StartFfMpeg</c> attaches to <c>process.StandardError</c> as fire-and-forget
/// (<c>_ = new JobLogger(...).StartStreamingLog(...)</c>), guards its read loop with
/// <c>while (!reader.EndOfStream ...)</c>. <see cref="StreamReader.EndOfStream"/> performs a *synchronous,
/// blocking* peek-read on first evaluation - before the loop body's first real <c>await</c> - so it runs
/// on the calling thread, not a background one. A silent child process (nothing written to stderr, pipe
/// not yet closed because the process is still alive) therefore blocks <c>StartFfMpeg</c> itself
/// indefinitely, even though "fire and forget" reads as "this can't block the caller". A real ffmpeg
/// writes progress lines constantly so this is never observable in production; a silent test double hits
/// it immediately. Emitting one line up front sidesteps it without deviating from the real behaviour a
/// short-lived/quiet ffmpeg (e.g. a fast remux) can legitimately have between its first and second lines.
/// </para>
/// </remarks>
public static class FakeFfmpegScript
{
    /// <summary>
    /// Writes the script to <paramref name="scriptPath"/> and marks it executable.
    /// </summary>
    /// <param name="scriptPath">Where to write the script. The parent directory must already exist.</param>
    /// <param name="outputFileToCreate">
    /// The file the script creates as soon as it runs (after <paramref name="delay"/>, if any) - point this
    /// at <c>state.WaitForPath ?? outputPath</c> so <c>StartFfMpeg</c>'s own polling loop sees it appear.
    /// Pass null to never create an output file (models "ffmpeg never produced anything").
    /// </param>
    /// <param name="stderrLines">Lines written to stderr, in order, right after the output file is created.</param>
    /// <param name="exitCode">Exit code used when the script exits - either immediately (if <paramref name="waitForStdinQuit"/> is false) or once "q" arrives on stdin.</param>
    /// <param name="waitForStdinQuit">
    /// When true (the default), the script blocks reading stdin lines until it sees a bare "q" (matching
    /// <c>TranscodingJob.Stop()</c>'s quit key), then exits - modelling a long-running ffmpeg that stays
    /// alive until asked to stop. When false, the script exits immediately after writing output/stderr.
    /// </param>
    /// <param name="delay">Optional delay before the script does anything - models a slow-to-start ffmpeg.</param>
    public static string Write(
        string scriptPath,
        string? outputFileToCreate,
        IReadOnlyList<string>? stderrLines = null,
        int exitCode = 0,
        bool waitForStdinQuit = true,
        TimeSpan? delay = null)
    {
        var sb = new StringBuilder();
        sb.Append("#!/bin/bash\n");
        sb.Append("set -u\n");

        if (delay is { } d && d > TimeSpan.Zero)
        {
            sb.Append("sleep ").Append(d.TotalSeconds.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        if (outputFileToCreate is not null)
        {
            sb.Append("mkdir -p ").Append(ShellQuote(Path.GetDirectoryName(outputFileToCreate) ?? ".")).Append('\n');
            sb.Append("touch ").Append(ShellQuote(outputFileToCreate)).Append('\n');
        }

        // See the "found the hard way" remark on the type: this unblocks JobLogger's synchronous
        // StreamReader.EndOfStream check on the FIRST loop iteration, which otherwise blocks the caller
        // of StartFfMpeg (not just a background task) for as long as this process stays silent.
        sb.Append("echo 'anemone-testkit: fake ffmpeg started' 1>&2\n");

        foreach (var line in stderrLines ?? [])
        {
            sb.Append("echo ").Append(ShellQuote(line)).Append(" 1>&2\n");
        }

        if (waitForStdinQuit)
        {
            // A plain blocking read (no -t timeout) is deliberate: macOS ships bash 3.2, whose `read -t`
            // both rejects sub-second values ("invalid timeout specification" for -t 0.05) and, on an
            // actual timeout, exits with status 1 - indistinguishable from real EOF (bash 4+'s ">128 on
            // timeout" convention isn't available), so a portable heartbeat-on-timeout loop isn't
            // straightforward here. It isn't needed anyway: the one-off "started" line above is what
            // unblocks JobLogger's synchronous EndOfStream check on ITS FIRST evaluation, which is the
            // one that would otherwise block StartFfMpeg's own caller thread (see the type-level remark).
            // Everything after that runs as a background continuation, where a blocked read just parks a
            // thread-pool thread for the process's lifetime - bounded by the test itself, not a hang.
            sb.Append("while IFS= read -r line; do\n");
            sb.Append("  if [ \"$line\" = \"q\" ]; then break; fi\n");
            sb.Append("done\n");
        }

        sb.Append("exit ").Append(exitCode.ToString(CultureInfo.InvariantCulture)).Append('\n');

        File.WriteAllText(scriptPath, sb.ToString());

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return scriptPath;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
