# jellyfin-anemone

[![agent CI](https://github.com/calibrae/jellyfin-anemone/actions/workflows/ci.yml/badge.svg)](https://github.com/calibrae/jellyfin-anemone/actions/workflows/ci.yml)
[![License: GPL-2.0-or-later](https://img.shields.io/badge/license-GPL--2.0--or--later-blue.svg)](LICENSE)

Distributed live transcoding for Jellyfin **without shared storage**. A Jellyfin plugin ("Anemone") replaces
the transcode manager and ships live ffmpeg jobs to `polyp` agent daemons on other machines; ffmpeg on the
agent pushes the HLS segments straight back to the server over HTTP PUT, and Jellyfin serves them exactly as
if it had produced them itself. No shared filesystem, no SSH, no wrapper binary pretending to be ffmpeg.

As far as we've found, nobody else does it this way — rffmpeg, ClusterPlex and kube-plex all require a
shared filesystem between the server and the workers. See [Prior art & credits](#prior-art--credits) and
[`RESEARCH.md`](RESEARCH.md) §5 for the survey, and [`PROTOCOL.md`](PROTOCOL.md) for exactly what goes over
the wire.

## Status

**v0, running live in one person's homelab.** It is young. The server is speedwagon (Jellyfin 10.11.0,
macOS), with two agents in daily use: trish (macOS/arm64, VideoToolbox, media over SMB at the same path as
the server, connected over Thunderbolt) and abbacchio (Debian 13, Intel Iris Xe VAAPI, media on local disk,
connected over LAN). The fleet is deliberately heterogeneous — different OS, different hardware encoder,
different media path on each agent — and the plugin translates the ffmpeg command line and remaps paths per
agent rather than requiring the boxes to match. Details of that deployment: [`docs/DEPLOY.md`](docs/DEPLOY.md).

**What works today:** HLS video transcodes, routed to any agent whose ffmpeg version policy is satisfied and
whose hardware profile either already matches the source or can be translated to it (VideoToolbox ↔ VAAPI /
NVENC / QSV / software); per-agent media path mapping when an agent's mount lives at a different path than
the server's; automatic, transparent fallback to local transcoding when no agent qualifies or an agent dies
mid-job.

**Explicitly out of scope in v0** (stays local, unconditionally): subtitle burn-in and any command line the
hardware translator does not fully understand (`-filter_complex`, tonemapping, an unrecognized filter —
the translator *refuses* rather than guesses); progressive/non-HLS transcodes; live TV; probing, thumbnails,
chapter images, trickplay, and subtitle/attachment extraction. See `RESEARCH.md` §7 for the full list and
`PROTOCOL.md`'s "Hardware acceleration" section for why refusing is the rule.

## Architecture

```
 client ──HLS──▶ Jellyfin (speedwagon:8096) ─────┐
                     ▲  DynamicHlsController      │ builds the ffmpeg command line, unchanged
                     │                            ▼
                     │                 AnemoneTranscodeManager ── JobRouter
                     │                 (fork of TranscodeManager)   picks an agent, translates hwaccel,
                     │                            │                 rewrites the argv
                     │            WebSocket control: job / stdin (q,p,u) / kill / stderr / exit / ping
                     │                            │
                     │                            ▼
                     │                  polyp (agent — trish, abbacchio, …)
                     │                    reads media from its own mount, runs jellyfin-ffmpeg
                     │                            │
                     └──────── HTTP PUT segments ◀┘   ffmpeg -f hls -method PUT
                          Anemone listener :8097        -headers "Authorization: Bearer <job token>"
                          (own Kestrel — not Jellyfin's :8096; see PROTOCOL.md "Why both channels
                           live on the plugin's own port")
```

Design points worth knowing before reading code (all covered at length in [`PROTOCOL.md`](PROTOCOL.md) and
[`RESEARCH.md`](RESEARCH.md); a focused tour for contributors is in
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)):

- The plugin replaces `ITranscodeManager` via plugin DI. `AnemoneTranscodeManager` is a **fork of Jellyfin's
  own `TranscodeManager`**, diff marked `// anemone:`, kept rebase-able against future Jellyfin minors
  (upstream copies live in `docs/upstream-10.11.0/`).
- The plugin runs **its own Kestrel listener** instead of endpoints inside Jellyfin's process, because
  Jellyfin claims every WebSocket upgrade before routing (so a plugin controller never sees one) and caps
  request bodies at 30 MB (too small for a video segment).
- Jellyfin always builds the ffmpeg command line for **its own hardware**. The plugin **translates** that
  command line per agent, and the translator **refuses** anything it doesn't fully model rather than
  guessing — falling back to a matching agent, or to local.
- Media paths and ingest URLs are both per-agent: a mount can live at a different path on the agent than on
  the server, and each agent is handed the ingest address it actually reached the server on.

## How it works, in four steps

1. Jellyfin builds the ffmpeg command line exactly as it always does — `DynamicHlsController` and everything
   upstream of `StartFfMpeg` is untouched.
2. `AnemoneTranscodeManager` intercepts that call. `JobRouter` picks a candidate agent — connected, alive,
   has free capacity, has a mount covering the input file, satisfies the ffmpeg-version policy, and either
   already matches the source's hardware profile or can be translated to it — or decides nothing qualifies
   and the job stays local.
3. If a candidate is found, the router rewrites the argv: the input path is remapped to the agent's mount,
   hardware-specific flags are translated to the agent's profile, and the HLS output arguments are replaced
   with the plugin's ingest URL plus a fresh per-job bearer token. The rewritten job is sent over that
   agent's WebSocket.
4. `polyp` spawns jellyfin-ffmpeg, streams its stderr back over the socket (feeding Jellyfin's existing
   progress/dashboard parsing, unmodified), and ffmpeg PUTs each HLS segment straight back to the plugin's
   ingest endpoint, which writes it atomically (`<name>.part` + rename). Jellyfin's existing segment-readiness
   check (segment N is ready once N+1 exists) serves it exactly as if it had been produced locally.

## Layout

| Path | What |
|---|---|
| `plugin/` | `Jellyfin.Plugin.Anemone` (C#, net9.0, Jellyfin 10.11.x). `Transcoding/AnemoneTranscodeManager.cs` is the `TranscodeManager` fork; `Transcoding/JobRouter.cs` + `RoutePlanner.cs` decide local vs. remote and rewrite the argv; `Transcoding/HwTranslator.cs` + `MountPathMapper.cs` handle per-agent hardware/path translation; `Agents/` is the WebSocket hub + registry + placement; `Ingest/` is the segment upload endpoint + token store; `Api/` is the dashboard status endpoint; `Configuration/` is the settings page. |
| `agent/` | `polyp` (Rust daemon: capability probe, WebSocket client, ffmpeg supervisor) and `anemone-mock` (a fake plugin to exercise the agent without Jellyfin). See [`agent/README.md`](agent/README.md). |
| `docs/upstream-10.11.0/` | Verbatim upstream Jellyfin files the fork is based on — diff against these when rebasing on a new Jellyfin minor. |
| `docs/ARCHITECTURE.md` | Technical tour: request path, component map, state, and the failure/fallback matrix. |
| `docs/DEPLOY.md` | The real deploy log: how speedwagon, trish and abbacchio were actually set up, what broke, and what was measured. |
| `research/` | The research reports behind `RESEARCH.md`, with file:line citations and the ffmpeg network-IO experiments. |
| `scripts/` | Package / install / deploy helpers. |

## Install

Add this repository in Jellyfin (Dashboard → Plugins → Repositories → **+**):

```
https://raw.githubusercontent.com/calibrae/jellyfin-anemone/main/manifest.json
```

then install **Anemone** from the catalogue and restart Jellyfin. Requires Jellyfin 10.11.x — the plugin is
built against a specific Jellyfin version and will refuse to load on a different one.

To install by hand instead, unzip the release asset into `plugins/Anemone_<version>/` inside your Jellyfin
data directory, so that `Jellyfin.Plugin.Anemone.dll` and `meta.json` sit directly in that folder.

## Quick start

### Plugin (server)

```sh
# needs the .NET 9 SDK; see CONTRIBUTING.md for a rootless install
dotnet build plugin/anemone.sln -c Release
dotnet test plugin/anemone.sln

scripts/package-plugin.sh              # → dist/Anemone_<version>/ (DLL + meta.json), dist/Anemone_<version>.zip
scripts/install-plugin-local.sh        # installs into a local macOS Jellyfin and relaunches the app
```

Then in the Jellyfin dashboard: Plugins → Anemone → set a **Shared secret**, leave **Ingest base URL** empty
unless you're behind NAT/a reverse proxy, and start with **Dry run** on to confirm the plugin loads and
routing decisions look right before it moves any real traffic. Full walkthrough, including how to bring the
first agent online and verify a real routed transcode: [`docs/DEPLOY.md`](docs/DEPLOY.md).

### Agent

```sh
scripts/deploy-agent.sh <host> ws://<server>:8097/Anemone/agents/ws /path/to/jellyfin-ffmpeg
ssh <host>   # edit ~/anemone/polyp.toml: secret = <the plugin's Shared secret>
             # macOS: sudo ~/anemone/install.sh   |   Linux: sudo ~/anemone/install-linux.sh
```

`scripts/deploy-agent.sh` detects the target OS over SSH and pushes the right packaging (macOS
LaunchDaemon or Linux systemd unit). See [`agent/README.md`](agent/README.md) for building and running
`polyp` by hand, and its `--once`/`--job-file` mock-server workflow for testing changes with no Jellyfin
server at all.

## Configuration reference

### Plugin settings (Jellyfin dashboard → Plugins → Anemone)

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | on | Master switch. Off = every transcode runs locally; agents may still connect. |
| `DryRun` | off | Log the routing decision but always transcode locally. Use to stage the plugin on a live server. |
| `SharedSecret` | *(empty)* | Bearer token every agent must present to open the control WebSocket. Required — agents can't connect while empty. |
| `IngestBaseUrl` | *(empty)* | Base URL agents PUT segments to. Leave empty (recommended): each agent is told the address it actually reached the server on, so a Thunderbolt-attached agent keeps using Thunderbolt and a LAN agent uses the LAN. Set only for NAT/reverse-proxy setups — it then applies to every agent. |
| `AgentListenPort` | `8097` | TCP port for the plugin's own listener (control WebSocket + ingest). Can't share Jellyfin's own port (see Architecture, above). `0` disables remote transcoding entirely. |
| `PreferRemote` | on | Prefer a remote agent over local transcoding whenever one has free capacity. |
| `LocalMaxSessions` | `2` | Concurrent local transcodes the server keeps for itself before preferring agents (when `PreferRemote` is off). |
| `AgentStartTimeoutSeconds` | `15` | Seconds to wait for an agent's `started` frame before falling back to local. |
| `AgentDeadAfterSeconds` | `30` | Seconds without a `status` frame before an agent is considered dead. |
| `RequireMatchingFfmpeg` | on | Require an agent's ffmpeg major.minor to match the server's before routing to it. |
| `AllowHwProfileTranslation` | on | Route to agents whose hardware profile differs from the source, via `HwTranslator`. Off = only agents whose profile already matches are eligible. |

### `polyp.toml` (agent)

| Key | Default | Meaning |
|---|---|---|
| `server_url` | *(required)* | Control WebSocket URL, e.g. `ws://10.240.0.1:8097/Anemone/agents/ws`. |
| `secret` | *(required)* | Sent as `Authorization: Bearer <secret>`; must match the plugin's `SharedSecret`. |
| `name` | machine's short hostname | Reported in `hello`. |
| `ffmpeg` | `ffmpeg` (via `PATH`) | Path to jellyfin-ffmpeg. |
| `max_sessions` | `3` | Concurrent transcode cap. |
| `mounts` | `[]` | Paths that must be readable; each entry can map a `server_path` when the tree lives at a different path on this agent than on the server. |
| `hwaccel` | auto-detected | `videotoolbox` / `nvenc` / `qsv` / `vaapi` / `amf` / `rkmpp` / `none`. |
| `hwaccel_device` | auto-detected for vaapi/qsv | e.g. `/dev/dri/renderD128`. |
| `log_level` | `info` | `trace`/`debug`/`info`/`warn`/`error`; `RUST_LOG` overrides. |

Full field-by-field detail, the two `mounts` forms, and hwaccel auto-detection rules:
[`agent/README.md`](agent/README.md#config-reference-polypexampletoml).

## Measured

All numbers below are from the live deployment on speedwagon, measured 2026-09-02 (see
[`docs/DEPLOY.md`](docs/DEPLOY.md) for the full write-up).

| Scenario | Result |
|---|---|
| abbacchio: VAAPI (Iris Xe), 1080p HEVC → 720p H.264, 60 s clip | 25.2× realtime |
| abbacchio: same clip, libx264 veryfast (16 threads) | 14.1× realtime |
| abbacchio: a real routed job (not a synthetic benchmark) | 51.8× realtime |
| abbacchio: first segment served on a real routed job | 0.44 s |
| trish: first segment served on a real routed job (VideoToolbox, over Thunderbolt) | 0.70 s |
| Graceful stop (`q` over the control channel) → agent reports exit | ~2 ms |
| Agent killed mid-stream → next transcode falls back to local | no user-visible failure |

## Testing

```sh
dotnet test plugin/anemone.sln     # plugin
cd agent && cargo test             # agent
```

The plugin test suite is organized in tiers (unit, integration, end-to-end); how to run a specific tier, and
the prerequisites for each, are in [`CONTRIBUTING.md`](CONTRIBUTING.md).

## Prior art & credits

- **[Jellyfin](https://github.com/jellyfin/jellyfin)** — `AnemoneTranscodeManager` is a fork of Jellyfin's
  own `TranscodeManager`; everything upstream of `StartFfMpeg` (argument building, the HLS state machine,
  segment serving) is untouched. See [NOTICE](NOTICE).
- **[rffmpeg](https://github.com/joshuaboniface/rffmpeg)** — the existing reference implementation for
  distributed Jellyfin transcoding (SSH-wrapped ffmpeg). Its hard requirement for identical shared storage
  and hardware on every node, and the specific failure modes in its issue tracker (pause/resume breaking
  over an SSH PTY, no real kill propagation, NFS lag), directly shaped several design choices here —
  connection-liveness-driven kill, forwarding `q`/`p`/`u` verbatim, refusing rather than guessing at argv
  translation. See `RESEARCH.md` §5.
- **[ClusterPlex](https://github.com/pabloromeo/clusterplex)** — the equivalent idea for Plex (a Node shim +
  Socket.IO worker pool). Its lesson that the client SIGKILLs the shim on disconnect, and the fix of an
  explicit kill RPC, is why job liveness here is tied to control-connection liveness rather than signal
  forwarding.
- **[jellyfin-meta discussion #36](https://github.com/jellyfin/jellyfin-meta/discussions/36)** — Jellyfin
  core sketched almost exactly this shape (a task API, node-local media mounts, a node pushing its own
  output) in 2023 and never built it. This project is an attempt at the real thing, outside core.

## License

GPL-2.0-or-later, for the whole repository — see [LICENSE](LICENSE). This isn't a stylistic pick:
`AnemoneTranscodeManager` is a fork of Jellyfin's own GPL-2.0-or-later `TranscodeManager`, and the GPL
requires derivative work to carry the same license (or a later version of it). Rather than splitting
licensing file-by-file, the rest of the plugin, the `polyp` agent, docs and scripts are released under the
same terms. See [NOTICE](NOTICE) for exactly which files are derived from Jellyfin and which are kept
verbatim for rebasing.
