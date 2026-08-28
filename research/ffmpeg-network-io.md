# FFmpeg networked I/O for a remote-transcode-agent Jellyfin plugin

Research agent report, 2026-08-28. Environment: macOS (speedwagon), Homebrew ffmpeg 8.0.1, Python 3.14 test
servers (`exp/put_server.py`, `exp/put_server_tls.py`, `exp/flaky_server.py`). FFmpeg source: shallow clone at
commit `d411d9e` (2026-08). jellyfin-ffmpeg / jellyfin source read from GitHub raw URLs.

Tags: **[VERIFIED]** ran the command and observed it; **[SOURCE]** read from source, file:line cited;
**[DOCS]** quoted from official docs.

---

## 1. HLS muxer over HTTP (`-f hls -method PUT`)

### Request sequence: one full PUT per segment, one full PUT per playlist refresh
**[VERIFIED]**
```
ffmpeg -f lavfi -i testsrc=duration=10:size=320x240:rate=25 \
  -c:v libx264 -g 50 -keyint_min 50 -sc_threshold 0 -force_key_frames "expr:gte(t,n_forced*2)" \
  -f hls -method PUT -hls_time 2 -hls_list_size 0 \
  -hls_segment_filename "http://127.0.0.1:8001/out/seg_%03d.ts" \
  http://127.0.0.1:8001/out/stream.m3u8
```
Observed (5 segments): `PUT seg_000.ts, PUT stream.m3u8, PUT seg_001.ts, PUT stream.m3u8, ...` — playlist re-PUT
after **every** segment by default. **[SOURCE]** non-VOD playlists (`hls_playlist_type` default `PLAYLIST_TYPE_NONE`,
`libavformat/hlsenc.c:3179`) call `hls_window()` after every segment (`hlsenc.c:2652`); **VOD mode defers to
`hls_write_trailer()`** (`hlsenc.c:2869`, writes `EXT-X-ENDLIST` once). Jellyfin passes `-hls_playlist_type vod`.

**Pitfall**: without forced keyframes a 10 s clip produced **one** segment (libx264 default `keyint=250`); HLS
cuts on keyframes only. Jellyfin's generated commands already set `-force_key_frames`/`-g`/`-keyint_min`.

### Transfer-Encoding: chunked, not Content-Length
**[VERIFIED]** every PUT showed `Transfer-Encoding: chunked`. **[SOURCE]** segments are muxed into an in-memory dynamic
buffer first (`avio_open_dyn_buf`, `hlsenc.c:860`; `flush_dynbuf()` `hlsenc.c:481-499`) but HTTP headers are written by
`http_connect()` (`http.c:1590-1710`) before the bytes are handed over. Default `chunked_post=1` (`http.c:191`) →
chunked (`http.c:1640`); terminator sent by `http_shutdown()` (`http.c:2091-2116`). **Receiver must accept chunked
PUT bodies.**

### `-hls_flags temp_file` over HTTP: silently downgraded
**[VERIFIED]** → `[hls] Cannot use rename on non file protocol, this may lead to races and temporary partial files`;
direct PUT to final name. **[SOURCE]** gated by `is_file_proto` at `hlsenc.c:1382-1384`, `:1556-1558`, `:2570-2571`;
`ff_rename()`→`ffurl_move()` (`avio.c:751-770,929-935`) needs `url_move`, only `file.c:434` implements it.
**Zero atomicity over HTTP — the receiver must provide it.**

### `-hls_segment_type fmp4`: init segment PUT once, up front
**[VERIFIED]** `PUT init.mp4` once before `PUT seg_000.m4s`. **[SOURCE]** `hlsenc.c:2521-2549`, default name
`init.mp4` (`:3160`), resend only with `-hls_fmp4_init_resend 1` (`:3161`, `:2377-2392`); `EXT-X-MAP` (`:1628-1629`).

### `-headers`: sent on every request, verbatim CRLF string
**[VERIFIED]** `-headers $'X-Agent-Token: sekrit123\r\nX-Job-Id: job42\r\n'` → present on every PUT.
**[SOURCE]** `set_http_options()` forwards `c->headers` (`hlsenc.c:350`); `http_connect()` appends verbatim (`http.c:1700-1701`).

### `-tls_verify 0` / self-signed HTTPS: does NOT work for the HLS muxer
**[VERIFIED]** self-signed server; no flag, `-tls_verify 0`, `-tls_verify 1` → all three succeeded identically.
`-hls_segment_options "tls_verify=1"` → `Some of the provided format options are not recognized`, exit 234.
**[SOURCE]** `-tls_verify` defaults to 1 (`libavformat/tls.h:93-94`, `tls_openssl.c:815-816`), but
**`set_http_options()` only forwards `method`, `user_agent`, `multiple_requests`, `timeout`, `headers`
(`hlsenc.c:334-350`)** — never `tls_verify`/`ca_file`/`cert_file`/`key_file`. `-hls_segment_options` goes to the
inner container muxer (`hlsenc.c:3149`, `:877-895`) — wrong layer.
**Conclusion: no supported CLI way to control TLS verification for the HLS muxer's uploads.** Use plain HTTP on a
trusted link with a token header, or a trusted cert via the system store, or terminate TLS elsewhere.

Side finding (unexplained): for a *direct* `-f mpegts -method PUT https://...`, `-tls_verify 1` correctly rejected the
self-signed cert, but omitting the flag also accepted it, contradicting the documented default.

### Failed PUT: ffmpeg does not check the HTTP response status at all
**[VERIFIED]** server returning HTTP 500 for `seg_001` → **exit 0**, nothing logged even at `-loglevel debug`.
**[SOURCE]** `http_shutdown()` (`http.c:2094-2119`) sends the terminating chunk then does a non-blocking drain read;
never calls `check_http_code()` (`http.c:937-946`). **A 4xx/5xx to a segment PUT is silently ignored.**

Transport failures at PUT-open are fatal by default: nothing listening → `Connection refused` → exit 195.
With `-ignore_io_errors 1`: retried, exits 0 having written nothing. **[SOURCE]** `hlsenc.c:2597-2601` gated by
`ignore_io_errors`; retry-exhaustion paths `hlsenc.c:2611-2630` (segment) and `:2652-2657` (playlist) return the
error unconditionally. `reconnect*` options are decode-only (`D` flag, `http.c:214-222`) — no effect on uploads.

### `-http_persistent 1`: real connection reuse, off by default
**[VERIFIED]** without: distinct client port per PUT, `Connection: close`. With: same port, `Connection: keep-alive`.
**[SOURCE]** `hlsenc_io_open()`/`hlsenc_io_close()` (`hlsenc.c:293-330`) use `ff_http_do_new_request()`;
`set_http_options()` also sets `multiple_requests=1` (`:345`). Default off (`:3194`). fmp4 + byte-range + persistent
unsupported (`:848-851`).

---

## 2. Alternative outputs to HTTP

### `-f segment` with `http://`: silently ignores `-method`/`-headers`
**[VERIFIED]** `-f segment -method PUT ... "http://.../out%03d.ts"` → every request is **POST**.
**[SOURCE]** `segment_start()` opens each segment with a NULL options dict (`segment.c:267`); `http.c` defaults
`AVIO_FLAG_WRITE`→`post=1` (`http.c:1611-1613`); the segment muxer has no `method`/`headers`/`http_persistent`
options (`segment.c:1054+`). **Use `-f hls`.**

### `-f mpegts`/continuous muxers to a single PUT/TCP/RTMP/SRT destination
**[SOURCE]** `mpegts` isn't `AVFMT_NOFILE` (`mpegtsenc.c:2468`) → normal `avio_open2(..., &mux->opts)` path
(`fftools/ffmpeg_mux_init.c:3583-3592`) with the full CLI options dict (`-method`/`-headers`/`-tls_verify` all apply).
But it yields one byte stream, not discrete segment files — the receiver would re-segment. Not useful here.

### `-hls_segment_filename` patterns
**[VERIFIED]** `-start_number 100 -hls_segment_filename ".../%d.ts"` → `100.ts, 101.ts, ...`.
**[SOURCE]** `replace_int_data_in_filename()` (`hlsenc.c:423-466`) handles `%d`/`%0Nd`, seeded from `-start_number`
(`:2986`, default 0 `:3140`). strftime needs `-strftime 1` (`:270-289`).

---

## 3. Networked input

### `-ss` before `-i` over HTTP: real Range-based seek
**[VERIFIED]** two 60 s mp4s (3.15 MB), faststart vs moov-at-end:
```
faststart: -ss 50 → GET bytes=0-  then  GET bytes=2626948-                          (2 requests)
normal:    -ss 50 → GET bytes=0-, GET bytes=3134261-, GET bytes=48-, GET bytes=2607854-  (4 requests)
```
**[VERIFIED]** `-seekable 0` → single GET, no Range, `-ss` degrades to decode-and-discard.

### `-reconnect`: resume-from-drop over HTTP works
**[VERIFIED]** flaky server drops after 500,000 bytes. Without flags: `Stream ends prematurely at 524288`, corrupt.
With `-reconnect 1 -reconnect_streamed 1 -reconnect_on_network_error 1`: `Will reconnect at 524288` → new
`GET Range: bytes=524288-`. **[SOURCE]** decode-only (`http.c:214-222`).

### `pipe:0`
**[VERIFIED]** `cat file.mp4 | ffmpeg -i pipe:0 -f null -` works; non-seekable.

### NFS/SMB
Not tested here (standard filesystem I/O once mounted; resilience is the mount's problem).

---

## 4. Control channel

### `-progress url`
**[VERIFIED]** `-re -stats_period 1 -progress http://...` → **one HTTP POST**, chunked, 4 chunks ~1 s apart, each a
`key=value` block ending `progress=continue`/`progress=end` (`frame=`, `fps=`, `bitrate=`, `out_time_us=`, `speed=`).
**[DOCS]** `-stats_period` default 0.5 s.

### stdin `q`
**[VERIFIED]** `-re` on a 30 s source, `q` after 2 s → stopped at `frame=53`, trailers written, exit 0.

### Jellyfin's throttling: jellyfin-ffmpeg-exclusive stdin protocol (`p`/`u`)
**[SOURCE]** `TranscodingThrottler.cs` polls every 5 s; writes `p`/`u` (or `c`/newline) to ffmpeg's stdin. The keys
exist because of jellyfin-ffmpeg patch `0028-add-pause-support-for-ffmpeg-cli.patch` (`fftools/ffmpeg.c` stdin
handler: `if (key == 'p') pause_transcoding(); ... "Transcoding is paused. Press [u] to resume."`;
`ffmpeg_demux.c`: `if (paused_start) { av_usleep(1000); continue; }`) — cooperative in-process pause.
Jellyfin probes for it at startup (`EncoderValidator.CheckSupportedRuntimeKey("p      pause transcoding", ...)`).
**On stock ffmpeg, throttling silently no-ops** (`c` is the filtergraph-command key; a bare `c` is a parse error).
A remote ffmpeg is unreachable by the existing throttler unless the plugin proxies stdin bytes.

---

## 5. jellyfin-ffmpeg

- Root `README.md` is upstream FFmpeg's; platform/HWA docs are on jellyfin.org.
- **Official builds** (tag `v7.1.4-3`): Debian/Ubuntu `.deb` (amd64/arm64), portable `linux64-gpl`,
  `linuxarm64-gpl`, **`mac64-gpl`, `macarm64-gpl`**, `win64-clang-gpl`, `winarm64-clang-gpl`. No official rpm.
- **HWA** [DOCS]: QSV, NVDEC/NVENC, AMF, VA-API (Linux), VideoToolbox (macOS), RKMPP (Linux).

| Vendor | Windows | macOS | Linux |
|---|---|---|---|
| AMD | AMF | — | VAAPI |
| Apple | — | VideoToolbox | — |
| Intel | QSV | VideoToolbox | QSV/VAAPI |
| Nvidia | NVENC | — | NVENC |
| Rockchip | — | — | RKMPP |

- **98 patches**: CUDA (8), AMF (3), QSV (13), VAAPI (8), OpenCL (7), Vulkan (4), D3D11 (10), VideoToolbox (14),
  RK3588 (1), tonemapping incl. `tonemapx` (3), subtitle/container/DOVI-to-HLS (11), AC-4/DTS:X/fdk-aac (3), build (2),
  **pause support (0028)**, readrate-catchup fix (0077).
- **Does the agent need it?** Jellyfin capability-probes filters, so stock ffmpeg mostly works, but: throttling
  silently broken; no `tonemapx`; HW filters (`scale_vt`, `overlay_qsv`, `yadif_cuda`, …) fall back. **Run
  jellyfin-ffmpeg on agents**, same major.minor as the server.

---

## What the agent did NOT verify
- NFS/SMB input behaviour; HLS-over-HTTPS with a trusted cert; root cause of the direct-mpegts `-tls_verify` anomaly.
- `-f segment` with `-headers` (only `-method` tested); which `-reconnect*` flag was load-bearing.
- `-multiple_requests` for inputs; Windows/Linux runtime; jellyfin-ffmpeg not built/run (patches read only).
- `-hls_flags delete_segments` over HTTP (source: reuses PUT machinery with `method=DELETE`, `hlsenc.c:509-527`).
- **Chunked PUT against Kestrel/ASP.NET** — only the Python test server was exercised. → RESEARCH.md spike #1.
