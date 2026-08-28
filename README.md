# jellyfincluster

Distributed live transcoding for Jellyfin **without shared storage**: a Jellyfin plugin replaces the transcode
manager and ships ffmpeg jobs to `jfc-agent` daemons on other machines; ffmpeg on the agent pushes the HLS segments
straight back to the server over HTTP PUT, where Jellyfin serves them exactly as if it had produced them itself.

```
 client ──HLS──▶ Jellyfin (speedwagon) ── WebSocket control ──▶ jfc-agent (trish)
                     ▲   plugin: Cluster                            │ jellyfin-ffmpeg
                     └────── HTTP PUT segments (ffmpeg -f hls -method PUT) ◀──┘
                                                          reads media from the same SMB share
```

Why it looks like this: [`RESEARCH.md`](RESEARCH.md). What goes over the wire: [`PROTOCOL.md`](PROTOCOL.md).
How to deploy and test: [`docs/DEPLOY.md`](docs/DEPLOY.md).

## Layout

| Path | What |
|---|---|
| `plugin/` | `Jellyfin.Plugin.Cluster` (C#, net9.0, Jellyfin 10.11.x). `Transcoding/ClusterTranscodeManager.cs` is a fork of upstream `TranscodeManager` @ v10.11.0 (diff marked `// jfc:`); `Transcoding/JobRouter.cs` decides local vs remote and rewrites the ffmpeg argv; `Agents/` = WebSocket hub + registry + placement; `Ingest/` = segment upload endpoint; `Api/` = status; `Configuration/` = dashboard page. |
| `agent/` | `jfc-agent` (Rust daemon: capability probe, WebSocket client, ffmpeg supervisor) and `jfc-mock-server` (a fake plugin to exercise the agent without Jellyfin). |
| `docs/upstream-10.11.0/` | verbatim upstream files the fork is based on — diff against these when rebasing on a new Jellyfin minor. |
| `research/` | the research reports with file:line citations and the ffmpeg experiments. |
| `scripts/` | package / install / deploy helpers. |

## Build

```sh
# plugin (needs .NET 9 SDK; rootless install: curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 9.0)
export PATH="$HOME/.dotnet:$PATH"
dotnet build plugin/jfc.sln -c Release && dotnet test plugin/jfc.sln
scripts/package-plugin.sh            # → dist/Cluster_<version>/ (DLL + meta.json) and dist/Cluster_<version>.zip

# agent
cd agent && cargo build --release && cargo test
```

## Try the agent without Jellyfin

```sh
cd agent
cargo run --release --bin jfc-mock-server -- --listen 127.0.0.1:8097 --secret dev --out-dir /tmp/jfc-out --job testsrc --once &
cargo run --release --bin jfc-agent -- --server-url ws://127.0.0.1:8097/Cluster/agents/ws --secret dev --ffmpeg /opt/homebrew/bin/ffmpeg
ls /tmp/jfc-out        # testjob0.ts … testjob.m3u8
```

## Status

v0 — mockup built, not yet run against a live Jellyfin. Scope of v0: HLS video transcodes only, macOS→macOS
(VideoToolbox) agents, media reachable on the agent at the same path. Probing, thumbnails, trickplay, subtitle
burn-in, progressive streams and live TV stay local. See `RESEARCH.md` §7–§9.
