# anemone wire protocol v1

Two channels between the Jellyfin plugin ("server") and each `polyp` ("agent"):

1. **Control** — one WebSocket per agent, opened *by the agent* to
   `ws://<server>:<AgentListenPort>/Anemone/agents/ws` (default port 8097), upgrade request carrying
   `Authorization: Bearer <shared secret>`.
   Text frames, one JSON object per frame, `type` discriminator. Either side may send `ping`; the peer answers `pong`.
2. **Ingest** — ffmpeg on the agent uploads HLS output with `HTTP PUT` to
   `<ingest_base>/Anemone/ingest/<job_id>/<filename>` (`ingest_base` is the same listener, from `welcome`) carrying `Authorization: Bearer <job token>`
   (chunked transfer, one PUT per segment; `-http_persistent 1`). The server writes `<filename>.part` in the job's
   target directory and renames on completion. Filenames are validated against the job's prefix.

All ids are opaque strings (the server uses GUID-N). Timestamps are RFC 3339. Unknown fields are ignored;
unknown `type` values are logged and ignored.

## Agent → Server

| type | fields | notes |
|---|---|---|
| `hello` | `name`, `version`, `platform` (`macos-arm64`, `linux-x86_64`, …), `ffmpeg: {path, version, hwaccels[], encoders[], decoders[], filters[], pause_keys?}`, `mounts: [{path, ok, server_path?}]`, `max_sessions`, `hwaccel?`, `hwaccel_device?` | first frame after connect; server answers `welcome` or `reject` and closes |
| `status` | `active` (int), `load` (0..1, optional), `mounts` (optional refresh, same shape as in `hello`) | sent on change and at least every 10 s (doubles as heartbeat) |
| `started` | `id`, `pid` | ffmpeg spawned |
| `stderr` | `id`, `line` | one frame per stderr line, verbatim, no trailing newline; ffmpeg progress lines (`frame=… time=…`) included — the server feeds them to Jellyfin's log/progress parser |
| `exit` | `id`, `code` (int, -1 if killed by signal), `error` (string, optional) | terminal; agent forgets the job |
| `error` | `id` (optional), `message` | non-fatal agent-side problem (e.g. spawn failure → also followed by `exit` with code -1) |
| `pong` | — | |

## Server → Agent

| type | fields | notes |
|---|---|---|
| `welcome` | `server: {version, ffmpeg_version}`, `ingest_base` (absolute URL), `ping_interval_s` | |
| `reject` | `reason` | then close |
| `job` | `id`, `argv[]` (already split, already rewritten; `argv[0]` is **not** included — the agent prepends its ffmpeg path), `token` (ingest bearer), `label` (for logs), `env` (object, optional) | agent must reply `started` or `exit` |
| `stdin` | `id`, `data` | raw bytes to write to ffmpeg stdin, **unbuffered, immediately**. Jellyfin sends `q` (quit), `p` (pause), `u` (resume). No newline is added by the agent; the server includes `\n` when Jellyfin's `WriteLine` did |
| `kill` | `id` | SIGKILL the ffmpeg process now; `exit` must still follow |
| `ping` | — | |

## Lifecycle rules

- Server closes the socket or loses it → agent **kills every job** it received on that connection, then reconnects
  with backoff (1 s → 30 s). Job liveness is control-connection liveness.
- Agent socket lost → server marks all its jobs exited (`code -1`), revokes their ingest tokens; Jellyfin's
  controller will restart the missing segment on the next client request and the job gets rescheduled.
- `stdin q` then no `exit` within 5 s → server sends `kill`.
- Agent must not accept more than `max_sessions` concurrent jobs; over-capacity `job` → `exit` with `code -2`,
  `error: "capacity"`.
- Ingest PUT with an unknown job / bad token / bad filename → `403`/`404`; ffmpeg ignores HTTP status, so the
  server also drops the connection to make the failure visible on the agent side.
- Valid filenames: `<prefix>` followed by `-?[0-9]+` and `.ts|.mp4|.m4s`, or `<prefix>.m3u8`, where `<prefix>`
  is the job's playlist basename (Jellyfin's MD5). Anything else is rejected.

## Argument rewriting (server side, before `job`)

Input: Jellyfin's single `commandLineArguments` string. It is split into argv with .NET's Unix rules
(`ProcessStartInfo.Arguments` parsing: double quotes group, backslash escapes only before a quote or backslash).
Then, on the argv list:

1. `-hls_segment_filename <dir>/<prefix>%d<ext>` → `<ingest_base>/Anemone/ingest/<id>/<prefix>%d<ext>`
2. last element (`<dir>/<prefix>.m3u8`) → `<ingest_base>/Anemone/ingest/<id>/<prefix>.m3u8`
3. insert before the last element: `-method PUT -http_persistent 1 -headers "Authorization: Bearer <token>\r\n"`
   (as three separate argv pairs; the header value contains literal CR LF)
4. `-i file:<path>` stays as is when the agent reports a mount covering `<path>`; otherwise the job is not
   routed to that agent (HTTP input fallback is a v1 feature, see RESEARCH.md)
5. Any of the following → job stays local: no `-f hls`; more than one `-i`; `subtitles=`/`fontsdir=`/`-f concat`
   in argv; `-hwaccel X`/`-init_hw_device X=` with `X` not in the agent's `hwaccels`; a `-codec:v:*` encoder not in the
   agent's `encoders`; the agent's ffmpeg major.minor differs from the server's.

## Example

```
→ {"type":"hello","name":"trish","version":"0.1.0","platform":"macos-arm64",
    "ffmpeg":{"path":"/opt/anemone/ffmpeg","version":"7.1.2-Jellyfin","hwaccels":["videotoolbox"],
              "encoders":["h264_videotoolbox","hevc_videotoolbox","aac_at","libx264"],"decoders":["h264","hevc"],
              "filters":["scale_vt","scale","overlay"]},
    "mounts":[{"path":"/Volumes/data","ok":true}],"max_sessions":3}
← {"type":"welcome","server":{"version":"10.11.0","ffmpeg_version":"7.1.2-Jellyfin"},
    "ingest_base":"http://10.240.0.1:8096","ping_interval_s":10}
← {"type":"job","id":"5f1c…","argv":["-analyzeduration","200M",…,"-hls_segment_filename",
    "http://10.240.0.1:8096/Anemone/ingest/5f1c…/a7858c…%d.ts",…,"-method","PUT","-http_persistent","1",
    "-headers","Authorization: Bearer 9kQ…\r\n","-y","http://10.240.0.1:8096/Anemone/ingest/5f1c…/a7858c….m3u8"],
    "token":"9kQ…","label":"Transcode a7858c…"}
→ {"type":"started","id":"5f1c…","pid":4242}
→ {"type":"stderr","id":"5f1c…","line":"frame=  120 fps= 60 q=-0.0 size=    1024KiB time=00:00:04.00 bitrate=2097.2kbits/s speed=2.0x"}
← {"type":"stdin","id":"5f1c…","data":"q\n"}
→ {"type":"exit","id":"5f1c…","code":0}
```

## Note on the playlist (verified 2026-08-28)

Jellyfin starts the first ffmpeg for a session with `-hls_playlist_type event` and waits for the **playlist** to
exist before answering the client; ffmpeg rewrites an *event* playlist after every segment. Seek-restarts use
`-hls_playlist_type vod` and Jellyfin then waits for the requested **segment** (`state.WaitForPath`); a *vod*
playlist is only written when ffmpeg finishes. Consequence: the ingest endpoint must store the `.m3u8` PUTs too, not
just segments — otherwise the initial start hangs.


## Why both channels live on the plugin's own port (verified live 2026-09-02)

Neither channel can be served from Jellyfin's own HTTP port:

- **WebSocket.** `Jellyfin.Server/Startup.cs:221` (10.11.0) calls `UseWebSocketHandler()` *before*
  `UseEndpoints/MapControllers` (:230), and that middleware claims **every** upgrade request regardless of
  path, answering anything without a Jellyfin API token with `403 "Token is required"`. A plugin
  `ControllerBase` therefore never sees an agent upgrade — measured: a plain `GET` with our bearer reaches
  the controller (400), the same request with `Upgrade: websocket` gets 403. `IStartupFilter` does not help
  either: plugin services are registered too late to affect the pipeline (the filter's `Configure` is never
  invoked).
- **Ingest.** Jellyfin never raises Kestrel's 30 MB request-body cap and runs its auth middleware over
  everything hosted in-process.

So the plugin runs its own Kestrel (`AnemoneListener`, `AgentListenPort`, default 8097) with
`MaxRequestBodySize = null`. `IngestBaseUrl` must point at that port.


---

# Protocol v2 additions (2026-09-02)

Both are backward compatible: an agent that omits the new fields behaves exactly as before.

## Path mapping — `mounts[].server_path`

The media tree rarely has the same path everywhere: the server may see `/Volumes/data` (SMB) while a
Linux agent NFS-mounts the same tree at `/mnt/media`. Each mount entry therefore carries two paths:

| field | meaning |
|---|---|
| `path` | where the tree lives **on the agent** — what its ffmpeg must open |
| `local` | `true` when that tree is on storage attached to the agent, so reading a source costs no network round trip. Optional; omit when unknown. Placement prefers a local-media agent, because reading the source is usually the larger transfer — the segments it sends back are already compressed |
| `server_path` | what the **Jellyfin server** calls the same tree. Optional; defaults to `path` (identical layout) |
| `ok` | the agent could actually read it (see the probe timeout note below) |

Placement compares the job's input paths (always server-side) against `server_path`, matching on a
path-segment boundary: `server_path` `/Volumes/data` covers `/Volumes/data/x.mkv` but never
`/Volumes/database/x.mkv`. Rewriting then swaps that prefix for `path`, so
`-i file:/Volumes/data/s/e.mkv` becomes `-i file:/mnt/media/s/e.mkv`. The longest matching
`server_path` wins when several overlap. Only the input side is mapped — output already goes to the
ingest URL.

## Hardware acceleration — `hwaccel` and `hwaccel_device`

Jellyfin builds the ffmpeg command line for **the server's own** hardware (here: VideoToolbox on
macOS). Shipping that verbatim to a Linux/VAAPI box would fail, so the plugin translates the
hardware-specific parts of the command line for the agent that will run it.

| field | meaning |
|---|---|
| `hwaccel` | the profile the agent wants its jobs built for: `videotoolbox`, `nvenc`, `qsv`, `vaapi`, `amf`, `rkmpp`, or `none` (software). Optional — the agent auto-detects when unset, and the server falls back to inferring from `ffmpeg.hwaccels` + `platform` |
| `hwaccel_device` | device the profile needs, e.g. `/dev/dri/renderD128` for VAAPI/QSV on Linux. Optional |

`none` is a valid, useful answer: a fast CPU with no usable GPU still helps, it just gets `libx264`.

The server only ever *narrows* what it was given — it never invents filters Jellyfin did not ask for.
When a command line uses anything the translator does not fully understand (subtitle burn-in,
`-filter_complex`, tonemapping, an unrecognised filter), the job is **not** sent to an agent needing
translation; it runs locally or on an agent whose profile already matches. Refusing is always
allowed, guessing is not — every prior project that tried to blindly rewrite ffmpeg arguments
(rffmpeg #75, jellyfin-meta #36) broke on exactly this.

After translation the server re-checks that every encoder and filter it produced is present in the
agent's reported `encoders`/`filters`, and refuses the placement if not.

### Translation table (source is whatever Jellyfin generated, typically videotoolbox here)

| piece | `none` | `vaapi` | `nvenc` | `qsv` |
|---|---|---|---|---|
| device/hwaccel init | *(removed)* | `-init_hw_device vaapi=va:<device> -hwaccel vaapi -hwaccel_output_format vaapi` | `-hwaccel cuda -hwaccel_output_format cuda` | `-init_hw_device qsv=qs:<device> -hwaccel qsv -hwaccel_output_format qsv` |
| H.264 encoder | `libx264` | `h264_vaapi` | `h264_nvenc` | `h264_qsv` |
| HEVC encoder | `libx265` | `hevc_vaapi` | `hevc_nvenc` | `hevc_qsv` |
| scale filter | `scale=w=W:h=H` | `scale_vaapi=w=W:h=H` | `scale_cuda=w=W:h=H` | `scale_qsv=w=W:h=H` |
| AudioToolbox `aac_at` | `aac` | `aac` | `aac` | `aac` |
| VideoToolbox-only flags (`-prio_speed`) | *(removed)* | *(removed)* | *(removed)* | *(removed)* |

`-codec:v copy` (remux) needs no video translation at all; only `aac_at` has to be mapped, which is
why remuxes are portable to any agent.


## Placement inputs (v2.1, 2026-09-02)

Placement ranks the agents that *can* run a job; these fields tell it which one *should*. All are
advisory — a missing or stale value only costs an agent its ranking edge, never its eligibility.

| field | frame | meaning |
|---|---|---|
| `mounts[].local` | `hello`, `status` | see above: the media is on the agent's own storage |
| `load` | `status` | 0..1, the agent's own view of how busy it is. Advisory: the server already knows the job count, this adds what the job count cannot see (other tenants on the box, a transcode that is unusually cheap or expensive) |

The server measures throughput itself rather than asking for it: ffmpeg reports `speed=N.Nx` on stderr,
those lines already flow back over the control channel for Jellyfin's progress display, and the plugin
keeps a per-agent rolling average from them. That number is self-calibrating and needs no configuration —
an agent that is fast at *this* library's files, on *this* hardware, with *this* link, earns its ranking
by the work it actually did. An agent that has not run a job yet has no measurement and is ranked on its
free capacity alone.


## Throttling (v2.2, 2026-09-03)

Jellyfin throttles a transcode that races too far ahead of the viewer by writing a single key to
ffmpeg's **stdin**: `p` to pause, `u` to resume. The server already forwards stdin to the agent
(`stdin` frames), so remote throttling needs no new frame — only a way to know whether the agent's
ffmpeg will honour those keys.

| field | frame | meaning |
|---|---|---|
| `ffmpeg.pause_keys` | `hello` | `true` when the agent's ffmpeg supports the `p`/`u` interactive pause keys. Optional; absent means unknown, treated as unsupported |

Those keys are **not** in upstream ffmpeg: they come from jellyfin-ffmpeg's
`0028-add-pause-support-for-ffmpeg-cli.patch`. Jellyfin detects them by running ffmpeg against a null
source, writing `?` to its stdin, and looking for `p      pause transcoding` in the help it prints;
the agent probes its own binary the same way and reports the answer. It must be the *agent's* answer,
not the server's — the two run different ffmpeg builds on different platforms, and the server's own
capability says nothing about the machine that will actually run the job.

An agent that reports no pause-key support still gets work; its jobs simply run unthrottled, exactly
as they do today. Upstream's fallback of sending `c` is deliberately not used remotely: on a build
without the patch `c` opens ffmpeg's "send a filtergraph command" prompt instead of pausing anything,
so it would mislead rather than throttle.

Why this matters: without throttling an agent encodes at whatever speed its hardware allows — measured
here at ~25x realtime — so a viewer watching a 4 Mbit/s stream generates around 100 Mbit/s of segment
traffic and the agent burns GPU on an episode that may be abandoned after two minutes. Throttling
bounds both to roughly what is actually being watched.
