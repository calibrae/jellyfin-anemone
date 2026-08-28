# polyp

Rust half of `jellyfin-anemone`: `polyp` is a daemon that connects to a Jellyfin cluster
plugin over WebSocket and runs `ffmpeg` transcodes on request, pushing HLS output straight back
to the plugin over HTTP PUT. `anemone-mock` is a fake plugin for exercising the agent without
a real Jellyfin server.

Read `../PROTOCOL.md` for the wire protocol both binaries implement, and `../RESEARCH.md` §6 for
the overall architecture.

## Building

```sh
cargo build --release
```

Produces `target/release/polyp` and `target/release/anemone-mock`.

## Running the agent

```sh
polyp --config /etc/polyp.toml
```

All config keys can also be passed as CLI flags, which override the config file:

```sh
polyp \
  --server-url ws://10.240.0.1:8096/Anemone/agents/ws \
  --secret "$(vault read -field=value secret/data/infra/anemone)" \
  --name trish \
  --ffmpeg /opt/anemone/ffmpeg \
  --max-sessions 3 \
  --mounts /Volumes/data \
  --log-level info
```

On startup the agent:
1. Runs `<ffmpeg> -version -hwaccels -encoders -decoders -filters` and parses the results.
2. Checks every configured mount (exists, is a directory, is readable, is non-empty) -- a bad
   mount is reported in `hello`, not a startup failure.
3. Connects to `server_url`, sends `hello`, and waits for `welcome` or `reject`.
4. On `welcome`, starts sending `status` every `ping_interval_s` (or 10s) and on every job-count
   change, and starts accepting `job` frames up to `max_sessions` concurrent.

On disconnect (server closes the socket, network drop, or `reject`), every running job is
SIGKILLed immediately -- per `PROTOCOL.md`, job liveness is control-connection liveness -- and the
agent reconnects with exponential backoff (1s -> 30s, with jitter).

SIGTERM/SIGINT trigger a graceful shutdown: kill all jobs, close the socket, exit.

### Config reference (`polyp.example.toml`)

| key | required | default | notes |
|---|---|---|---|
| `server_url` | yes (here or `--server-url`) | -- | e.g. `ws://10.240.0.1:8096/Anemone/agents/ws` |
| `secret` | yes (here or `--secret`) | -- | sent as `Authorization: Bearer <secret>`; pull from Vault, don't commit it |
| `name` | no | machine's short hostname | reported in `hello` |
| `ffmpeg` | no | `ffmpeg` (resolved via `PATH`) | path to jellyfin-ffmpeg |
| `max_sessions` | no | `3` | concurrent transcode cap |
| `mounts` | no | `[]` | paths that must be readable, e.g. an SMB mount shared with the Jellyfin server |
| `log_level` | no | `info` | `trace`/`debug`/`info`/`warn`/`error`; `RUST_LOG` overrides this |

### Where jellyfin-ffmpeg comes from

Official portable builds (no download performed here -- grab the one matching your platform):
https://github.com/jellyfin/jellyfin-ffmpeg/releases -- look for `macarm64-gpl` (Apple Silicon
macOS), `mac64-gpl` (Intel macOS), `linux64-gpl` / `linuxarm64-gpl` (Linux).

## Running the mock-server + agent loop locally

Two terminals, no real Jellyfin required:

```sh
# terminal 1: fake plugin, sends a 20s synthetic HLS job to the first agent that connects
mkdir -p /tmp/anemone-out
./target/release/anemone-mock \
  --listen 127.0.0.1:8097 \
  --secret devsecret \
  --out-dir /tmp/anemone-out
```

```sh
# terminal 2: the real agent, pointed at the mock server
./target/release/polyp \
  --server-url ws://127.0.0.1:8097/Anemone/agents/ws \
  --secret devsecret \
  --name dev-agent \
  --ffmpeg /opt/homebrew/bin/ffmpeg
```

Terminal 1 prints the agent's reported capabilities on connect, then every `status`/`started`/
`stderr`/`exit` frame it receives. Segments and the playlist land in `/tmp/anemone-out` as
`testjob0.ts`, `testjob1.ts`, ..., `testjob.m3u8`.

While a job is running, type into terminal 1 (mock-server's stdin) and press Enter:

| command | effect |
|---|---|
| `q` | sends `stdin` `"q\n"` -- ffmpeg quits cleanly (writes trailers, exits 0) |
| `p` | sends `stdin` `"p"` -- pause (jellyfin-ffmpeg's patched stdin protocol; no-ops on stock ffmpeg) |
| `u` | sends `stdin` `"u"` -- resume |
| `kill` | sends `kill` -- SIGKILLs the job immediately |
| `job` | sends another testsrc job |

### `--once` (for CI / scripted runs)

```sh
anemone-mock --listen 127.0.0.1:0 --secret devsecret --out-dir /tmp/anemone-out --once
```

Sends the job, prints every frame, and exits 0 as soon as that job's `exit` frame arrives (no
interactive stdin). `tests/e2e.rs` uses this.

### Replaying a real Jellyfin command line (`--job-file`)

Grab a `commandLineArguments` line from a Jellyfin log, split it into a JSON array of strings
(`.NET`-quote-aware splitting, same as `PROTOCOL.md`'s argument-rewriting section describes), and
replace the segment/playlist output paths with `{ingest}/Anemone/ingest/{id}/<prefix>...` plus a
`{token}`-bearing `-headers` value -- `{id}`, `{token}`, `{ingest}` get substituted at send time.
Example (`job.json`):

```json
[
  "-re", "-f", "lavfi", "-i", "testsrc=duration=12:size=640x360:rate=25",
  "-c:v", "libx264", "-preset", "veryfast", "-g", "50", "-keyint_min", "50",
  "-sc_threshold", "0", "-force_key_frames", "expr:gte(t,n_forced*2)",
  "-f", "hls", "-hls_time", "2", "-hls_list_size", "0", "-hls_playlist_type", "vod",
  "-start_number", "0", "-hls_segment_type", "mpegts",
  "-hls_segment_filename", "{ingest}/Anemone/ingest/{id}/myjob%d.ts",
  "-method", "PUT", "-http_persistent", "1",
  "-headers", "Authorization: Bearer {token}\r\n",
  "{ingest}/Anemone/ingest/{id}/myjob.m3u8"
]
```

```sh
anemone-mock --secret devsecret --out-dir /tmp/anemone-out --job-file job.json
```

The mock server derives the ingest filename prefix from `-hls_segment_filename` (or, failing
that, the playlist path) so it can validate ingest PUTs the same way a real server would.

## Testing

```sh
cargo build --release
cargo test
cargo clippy -- -D warnings
cargo fmt --check
```

`cargo test` runs:
- Unit tests (`src/*.rs`): ffmpeg `-version`/`-hwaccels`/`-encoders`/`-decoders`/`-filters`
  parsers against captured real fixtures (`tests/fixtures/`, both Homebrew ffmpeg and
  jellyfin-ffmpeg 7.1.2), stderr line-splitting on `\n`/`\r`/`\r\n`, protocol frame serde
  round-trips, ingest filename validation, and job-supervisor behavior (capacity, kill, stdin
  forwarding) against `/bin/sh`.
- `tests/e2e.rs`: spawns the real `anemone-mock` and `polyp` binaries against a real local
  ffmpeg and checks the ingest output on disk end-to-end. Skipped with a clear message (visible
  with `cargo test -- --nocapture`) if neither `/opt/homebrew/bin/ffmpeg` nor `ffmpeg` on `PATH`
  is found.

## Packaging (macOS LaunchDaemon)

```sh
cargo build --release
sudo ./install.sh    # copies the binary, an example config (if none exists), and the plist;
                      # bootstraps the daemon
```

Then edit `/etc/polyp.toml` (it's installed with the placeholder secret from
`polyp.example.toml`) and:

```sh
sudo launchctl kickstart -k system/net.calii.polyp
```

Logs: `/var/log/polyp/polyp.log` (stdout) and `polyp.err.log` (stderr/tracing).

```sh
sudo ./uninstall.sh       # stop + remove daemon + binary, leaves /etc/polyp.toml
sudo ./uninstall.sh -c    # also remove /etc/polyp.toml
```

Both scripts are idempotent.

## Crate layout

```
agent/
  src/
    main.rs      -- polyp entry point: config, probe, signal handling, wires ws.rs + job.rs
    config.rs     -- CLI (clap) + TOML file config, merged with CLI precedence
    probe.rs      -- ffmpeg -version/-hwaccels/-encoders/-decoders/-filters parsers, mount checks
    protocol.rs    -- wire frame types (serde), stderr line splitter, ingest filename validation
    ws.rs          -- control WebSocket client: handshake, dispatch, status/ping, reconnect backoff
    job.rs          -- job supervisor: spawn/stdin/kill/exit per job, capacity enforcement
    bin/
      anemone-mock.rs -- fake plugin: control WS + ingest PUT + interactive/--once test driver
  tests/
    e2e.rs         -- full-binary integration test
    fixtures/      -- captured ffmpeg -version/-hwaccels/-encoders/-decoders/-filters output
  launchd/
    net.calii.polyp.plist
  install.sh / uninstall.sh
  polyp.example.toml
```
