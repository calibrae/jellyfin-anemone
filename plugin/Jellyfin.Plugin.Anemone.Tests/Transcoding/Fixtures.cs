namespace Jellyfin.Plugin.Anemone.Tests.Transcoding;

/// <summary>
/// The two real ffmpeg command lines from RESEARCH.md §1 (the h264_videotoolbox HLS transcode, captured
/// verbatim from speedwagon's FFmpeg.Transcode-*.log — only the "…"-elided bitrate flags and path/hash
/// placeholders below are filled in with concrete values; a "DirectStream" (video copy, audio
/// re-encoded to aac_at) variant built from the same source per RESEARCH.md's description of that
/// variant: "-hls_segment_type fmp4 -hls_fmp4_init_filename ..." and "-start_number 457 after a seek").
/// These are <c>commandLineArguments</c> strings — i.e. everything ProcessStartInfo.Arguments would
/// hold, NOT including the "ffmpeg" executable name itself (Jellyfin passes that separately as
/// FileName).
/// </summary>
internal static class Fixtures
{
    internal const string InputPath = "/Volumes/data/_tvshows/Show Name/Season 01/Show Name - S01E01 - Pilot.mkv";

    internal const string TranscodesDir = "/Users/speedwagon/Library/Application Support/jellyfin/cache/transcodes";

    internal const string Md5 = "a7858cf3a2e6dbf7c9a1d5b6e4f0c2d1";

    /// <summary>1080p HEVC source, transcoded to h264_videotoolbox + aac_at, HLS mpegts segments.</summary>
    internal const string TranscodeCommandLine =
        "-analyzeduration 200M -probesize 1G -f matroska -init_hw_device videotoolbox=vt " +
        "-hwaccel videotoolbox -hwaccel_output_format videotoolbox_vld -noautorotate " +
        "-i file:\"" + InputPath + "\" -noautoscale -map_metadata -1 -map_chapters -1 " +
        "-threads 0 -map 0:0 -map 0:1 -map -0:s -codec:v:0 h264_videotoolbox -prio_speed 1 " +
        "-b:v 6671258 -maxrate 6671258 -bufsize 13342516 " +
        "-force_key_frames:0 \"expr:gte(t,n_forced*3)\" -g:v:0 72 -keyint_min:v:0 72 " +
        "-vf \"scale_vt=w=1280:h=640:format=nv12\" -codec:a:0 aac_at -ac 2 -ab 256000 -af \"volume=2\" " +
        "-copyts -avoid_negative_ts disabled -max_muxing_queue_size 2048 " +
        "-f hls -max_delay 5000000 -hls_time 3 -hls_segment_type mpegts -start_number 0 " +
        "-hls_segment_filename \"" + TranscodesDir + "/" + Md5 + "%d.ts\" -hls_playlist_type vod -hls_list_size 0 " +
        "-y \"" + TranscodesDir + "/" + Md5 + ".m3u8\"";

    /// <summary>Same source, video stream copied (direct-streamed), audio re-encoded — fmp4 HLS segments, mid-seek start.</summary>
    internal const string DirectStreamCommandLine =
        "-analyzeduration 200M -probesize 1G -f matroska -init_hw_device videotoolbox=vt " +
        "-hwaccel videotoolbox -hwaccel_output_format videotoolbox_vld -noautorotate " +
        "-i file:\"" + InputPath + "\" -noautoscale -map_metadata -1 -map_chapters -1 " +
        "-threads 0 -map 0:0 -map 0:1 -map -0:s -codec:v:0 copy " +
        "-codec:a:0 aac_at -ac 2 -ab 256000 -af \"volume=2\" " +
        "-copyts -avoid_negative_ts disabled -max_muxing_queue_size 2048 " +
        "-f hls -max_delay 5000000 -hls_time 3 -hls_segment_type fmp4 -hls_fmp4_init_filename \"" + Md5 + "-1.mp4\" -start_number 457 " +
        "-hls_segment_filename \"" + TranscodesDir + "/" + Md5 + "%d.m4s\" -hls_playlist_type vod -hls_list_size 0 " +
        "-y \"" + TranscodesDir + "/" + Md5 + ".m3u8\"";

    /// <summary>A progressive (non-HLS) transcode — must never be routable.</summary>
    internal const string ProgressiveCommandLine =
        "-analyzeduration 200M -probesize 1G -i file:\"" + InputPath + "\" -map_metadata -1 " +
        "-threads 0 -codec:v:0 h264_videotoolbox -codec:a:0 aac_at -f mp4 " +
        "-movflags frag_keyframe+empty_moov -y \"" + TranscodesDir + "/" + Md5 + ".mp4\"";

    /// <summary>An HLS transcode with subtitle burn-in — must never be routable (needs the server's attachments/ dir).</summary>
    internal const string SubtitleBurnInCommandLine =
        "-analyzeduration 200M -probesize 1G -i file:\"" + InputPath + "\" -map_metadata -1 " +
        "-threads 0 -codec:v:0 h264_videotoolbox " +
        "-vf \"subtitles=f='" + InputPath + "':si=0:fontsdir='/data/attachments/ab/abc123'\" " +
        "-codec:a:0 aac_at -f hls -hls_time 3 -hls_segment_filename \"" + TranscodesDir + "/" + Md5 + "%d.ts\" " +
        "-y \"" + TranscodesDir + "/" + Md5 + ".m3u8\"";

    // --- HwTranslator fixtures (PROTOCOL.md "Protocol v2 additions", real captured command lines) ---

    /// <summary>Unquoted, no-spaces path used by the HwTranslator fixtures below, exactly as captured.</summary>
    internal const string HwInputPath = "/Volumes/data/x/e.mkv";

    /// <summary>
    /// No-spaces transcodes dir for the HwTranslator fixtures below, so they need no quoting (unlike
    /// <see cref="TranscodesDir"/>, which has a space in "Application Support" and is quoted elsewhere).
    /// </summary>
    internal const string HwTranscodesDir = "/var/lib/jellyfin/transcodes";

    /// <summary>
    /// Real videotoolbox HLS transcode command line (mpegts segments, audio copied). Captured verbatim —
    /// only the elided "..." HLS-output tail from the task brief is filled in, matching the shape of
    /// <see cref="TranscodeCommandLine"/>'s own tail.
    /// </summary>
    internal const string HwVideotoolboxCommandLine =
        "-analyzeduration 200M -probesize 1G -f matroska -init_hw_device videotoolbox=vt " +
        "-hwaccel videotoolbox -hwaccel_output_format videotoolbox_vld -noautorotate " +
        "-i file:" + HwInputPath + " -noautoscale -map_metadata -1 -map_chapters -1 " +
        "-threads 0 -map 0:0 -map 0:1 -map -0:s -codec:v:0 h264_videotoolbox -prio_speed 1 " +
        "-b:v 1000000 -qmin -1 -qmax -1 " +
        "-force_key_frames:0 expr:gte(t,n_forced*3) -g:v:0 75 -keyint_min:v:0 75 " +
        "-vf scale_vt=w=640:h=360 -codec:a:0 copy " +
        "-copyts -avoid_negative_ts disabled -max_muxing_queue_size 2048 " +
        "-f hls -max_delay 5000000 -hls_time 3 -hls_segment_type mpegts -start_number 0 " +
        "-hls_segment_filename " + HwTranscodesDir + "/" + Md5 + "%d.ts -hls_playlist_type vod -hls_list_size 0 " +
        "-y " + HwTranscodesDir + "/" + Md5 + ".m3u8";

    /// <summary>
    /// Same shape as <see cref="HwVideotoolboxCommandLine"/> but with a larger scale target carrying an
    /// explicit pixel-format hint, audio re-encoded through AudioToolbox, and profile/level flags — all
    /// per the task brief's fixture 2 description.
    /// </summary>
    internal const string HwVideotoolboxHigherResCommandLine =
        "-analyzeduration 200M -probesize 1G -f matroska -init_hw_device videotoolbox=vt " +
        "-hwaccel videotoolbox -hwaccel_output_format videotoolbox_vld -noautorotate " +
        "-i file:" + HwInputPath + " -noautoscale -map_metadata -1 -map_chapters -1 " +
        "-threads 0 -map 0:0 -map 0:1 -map -0:s -codec:v:0 h264_videotoolbox -prio_speed 1 " +
        "-profile:v:0 high -level 42 " +
        "-b:v 4000000 -qmin -1 -qmax -1 " +
        "-force_key_frames:0 expr:gte(t,n_forced*3) -g:v:0 75 -keyint_min:v:0 75 " +
        "-vf scale_vt=w=1280:h=640:format=nv12 -codec:a:0 aac_at -ac 2 -ab 256000 -af volume=2 " +
        "-copyts -avoid_negative_ts disabled -max_muxing_queue_size 2048 " +
        "-f hls -max_delay 5000000 -hls_time 3 -hls_segment_type mpegts -start_number 0 " +
        "-hls_segment_filename " + HwTranscodesDir + "/" + Md5 + "%d.ts -hls_playlist_type vod -hls_list_size 0 " +
        "-y " + HwTranscodesDir + "/" + Md5 + ".m3u8";

    /// <summary>
    /// Real remux command line: video stream copied (bitstream-filtered for fmp4), audio re-encoded through
    /// AudioToolbox. Per the task brief's fixture 3: "-codec:v:0 copy -bsf:v h264_mp4toannexb ...
    /// -codec:a:0 aac_at ... -hls_segment_type fmp4".
    /// </summary>
    internal const string HwRemuxCommandLine =
        "-analyzeduration 200M -probesize 1G -f matroska -init_hw_device videotoolbox=vt " +
        "-hwaccel videotoolbox -hwaccel_output_format videotoolbox_vld -noautorotate " +
        "-i file:" + HwInputPath + " -noautoscale -map_metadata -1 -map_chapters -1 " +
        "-threads 0 -map 0:0 -map 0:1 -map -0:s -codec:v:0 copy -bsf:v h264_mp4toannexb " +
        "-codec:a:0 aac_at -ac 2 -ab 256000 " +
        "-copyts -avoid_negative_ts disabled -max_muxing_queue_size 2048 " +
        "-f hls -max_delay 5000000 -hls_time 3 -hls_segment_type fmp4 -hls_fmp4_init_filename " + Md5 + "-1.mp4 -start_number 0 " +
        "-hls_segment_filename " + HwTranscodesDir + "/" + Md5 + "%d.m4s -hls_playlist_type vod -hls_list_size 0 " +
        "-y " + HwTranscodesDir + "/" + Md5 + ".m3u8";
}
