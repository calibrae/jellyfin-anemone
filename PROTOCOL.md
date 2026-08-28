# anemone wire protocol v1

Two channels between the Jellyfin plugin ("server") and each `polyp` ("agent"):

1. **Control** — one WebSocket per agent, opened *by the agent* to
   `ws(s)://<jellyfin>/Anemone/agents/ws`, upgrade request carrying `Authorization: Bearer <shared secret>`.
   Text frames, one JSON object per frame, `type` discriminator. Either side may send `ping`; the peer answers `pong`.
2. **Ingest** — ffmpeg on the agent uploads HLS output with `HTTP PUT` to
   `<ingest_base>/Anemone/ingest/<job_id>/<filename>` carrying `Authorization: Bearer <job token>`
   (chunked transfer, one PUT per segment; `-http_persistent 1`). The server writes `<filename>.part` in the job's
   target directory and renames on completion. Filenames are validated against the job's prefix.

All ids are opaque strings (the server uses GUID-N). Timestamps are RFC 3339. Unknown fields are ignored;
unknown `type` values are logged and ignored.

## Agent → Server

| type | fields | notes |
|---|---|---|
| `hello` | `name`, `version`, `platform` (`macos-arm64`, `linux-x86_64`, …), `ffmpeg: {path, version, hwaccels[], encoders[], decoders[], filters[]}`, `mounts: [{path, ok}]`, `max_sessions` | first frame after connect; server answers `welcome` or `reject` and closes |
| `status` | `active` (int), `load` (0..1, optional), `mounts` (optional refresh) | sent on change and at least every 10 s (doubles as heartbeat) |
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
