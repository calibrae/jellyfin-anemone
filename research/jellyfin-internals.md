# Jellyfin Transcoding Pipeline — Source Research

Research agent report, 2026-08-28. Repo: `https://github.com/jellyfin/jellyfin.git`, shallow clone,
commit `6ad1e341b18432a7c7309cbd3f744cf6c2cb5ffe` (master, `SharedVersion.cs:3` → 12.0.0).
Cross-checked afterwards against the **v10.11.0 tag** (the version running on speedwagon): DI order
(`ApplicationHost.cs:460/462`), `ITranscodeManager.StartFfMpeg` signature, `TranscodingJob.Process`
usages and `TranscodingJob.Stop()` are identical — see RESEARCH.md §3.

---

## 1. FFmpeg binary path resolution and startup validation

### Path resolution precedence
`MediaBrowser.MediaEncoding/Encoder/MediaEncoder.cs:180-221` (`SetFFmpegPath()`), precedence documented at
`MediaEncoder.cs:174-179`: **CLI/env var > config XML > $PATH**.

1. `_startupOptionFFmpegPath`, set in the constructor at `MediaEncoder.cs:127`:
   `_startupOptionFFmpegPath = config.GetValue<string>(ConfigurationExtensions.FfmpegPathKey) ?? string.Empty;`
   `FfmpegPathKey = "ffmpeg"` (`MediaBrowser.Controller/Extensions/ConfigurationExtensions.cs:50`). Populated by,
   in `ConfigurationBuilder` order (`Jellyfin.Server/Program.cs:347-365` on master; `:337-341` on v10.11.0 —
   env `JELLYFIN_FFMPEG` is added *before* the CLI in-memory collection, so **CLI wins over env**):
   - `--ffmpeg <path>` CLI switch (`Jellyfin.Server/StartupOptions.cs:55-56`, mapped to config key `"ffmpeg"` in
     `ConvertToConfig()` at `StartupOptions.cs:108-111`)
   - env var `JELLYFIN_FFMPEG` (`.AddEnvironmentVariables("JELLYFIN_")`, prefix stripped)
2. Fallback: `_configurationManager.GetEncodingOptions().EncoderAppPath` — the `<EncoderAppPath>` field in
   `encoding.xml`, editable in the dashboard — `MediaEncoder.cs:195`.
3. Fallback: literal `"ffmpeg"`, resolved via OS `$PATH` — `MediaEncoder.cs:200`.

The resolved path is validated (`ValidatePath`, `:293-309`, runs `EncoderValidator.ValidateVersion()`); on success
`_ffmpegPath` is set and written back to config as `EncoderAppPathDisplay` (`:212-215`).

**`SetFFmpegPath()` runs exactly once, at server startup** — called from
`Emby.Server.Implementations/ApplicationHost.cs:429` in `RunStartupTasksAsync()` (`:417` on v10.11.0). No controller
re-invokes it when the admin edits `EncoderAppPath`; a restart is required. **All probing below happens once per
server process lifetime, not per transcode.**

`FFmpeg:novalidation=true` (`ConfigurationExtensions.cs:35`) skips *all* startup validation (`MediaEncoder.cs:182-187`),
but also skips populating the encoder/decoder/filter/hwaccel lists — not a safe shortcut for a wrapper.

### Every non-transcode invocation Jellyfin makes at startup
All in `MediaBrowser.MediaEncoding/Encoder/EncoderValidator.cs`, launched with `UseShellExecute=false`, stdout+stderr
redirected and drained concurrently (`:666-674`), `CreateNoWindow=true`:

| # | Purpose | Args | Reads | Call site |
|---|---|---|---|---|
| 1 | Version check | `-version` | stdout | `ValidateVersion()` `:221-243`, `GetFFmpegVersion()` `:308-330` |
| 2 | Decoder list | `-decoders` | stdout, filtered against `_requiredDecoders` (~38, `:19-62`) | `GetCodecs(Codec.Decoder)` `:578-607` |
| 3 | Encoder list | `-encoders` | stdout, filtered against `_requiredEncoders` (~35, `:64-102`) | `GetCodecs(Codec.Encoder)` |
| 4 | Filter list | `-filters` | stdout, filtered against `_requiredFilters` (~40, `:104-155`) | `GetFFmpegFilters()` `:609-635` |
| 5 | Hwaccel list | `-hwaccels` | stdout | `GetHwaccelTypes()` `:466-487` |
| 6 | Filter option probe | `-h filter=<name>` (`scale_cuda`, `tonemap_cuda`, `tonemap_opencl`, `overlay_opencl/vaapi/vulkan`, `transpose_opencl`) | stdout | `CheckFilterWithOption` `:489-515` |
| 7 | BSF option probe | `-h bsf=<name>` (`hevc_metadata`, `av1_metadata`, `dovi_rpu`) | stdout | `CheckBitStreamFilterWithOption` `:517-543` |
| 8 | Interactive-key probe | `-hide_banner -f lavfi -i nullsrc=s=1x1:d=<10000\|1000> -f null -`, with literal `"?"` written to stdin | stderr, looks for `"p      pause transcoding"` | `CheckSupportedRuntimeKey` `:545-566`, `MediaEncoder.cs:236` |
| 9 | Hwaccel flag probe | `-loglevel quiet -hwaccel_flags +low_priority -hide_banner -f lavfi -i nullsrc=s=1x1:d=100 -f null -` | exit code | `CheckSupportedHwaccelFlag` `:568-571` |
| 10 | ffprobe option probe | **ffprobe**: `-loglevel quiet -f lavfi -i nullsrc=s=1x1:d=1 -only_first_vframe` | exit code | `CheckSupportedProberOption` `:573-576` |
| 11 | VAAPI driver probe (Linux, if configured) | `-v verbose -hide_banner -init_hw_device vaapi=va:<VaapiDevice>` | stderr substring | `CheckVaapiDeviceByDriverName` `:403-425` |
| 12 | Vulkan/DRM interop probe (Linux, vaapi) | `-v verbose -hide_banner -init_hw_device drm=dr:<VaapiDevice> -init_hw_device vulkan=vk@dr` | stderr | `CheckVulkanDrmDeviceByExtensionName` `:427-458` |
| 13 | macOS AV1 VideoToolbox probe | native `sysctlbyname()` P/Invoke, not ffmpeg | n/a | `ApplePlatformHelper.HasAv1HardwareAccel`, `:20-60` |

All #1–#12 run sequentially inside `SetFFmpegPath()` (`MediaEncoder.cs:218-281`). A wrapper standing in for ffmpeg
must answer all of these or Jellyfin refuses to start (`FfmpegException`, `ApplicationHost.cs:430-433`) or silently
disables hw features.

Version gate: `MinVersion=new Version(4,4)`, `MaxVersion=null` (`EncoderValidator.cs:211-213`). Parsed from
`^ffmpeg version n?((?:[0-9]+\.?)+)` on the first output line; falls back to library `.so` version table. The string
`"Libav developers"` triggers rejection (`:247-251`).

---

## 2. ffprobe path resolution and invocation

Derived from the ffmpeg path, not independently configured:

```csharp
// MediaEncoder.cs:220-221
_ffprobePath = FfprobePathRegex().Replace(_ffmpegPath, "ffprobe$1");
```
Regex `[^\/\\]+?(\.[^\/\\\n.]+)?$` (`:171-172`) replaces the last path component with `ffprobe` + same extension.
**No separate `EncoderProbePath` config knob exists.**

Real media probing via `GetMediaInfoInternal` (`MediaEncoder.cs:503-593`):
```
{probeArgs} -i {input} -threads {N} -v warning -print_format json -show_streams -show_chapters -show_format [-show_frames -only_first_vframe]
```
`RedirectStandardOutput=true` (JSON parsed via `System.Text.Json`); runs on every playback session start and every
library-scan item.

---

## 3. Transcode job lifecycle

### Process launch
`MediaBrowser.MediaEncoding/Transcoding/TranscodeManager.cs`, `StartFfMpeg` (`:371-541`). `ProcessStartInfo` at `:417-436`:

```csharp
WindowStyle = ProcessWindowStyle.Hidden,
CreateNoWindow = true,
UseShellExecute = false,
// RedirectStandardOutput = true,   <-- commented out, stdout NOT redirected
StandardErrorEncoding = Encoding.UTF8,
RedirectStandardError = true,
RedirectStandardInput = true,
FileName = _mediaEncoder.EncoderPath,
Arguments = commandLineArguments,
WorkingDirectory = workingDirectory ?? string.Empty,
ErrorDialog = false
```
**stdin redirected** (`q`/pause/resume keys), **stderr redirected** (progress + log file), **stdout NOT redirected**.
No `WorkingDirectory` on the main transcode path (`AttachmentExtractor` sets one, `AttachmentExtractor.cs:307`).
No custom environment variables are set for ffmpeg/ffprobe anywhere.

If subtitle burn-in needs fonts/attachments, `_attachmentExtractor.ExtractAllAttachments(...)` runs first
(`TranscodeManager.cs:398-415`) — a separate ffmpeg call (§7).

Logging: `FFmpeg.Transcode-`/`FFmpeg.Remux-`/`FFmpeg.DirectStream-` prefixed file under
`ApplicationPaths.LogDirectoryPath` (`:451-467`); `MediaSourceInfo` JSON + full command line written first, then
`JobLogger.StartStreamingLog` streams `process.StandardError` into it line-by-line (`:506`; `JobLogger.cs:23-60`).

After `process.Start()` (`:492`), the server **polls file existence** on `state.WaitForPath ?? outputPath` in a 100 ms
loop (`:509-516`) before returning. For progressive video an extra 1000–2500 ms sleep follows (`:518-526`).

### Stopping a job
`MediaBrowser.Controller/MediaEncoding/TranscodingJob.cs`, `Stop()` (`:260-291`):
```csharp
process!.StandardInput.WriteLine("q");   // graceful quit via stdin
if (!process.WaitForExit(5000)) {
    process.Kill();                       // hard kill after 5s grace
}
```
Only `InvalidOperationException` is caught — a null `Process` while `!HasExited` would NRE. `HasExited` is a plain
settable bool (`TranscodingJob.cs:82` on v10.11.0).

### Throttling — `TranscodingThrottler`
`MediaBrowser.Controller/MediaEncoding/TranscodingThrottler.cs` — pauses/resumes ffmpeg's own encode loop via
**stdin keystrokes**:
- Pause: `"p"` if `_mediaEncoder.IsPkeyPauseSupported`, else `"c"` (`:132`/`:138`).
- Resume: `"u"` if pkey-pause supported, else `Environment.NewLine` (`:61`/`:62`).

`IsPkeyPauseSupported` comes from startup probe #8. `p`/`u` is jellyfin-ffmpeg patch
`0028-add-pause-support-for-ffmpeg-cli.patch` (confirmed by the ffmpeg report). On stock ffmpeg the `c` fallback
is a no-op. `TranscodeManager.cs:546-551` also permits throttling when `EncoderVersion <= 6.1`.

Throttling gate (`EnableThrottling`, `TranscodeManager.cs:554-559`): local file input, video, runtime ≥ 5 min,
`VideoType.VideoFile`. 5 s timer (`:44-46`); `IsThrottleAllowed` (`:148-208`) compares
`transcodingPositionTicks - downloadPositionTicks` against `ThrottleDelaySeconds` (min 60 s) for HLS.

**Implication:** throttling/pause/quit are single-byte-on-stdin protocols. A remote-agent design that doesn't forward
stdin bytes to the actual remote ffmpeg process silently breaks pause/quit/throttle.

`TranscodingSegmentCleaner.cs` deletes old HLS segments on a timer when `EnableSegmentDeletion` is set — pure local
filesystem housekeeping.

---

## 4. Progress tracking

**stderr line parsing, not `-progress`.** Repo-wide grep for `-progress` found zero usages.

`JobLogger.StartStreamingLog` (`:23-60`) reads `process.StandardError` line-by-line, `ParseLogLine` (`:62-161`)
tokenizes on spaces for `fps=`, `time=` (→ percent via `state.RunTimeTicks`/`StartTimeTicks`), `size=...kB`,
`bitrate=...kbits/s`.

Chain: `state.ReportTranscodingProgress(...)` (`EncodingJobInfo.cs:730`, `StreamState.cs:152-155`) →
`_transcodeManager.ReportTranscodingProgress` (`TranscodeManager.cs:323-368`) → updates
`job.Framerate/CompletionPercentage/TranscodingPositionTicks/BytesTranscoded/BitRate` (feeds the throttler) and
`_sessionManager.ReportTranscodingInfo(deviceId, new TranscodingInfo{...})` → dashboard Now Playing panel and
`/Sessions` API `TranscodingInfo`.

HLS segment availability is **not** driven by progress — purely disk polling (§5).

---

## 5. HLS output: paths, filenames, serving

**Storage root:** `MediaBrowser.Common/Configuration/EncodingConfigurationExtensions.cs:29-40` (`GetTranscodePath`)
— `EncodingOptions.TranscodingTempPath` if set, else `<CachePath>/transcodes`.

**Filename pattern:** `Jellyfin.Api/Helpers/StreamingHelpers.cs:377-386` (`GetOutputFilePath`):
```csharp
var data = $"{state.MediaPath}-{state.UserAgent}-{deviceId}-{playSessionId}";
var filename = data.GetMD5().ToString("N", ...);   // 32-char hex MD5
return Path.Combine(folder, filename + ext);
```
Playlist = `Path.ChangeExtension(outputFilePath, ".m3u8")` (`DynamicHlsController.cs:1452`, `:298`). Segment path
(`GetSegmentPath`, `:1907-1913`) = `<folder>/<playlistFileNameNoExt><index><segExt>` e.g. `<md5>0.ts`. The
`-hls_segment_filename` given to ffmpeg is `<prefix>%d<ext>` (`:1589`, `:1650`); fMP4 init segment is
`<prefix>-1<ext>` (`:1602-1607`, `HlsHelpers.cs:81-104`).

**Command args** (`GetCommandLineArguments`, `DynamicHlsController.cs:1578-1652`, private):
```
-hls_playlist_type {event|vod} -hls_list_size 0 ... -f hls -max_delay 5000000
-hls_time {segLen} -hls_segment_type {mpegts|fmp4} -start_number {n}
-hls_segment_filename "<prefix>%d<ext>" ... -y "<playlistPath>"
```

**Serving: pure disk polling, no `FileSystemWatcher`.**
- `GetDynamicSegment` (`:1427-1544`)/`GetSegmentResult` (`:1915-1989`): if the segment file exists → serve.
- Whether to (re)start ffmpeg is decided by `GetCurrentTranscodingIndex` (`:2009-2030`), which reads **the
  newest-mtime file on disk matching the playlist prefix** (`GetLastTranscodingFile`, `:2032-2048`).
- Readiness while waiting (`:1944-1969`): 100 ms `Task.Delay` loop (`:1968`); ready when `File.Exists(segmentPath)`
  **and** (job exited **or** the *next* segment file exists) (`:1949-1958`).
- Delivery: `FileStreamResponseHelpers.GetStaticFileResult` → `new PhysicalFileResult(path, contentType){ EnableRangeProcessing=true }`
  (`:105-110`). **No code path serves segments from anything but local disk.**
- Live-TV `.m3u8` waiting: `HlsHelpers.WaitForMinimumSegmentCount` (`:27-72`) counts `#EXTINF:` lines in ffmpeg's playlist.
- Legacy `HlsSegmentController` also only reads `GetTranscodePath()` with a path-traversal guard (`:165-176`).

---

## 6. Local filesystem paths embedded in the ffmpeg command line (besides input/output)

All built in `MediaBrowser.Controller/MediaEncoding/EncodingHelper.cs`:

| What | Where built | Fragment |
|---|---|---|
| External subtitle file (extra input) | `GetInputArgument`, `:1272-1319` | `-i file:"<subtitlePath>"` (`.sub`→`.idx` swap, `:1278-1287`) |
| External audio file (extra input) | `:1321-1329` | `-i "<audioStream.Path>"` |
| Text-subtitle burn-in source | `GetTextSubtitlesFilter`, `:1953-1957` | `subtitles=f='<escaped path>'` |
| Fonts directory for ASS/SSA | `:1928-1935`, `_pathManager.GetAttachmentFolderPath(mediaSourceId)` = `<DataPath>/attachments/<id[:2]>/<id>` (`PathManager.cs:60-71`) | `subtitles=...:fontsdir='<attachmentFolder>'` |
| DVD/BluRay concat playlist | `:1260-1268`, `MediaEncoder.GenerateConcatConfig` (`MediaEncoder.cs:1313-1357`) → `<CachePath>/concat/<mediaSourceId>.concat` | `-f concat -safe 0 -i "<concatFilePath>"` |
| VAAPI render node | `GetVaapiDeviceArgs`, `:910-931`, default `/dev/dri/renderD128` (`EncodingOptions.cs:31`) | `-init_hw_device vaapi=va:/dev/dri/renderD128` |
| DRM render node | `GetDrmDeviceArgs`, `:935-945` | `-init_hw_device drm=dr:/dev/dri/renderD128` |
| QSV device (Linux) | `GetQsvDeviceArgs`, `:947-969` | chained via `@` alias |
| Attachment/font extraction output dir | `AttachmentExtractor.cs:130,264` | `-dump_attachment:<idx> "<path>"` |
| Extracted/converted subtitle output | `SubtitleEncoder.cs`, `<DataPath>/subtitles/<id[:2]>/<id>/<idx><ext>` (`PathManager.cs:73-88`) | output arg |
| Trickplay tile temp dir | `MediaEncoder.cs:1015-1017` | `-f image2 "<TempDirectory>/<guid>/%08d.jpg"` |
| Chapter/thumbnail temp file | `MediaEncoder.cs:692-693` | `-f image2 "<TempDirectory>/<guid>.jpg"` |

**Ruled out:** no tonemapping LUT/`.cube` files (built-in `zscale`/`tonemapx`/`tonemap_opencl|vaapi|cuda|videotoolbox`
with numeric params); `FallbackFontPath` is never passed to ffmpeg (only served to the web client); no custom
`ProcessStartInfo.EnvironmentVariables`; Vulkan/OpenCL device selection is index/vendor based, no paths.

---

## 7. Non-HLS outputs and separate ffmpeg invocations

### Progressive transcoding (`/Videos/{id}/stream`)
ffmpeg writes to a local file that the server streams while it grows — no stdout piping.
`EncodingHelper.GetProgressiveVideoFullCommandLine` (`:7646-7679`) ends with `-y "<outputPath>"`.
`FileStreamResponseHelpers.GetTranscodedFile` (`:123-168`) wraps the path in `ProgressiveFileStream`
(`MediaBrowser.Controller/Streaming/ProgressiveFileStream.cs`): `FileShare.ReadWrite`, 50 ms spin on short reads,
30 s timeout (`:175-181`). Live-TV static passthrough bypasses ffmpeg (`VideosController.cs:439-449`).

### Separate invocations a routing layer must decide about
1. **Media probing** — `GetMediaInfoInternal` (`MediaEncoder.cs:503-593`), every playback start + library scan.
2. **Thumbnail/embedded image extraction** — `ExtractVideoImage`/`ExtractImage` (`:607-831`), `-vframes 1 -f image2`.
3. **Chapter thumbnails** — `ChapterManager.cs:182`, one call per chapter.
4. **Trickplay tiles** — `TrickplayManager.cs:482` → `ExtractVideoImagesOnIntervalAccelerated` (`:834-972`), watched via JPEG count (`:1076-1110`).
5. **Attachment/font extraction** — `AttachmentExtractor.cs`: `-dump_attachment:t "" -y` or per-index, then `-t 0 -f null null`.
6. **Subtitle extraction/conversion** — `SubtitleEncoder.cs:823-844`: `-y -i {input} -copyts -map 0:{idx} -an -vn -c:s {codec} "{outputPath}"`, prepends `-nostdin`.
7. **Concat-list duration probing** — `GenerateConcatConfig` calls ffprobe per VOB/M2TS.

---

## 8. Input path: local file vs. URL

Decided by `MediaSourceInfo.Protocol` (`EncodingUtils.GetInputArgument`, `:14-32`, `:61-71`):
```csharp
if (protocol != MediaProtocol.File)
    return $"\"{inputFile}\"";              // straight URL, quoted, no "file:" prefix
if (path.Contains("://")) return $"\"{path}\"";
path = path.EscapeProcessArgument();
return $"{inputPrefix}:\"{path}\"";          // e.g. file:"..." or bluray:"..."
```
For `MediaProtocol.Http`/`Rtsp`/`Rtp` (live TV tuners, `.strm`) the input is the quoted URL. `.strm` targets are
restricted server-side to `http`/`https`/`rtsp`/`rtp` (`ProbeProvider.cs:316-339`); `BaseItem.cs:1237-1249` sets
`info.Protocol = shortcutProtocol`, `IsRemote = true`. DVD/BluRay go through `GetPrimaryPlaylistVobFiles`/`M2tsFiles`
(`MediaEncoder.cs:474-497`, `:1298-1310`).

---

## What the agent did NOT verify

- Whether `p`/`u` pause is jellyfin-ffmpeg-only vs stock — inferred from the probe; **since confirmed** by the ffmpeg
  report (patch `0028-add-pause-support-for-ffmpeg-cli.patch`).
- Runtime behaviour: static source reading only; no binary run.
- `AudioController`, `DlnaServerController`, Live TV tuner internals, Windows-specific branches — not traced.
- Absence of `-progress` is based on a repo-wide literal grep.
