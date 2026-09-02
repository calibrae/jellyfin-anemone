# jellyfin-anemone — research notes

Date: 2026-08-28. Target: Jellyfin **10.11.0** on speedwagon (M1 mini, macOS), ~40 users.
Goal: a Jellyfin plugin that offloads live ffmpeg transcodes to agents on other machines,
which stream the result back to the server. No shared transcode dir, no SSH, no wrapper binary.

Detailed agent reports (file:line citations, experiments) live in `research/`:
`jellyfin-internals.md`, `plugin-hooks.md`, `ffmpeg-network-io.md`, `prior-art.md`.

---

## TL;DR

- **Hook = a plugin that DI-overrides `ITranscodeManager`.** Plugins register services *after*
  core (`ApplicationHost.cs:460` then `:462` on the v10.11.0 tag; core `ITranscodeManager`
  at `:575`), last registration wins, nothing resolves the concrete class. `StartFfMpeg(state,
  outputPath, commandLineArguments, …)` receives the fully built ffmpeg command line and returns
  a `TranscodingJob` — that one method is the seam. Everything upstream (arg building, HLS
  playlist, sessions) stays untouched.
- **Wrapper-binary approach is a non-starter on this box**: the Mac launcher always passes
  `--ffmpeg <bundled>` and CLI beats `JELLYFIN_FFMPEG` (`Program.cs:340-341`); the app bundle is
  signed + hardened runtime, so swapping the binary breaks the seal.
- **Output path = ffmpeg pushes HLS segments itself with `-f hls -method PUT`** to the plugin's
  own listener (port 8097 — *not* Jellyfin's port; see PROTOCOL.md), verified live: one chunked PUT per segment, `-headers` on every request,
  `-http_persistent 1` keep-alive. Receiver writes `.part` + rename → atomic files at the exact
  `<md5>%d.ts` paths Jellyfin polls for. Jellyfin serves segment N once N+1 exists — unchanged.
- **Input path = agent mounts the same SMB share at the same path** (`/Volumes/data` from
  polnareff). Media never transits speedwagon. HTTP input from Jellyfin (`/Videos/{id}/stream?static=true`)
  is a verified fallback (Range seek works, 2 requests per `-ss`).
- **Control = one WebSocket per agent, on the plugin's own port** (Jellyfin hijacks every upgrade on its own port) carrying job spec, stdin bytes (`q`/`p`/`u`), stderr
  lines back, exit code, heartbeat. Connection liveness drives job teardown (ClusterPlex lesson).
- **Agent = Rust daemon running the official jellyfin-ffmpeg portable build** (`macarm64-gpl`).
  Stock ffmpeg silently no-ops Jellyfin's throttle (`p`/`u` is jellyfin-ffmpeg patch 0028).
- **v0 = speedwagon + trish (both M1/VideoToolbox → identical args, only paths rewritten).**
  giorno next. Linux/NVENC agents later — needs arg *regeneration*, not rewriting (every prior
  project that tried argv-mangling gave up; Jellyfin core says the same in jellyfin-meta #36).
- Nobody has built this. rffmpeg/ClusterPlex/kube-plex all need shared storage; Jellyfin core
  sketched exactly this shape (task API, node-local input mount, node pushes its own output) in
  2023 and never implemented it.

---

## 1. Context — this homelab

| Host | HW | Role today | Link to speedwagon |
|---|---|---|---|
| speedwagon 10.10.0.2 | M1 16 GB | Jellyfin 10.11.0, `Jellyfin.app`, jellyfin-ffmpeg 7.1.2, VideoToolbox | — |
| trish 10.10.0.8 | M1 16 GB, fresh, no brew | idle spare compute | **TB4 37.7 Gbit/s** (10.240.0.1 ↔ .2) + 1 GbE |
| giorno 10.10.0.13 | M4 Pro 64 GB | Whisper/LLM | 1 GbE (TB5 ports, not cabled) |
| doppio 10.10.0.12 | Fedora, RTX 4070 (→ mira VM) | staging, idle-shutdown | 1 GbE |
| polnareff 10.10.0.7 | R86S + 22 TB DAS | media via SMB/NFS `/mnt/das/data` | 1 GbE |

Observed on speedwagon (`encoding.xml`, today's `FFmpeg.Transcode-*.log`):
- `HardwareAccelerationType=videotoolbox`, HEVC encode on, **throttling off**, segment deletion off.
- Transcode dir: `~/Library/Application Support/jellyfin/cache/transcodes`.
- Real transcode command (1080p H265 → h264_videotoolbox, HLS mpegts, 3 s segments):
  ```
  ffmpeg -analyzeduration 200M -probesize 1G -f matroska -init_hw_device videotoolbox=vt
    -hwaccel videotoolbox -hwaccel_output_format videotoolbox_vld -noautorotate
    -i file:"/Volumes/data/_tvshows/…/….mkv" -noautoscale -map_metadata -1 -map_chapters -1
    -threads 0 -map 0:0 -map 0:1 -map -0:s -codec:v:0 h264_videotoolbox -prio_speed 1
    -b:v 6671258 … -force_key_frames:0 "expr:gte(t,n_forced*3)" -g:v:0 72 -keyint_min:v:0 72
    -vf "scale_vt=w=1280:h=640:format=nv12" -codec:a:0 aac_at -ac 2 -ab 256000 -af "volume=2"
    -copyts -avoid_negative_ts disabled -max_muxing_queue_size 2048
    -f hls -max_delay 5000000 -hls_time 3 -hls_segment_type mpegts -start_number 0
    -hls_segment_filename "<transcodes>/<md5>%d.ts" -hls_playlist_type vod -hls_list_size 0
    -y "<transcodes>/<md5>.m3u8"
  ```
  DirectStream (remux) variant uses `-hls_segment_type fmp4 -hls_fmp4_init_filename "<md5>-1.mp4"`
  and `-start_number 457` after a seek. No `-progress`, no env vars, no LUT files.
- Plugins are plain folders: `plugins/<Name>_<version>/` (AniDB, OpenSubtitles, TVDB, Webhook).
- No `dotnet` SDK on speedwagon → build on CI (giorno/doppio) or `brew install dotnet-sdk`.

## 2. How Jellyfin 10.11 transcodes (verified from source)

| Fact | Where |
|---|---|
| ffmpeg path precedence: `--ffmpeg` CLI > `JELLYFIN_FFMPEG` > `EncoderAppPath` (dashboard) > `$PATH` | `MediaEncoder.SetFFmpegPath`, `Program.cs:337-341` |
| Validation runs **once per process**: `-version`, `-decoders`, `-encoders`, `-filters`, `-hwaccels`, `-h filter=…`, `-h bsf=…`, a real lavfi encode with `?` on stdin (detects `p`/`u` pause keys), `-hwaccel_flags +low_priority` probe, ffprobe `-only_first_vframe` probe | `EncoderValidator.cs`, `MediaEncoder.cs:218-281` |
| ffprobe path = ffmpeg path with last component swapped — no separate knob | `MediaEncoder.cs:220` |
| Process launch: stdin **redirected**, stderr **redirected**, stdout **not**; no env, no cwd | `TranscodeManager.cs:417-436` |
| Stop = write `q` to stdin, wait 5 s, `Kill()` | `TranscodingJob.Stop()` |
| Throttle = write `p`/`u` (jellyfin-ffmpeg) or `c`/newline (stock, no-op) to stdin every 5 s check | `TranscodingThrottler.cs:62,138` |
| Progress = parse stderr lines (`fps=`, `time=`, `size=`, `bitrate=`) → `ReportTranscodingProgress` → dashboard + throttler | `JobLogger.cs:62-161` |
| Output file name = MD5(`{MediaPath}-{UserAgent}-{deviceId}-{playSessionId}`); segments `<md5>%d.ts`, fmp4 init `<md5>-1.mp4` | `StreamingHelpers.cs:377-386`, `DynamicHlsController.cs:1907` |
| After start, server polls (100 ms) until the output path exists before returning | `TranscodeManager.cs:509-516` |
| Segment readiness: `File.Exists(N) && (job.HasExited \|\| File.Exists(N+1))`, 100 ms polling, served via `PhysicalFileResult` — **local disk only** | `DynamicHlsController.cs:1944-1969` |
| "Current transcoding index" = newest-mtime file with the playlist prefix | `DynamicHlsController.cs:2032-2048` |
| Seek to a segment not on disk → kills job, restarts ffmpeg with `-ss` + `-start_number N` (natural failover point) | `DynamicHlsController.GetDynamicSegment` |
| Input arg: `file:"path"` for local, bare quoted URL for http/rtsp (live TV, .strm) | `EncodingUtils.GetInputArgument` |
| Other local paths in args: external subs `-i file:"…"`, `subtitles=f='…':fontsdir='<data>/attachments/…'`, `-f concat` lists, `/dev/dri/renderD128` (Linux) | `EncodingHelper.cs` (see internals report §6) |
| Separate ffmpeg/ffprobe uses: probing, thumbnails, chapter images, trickplay, attachment extraction, subtitle extraction, progressive (non-HLS) transcode writes a growing file | internals report §7 |

## 3. Hooking Jellyfin: plugin, not wrapper

**Plugin (chosen).**
- `IPluginServiceRegistrator.RegisterServices(IServiceCollection, IServerApplicationHost)` runs
  after core registrations (verified on the v10.11.0 tag). `services.AddSingleton<ITranscodeManager, AnemoneTranscodeManager>()` wins.
- `ITranscodeManager` has 10 members. Core `TranscodeManager` is `public sealed`, 757 lines, and
  its job registry is private → **fork it into the plugin** (GPL-2, fine) and change `StartFfMpeg`
  to route. All ctor deps are public interfaces in the `Jellyfin.Controller` NuGet
  (`ILoggerFactory, IFileSystem, IApplicationPaths, IServerConfigurationManager, IUserManager,
  ISessionManager, IMediaEncoder, IMediaSourceManager, IAttachmentExtractor`). Uses
  `Jellyfin.Data` + `Jellyfin.Database.Implementations.Enums` (both on NuGet).
- Remote jobs: `TranscodingJob` is sealed, `Process` is nullable, `HasExited` is a plain settable
  bool. Only two things dereference `Process!`: `TranscodingJob.Stop()` and `TranscodingThrottler`.
  Both are invoked only by the manager → the fork never calls them for remote jobs (sends
  control messages instead; attaches no throttler in v0).
- Progress: `JobLogger.StartStreamingLog(state, Stream, Stream)` is public — feed it a pipe fed
  by the WebSocket's stderr lines and the dashboard/progress path is unchanged.
- HTTP endpoints: any `ControllerBase` in the plugin assembly is auto-discovered
  (`ApplicationHost.GetApiPluginAssemblies`). Kestrel body limit is the 30 MB default (Jellyfin
  never raises it) → `[DisableRequestSizeLimit]` + stream `Request.Body` to disk.
- Startup code: `services.AddHostedService<T>()` (`IServerEntryPoint` is gone).
- API key for HTTP-input fallback: `IAuthenticationManager.CreateApiKey(name)` (scoped service;
  returns void, fetch it back via `GetApiKeys()`).
- Host facts: `IConfigurationManager.GetTranscodePath()`, `.GetEncodingOptions()`,
  `IServerApplicationHost.GetApiUrlForLocalAccess()` / `GetSmartApiUrl()`.
- Packaging: `plugins/<Name>_<ver>/` folder with DLL (+ optional `meta.json`); config page = embedded
  HTML via `IHasWebPages`; config = `BasePluginConfiguration` XML.
- Versions: `Jellyfin.Controller` 10.11.11 is the latest 10.11 on NuGet (net9.0); 12.0.0 is at rc6
  (net10.0). The seam (`StartFfMpeg` signature, DI order) is identical on master today, but this
  rides on an emergent DI property, not a documented extension point — **pin per Jellyfin minor,
  rebuild on upgrade.**

**Wrapper binary (rejected for this box, keep as plan B for Linux installs).** Requires bypassing
the Mac launcher or resigning the bundle; must answer all 13 startup probes; must relay stdin/stderr;
ffprobe must sit next to it. rffmpeg's model — and its whole issue tracker.

## 4. ffmpeg over the network (live-tested, ffmpeg 8.0.1; source read in hlsenc.c/http.c)

- `-f hls -method PUT -hls_segment_filename http://…/%d.ts http://…/x.m3u8`: one **chunked**
  PUT per segment (no Content-Length, even though the segment is fully buffered in memory first).
  With Jellyfin's `-hls_playlist_type vod`, the playlist is PUT once at the end — irrelevant,
  Jellyfin builds its own playlist.
- fmp4: init segment PUT once up front; `-hls_fmp4_init_filename` resolves relative to the playlist URL.
- `-headers $'Authorization: Bearer …\r\n'` is sent on every PUT. `-http_persistent 1` reuses
  one TCP connection (off by default — turn it on).
- `-hls_flags temp_file` is **silently ignored over HTTP** (rename needs the `file` protocol) →
  atomicity is the receiver's job.
- ffmpeg **never reads the HTTP status of a PUT** (only the transport is checked): a 500 is
  invisible to it. Connection refused/reset is fatal by default (good: fail fast → fall back local).
  `-reconnect*` options are input-only.
- `-tls_verify` **never reaches the HLS muxer's uploads** (`set_http_options()` forwards only
  method/user_agent/multiple_requests/timeout/headers) → self-signed HTTPS gives zero protection.
  Use plain HTTP on the LAN/TB link with a per-job token, or put TLS on the agent↔plugin control
  channel and keep ffmpeg's PUT local to the agent (proxy) if it ever needs to cross an untrusted net.
- `-f segment` is a trap: opens each segment with a NULL options dict → always POST, drops headers.
- HTTP input: `-ss` before `-i` uses Range (2 requests for MKV/faststart MP4, 4 for moov-at-end MP4).
  `-reconnect 1 -reconnect_streamed 1 -reconnect_on_network_error 1` resumes at the exact byte.
- `-progress http://…` = one long-lived chunked POST, key=value blocks every `-stats_period`.
  Not needed: Jellyfin parses stderr and we relay stderr anyway.
- Segments only cut on keyframes — Jellyfin's commands already pass `-force_key_frames`/`-g`.
- jellyfin-ffmpeg: 98 patches; official portable builds for linux64/linuxarm64/**mac64/macarm64**/win64/winarm64,
  latest v7.1.4-3. Agent must run it for `p`/`u` pause, `tonemapx`, `scale_vt` etc. Fingerprint
  `ffmpeg -version` on registration and refuse mismatched major.minor.

## 5. Prior art (details + 17 verified issue links in `research/prior-art.md`)

| Project | Model | Needs | Dies on |
|---|---|---|---|
| rffmpeg (1068★, active) | Python argv wrapper → `ssh -t host ffmpeg …`, SQLite host DB | NFS for media **and** transcode dir at identical paths; same jellyfin-ffmpeg; same HWA everywhere | pause/resume over SSH PTY (#76), no kill propagation (#89), SQLite locks at 4+ simultaneous starts, NFS `actimeo` 15-60 s segment lag, startup probe (#94) |
| ClusterPlex (587★, active) | Node shim replaces "Plex Transcoder", Socket.IO orchestrator → worker runs real binary | RWX shared storage for media + `/transcode` | Plex SIGKILLs the shim → fixed with explicit `worker.task.kill` RPC on socket disconnect |
| UnicornTranscoder | Worker becomes the client-facing origin (HTTP 307 redirect) | none shared, but exposes workers to clients | unmaintained |
| kube-plex | pod per transcode | shared PVC | Plex changed the invocation contract → dead |
| Transcodarr (2026-01) | rffmpeg installer for Synology + Apple Silicon workers | rffmpeg's | rffmpeg's; first VideoToolbox worker precedent |
| jellyfin-ha (ZoltyMat, 22★, 2026) | **Server fork**: N Jellyfin replicas, Redis-leased transcode sessions, pod takeover resumes HLS | RWX shared transcode volume (pods read each other's segments) | not distribution — HA of identical pods, each runs its own ffmpeg |
| jellyfin-meta #36 (2023→2026-05, open) | Jellyfin core (gnattu): task API not argv, node-local media mount, node exposes its own output | — | never implemented |

Lessons we adopt:
1. Kill is a control-plane message keyed by job id, driven by connection liveness — never signal forwarding.
2. Forward stdin bytes verbatim (`q`/`p`/`u`), unbuffered, or pause silently breaks.
3. Same jellyfin-ffmpeg build on every node; verify, don't hope.
4. Whole session → one worker. Never split a file across workers (rejected twice upstream: VRAM, keyframe artifacts).
5. Local transcoding stays the permanent fallback, not a rollout crutch.
6. Don't mangle argv for a different HWA vendor. Homogeneous nodes first; heterogeneous = regenerate.
7. Load-balance on real capacity (HW encode sessions in use), not job count.

## 6. Recommended architecture

```
                    speedwagon (Jellyfin 10.11 + plugin)                    trish / giorno (agent, Rust)
 client ──HLS──▶ DynamicHlsController                                  ┌──────────────────────────────┐
                    │ builds ffmpeg args (unchanged)                    │ polyp (LaunchDaemon)     │
                    ▼                                                   │  ├ registers: caps, ffmpeg   │
              AnemoneTranscodeManager  ══ WebSocket (control) ═════════▶│  │   fingerprint, capacity   │
              (fork of TranscodeManager)   job{id,args',token}          │  ├ spawns jellyfin-ffmpeg    │
                    │  ◀───── stderr lines, exit, heartbeat ────────────│  │   stdin ◀ q/p/u  stderr ▶ │
                    │  ─────▶ stdin bytes (q/p/u), kill{id} ───────────▶│  └ kills jobs on disconnect  │
                    │                                                   └──────────────┬───────────────┘
              /anemone/ingest/{job}/{name}  ◀══ HTTP PUT (chunked, Bearer) ═══ ffmpeg -f hls -method PUT
                    │ .part + rename → <transcodes>/<md5>N.ts                          │
                    ▼                                                                  ▼ reads
              local transcodes dir  ◀── Jellyfin polls/serves as today      /Volumes/data (SMB from polnareff)
```

**Job flow**
1. `StartFfMpeg(state, outputPath, args)` → scheduler picks an agent with free capacity (else local: run
   the upstream code path verbatim).
2. Rewrite args: `-hls_segment_filename "<dir>/<md5>%d.ts"` → `http://10.240.0.1:8096/anemone/ingest/<job>/%d.ts`;
   playlist → `…/<job>/playlist.m3u8` (receiver discards or stores); prepend `-method PUT -http_persistent 1
   -headers "Authorization: Bearer <token>\r\n"`. Input path unchanged (same mount) or → 
   `http://server/Videos/{itemId}/stream?static=true&mediaSourceId=…&api_key=…` if the agent reports no mount.
3. Send job over the agent's WebSocket; create `TranscodingJob{Process=null}` and register it exactly like upstream
   (kill timer, ping, session reporting). Pipe stderr lines into `JobLogger` → progress/dashboard unchanged.
4. Ingest endpoint validates token→job, restricts names to `^-?\d+\.(ts|mp4|m4s)$`, streams body to
   `<md5><n>.<ext>.part`, renames on completion. Jellyfin's existing readiness rule (N+1 exists) does the rest.
5. Stop/seek: manager sends `q` (then `kill{id}` after 5 s), marks `HasExited`. Agent disconnect → all its jobs
   marked exited; Jellyfin's controller restarts the missing segment on next request → rescheduled elsewhere.
6. Agent fails **before the first segment lands** → transparent local restart of the same job.

**Scheduling v0**: each agent advertises `max_sessions` (VideoToolbox realistic: 3-4 × 1080p on M1, more on
M4 Pro) and `active`; pick least `active/max`; server-local counts as an agent with its own cap; optional
"prefer remote" so speedwagon stays responsive for the 40 users.

**Security v0**: control channel = WebSocket on the plugin's endpoint, shared secret from Vault (`infra/anemone`);
ingest = per-job 256-bit bearer token, LAN/TB only, no path traversal; HTTP-input fallback uses a plugin-minted
API key. Plain HTTP is acceptable inside the LAN because ffmpeg can't verify TLS here anyway.

**Throttling**: off on speedwagon today; v0 skips it for remote jobs. v1: fork `TranscodingThrottler`
(~100 lines) to emit `p`/`u` over the control channel — the agent writes them to ffmpeg's stdin.

## 7. What stays local in v0

ffprobe/media probing, thumbnails, chapter images, trickplay, attachment + subtitle extraction, progressive
(non-HLS) transcodes, live TV, anything with external subtitle inputs or `subtitles=…:fontsdir=` burn-in
(needs the server's `attachments/` dir; ship-the-files is v1), DVD/BluRay concat inputs. Detected by
inspecting `state` / the args → routed to the upstream local path.

## 8. Risks / open questions

- **Interface churn**: `ITranscodeManager`/`StreamState`/`TranscodingJob` are internal-ish; 12.0 is at rc6.
  Mitigation: one plugin build per Jellyfin minor; CI matrix.
- **`TranscodingJob.Process == null` consumers** beyond `Stop()`/throttler — the audit covered
  `MediaBrowser.Controller`, `MediaBrowser.MediaEncoding`, `Jellyfin.Api`, `Emby.Server.Implementations`;
  Live TV / DLNA not traced.
- **Kestrel + chunked PUT + `[DisableRequestSizeLimit]`** — standard ASP.NET, but untested against a plugin
  controller inside Jellyfin's pipeline (auth middleware, `Policies`). Spike #1.
- **VideoToolbox from a LaunchDaemon on trish** (FileVault on, no autologin) — Plex does it, but verify.
- **Mismatched hardware** (doppio/NVENC): not argv rewriting. Either regenerate via `EncodingHelper` with a
  per-agent `EncodingOptions` (possible: `EncodingHelper` is public and takes `EncodingOptions` per call, but
  `DynamicHlsController.GetCommandLineArguments` is private → fork that too), or accept macOS-only agents.
- **SMB mount parity on agents**: same `/Volumes/data` path; reuse speedwagon's `smb-mount.sh` LaunchAgent
  pattern; agent reports mount health as a capability.
- **Segment readiness latency** unchanged (N+1 rule), but the first segment now waits for PUT completion;
  over TB this is negligible; measure over 1 GbE for giorno.
- Server on **10.11.0**; 10.11.11 exists — upgrade before building so the NuGet and the binary match.

## 9. Spikes before building (in order, each ≤ 1 evening)

1. **Ingest endpoint in a stub plugin**: `[DisableRequestSizeLimit]` PUT that writes `.part`+rename; drive it
   with `ffmpeg -f lavfi … -f hls -method PUT -http_persistent 1 -headers …` from trish over the TB link.
   Pass = files appear atomically, Jellyfin serves a hand-started playlist.
2. **DI override on 10.11.0**: plugin registering a *verbatim* fork of `TranscodeManager` as
   `ITranscodeManager`; playback must behave identically. Pass = log line from the fork on every transcode.
3. **Manual remote transcode**: take today's real command from the log, rewrite output to the spike-1 endpoint,
   run it on trish with jellyfin-ffmpeg macarm64 reading `/Volumes/data` over SMB; play it in a client.
   Measure start latency + segment cadence vs local.
4. **stdin relay**: WebSocket carrying `q`/`p`/`u` to a remote jellyfin-ffmpeg; confirm pause/resume/quit
   semantics and the 5 s kill path.
5. **VideoToolbox from a root LaunchDaemon on trish** (`ffmpeg -init_hw_device videotoolbox … h264_videotoolbox`).

## 10. Proposed shape

```
jellyfin-anemone/
├── plugin/        C# net9.0, Jellyfin.Controller 10.11.x  — Jellyfin.Plugin.Anemone
│   ├── AnemoneTranscodeManager.cs   (fork of TranscodeManager + routing)
│   ├── Agents/   registry, WebSocket hub, scheduler
│   ├── Ingest/   IngestController (PUT segments), token store
│   └── Configuration/ config page (agents, secret, prefer-remote, caps)
├── agent/         Rust — polyp: WS client, ffmpeg supervisor, capability probe, LaunchDaemon (SMAppService, fucina pattern)
├── research/      the four reports
└── .gitea/workflows/ci.yaml   (dotnet on giorno/doppio, cargo on macos-arm64)
```

Wire protocol: JSON over WebSocket (`hello`, `job`, `stdin`, `stderr`, `exit`, `kill`, `ping`) — small
enough that MQTT/NATS would be overkill. Segments never touch the control channel.
