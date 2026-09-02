# Architecture

A technical tour for someone about to change code, not a restatement of the design rationale — that's
[`RESEARCH.md`](../RESEARCH.md) (why it looks like this) and [`PROTOCOL.md`](../PROTOCOL.md) (the exact wire
contract). This document cross-links both rather than duplicating them; if something here disagrees with
`PROTOCOL.md`, `PROTOCOL.md` wins.

## Request path: client to segment

1. A client asks Jellyfin for HLS playback. `DynamicHlsController` (stock Jellyfin, untouched) builds the
   full ffmpeg command line as it always would — same as if this plugin didn't exist — and calls
   `ITranscodeManager.StartFfMpeg(state, outputPath, commandLineArguments, …)`.
2. Plugin DI resolved `AnemoneTranscodeManager` in place of the stock `TranscodeManager` for that interface
   (see `PluginServiceRegistrator.cs`; registration order is what makes the override win — see
   `RESEARCH.md` §3). It calls `IJobRouter.TryPlan(state, outputPath, commandLineArguments, jobType)`.
3. `JobRouter.TryPlanCore`:
   - Runs `RoutePlanner.Analyze` on the split argv — a pure function that decides whether this command line
     is even a *candidate* for remote routing (HLS output, single input, no concat/subtitle-burn-in/etc.;
     see `PROTOCOL.md` "Argument rewriting" rule 5) and extracts the input path(s) and the hardware
     requirements (`hwaccels`/`decoders`/`encoders`/`filters` the command line asks for).
   - If routable, asks `AgentHub.Candidates(...)` for connected agents ordered by load that are alive, have
     free capacity, have a mount covering the input path (`MountPathMapper`), and either already match the
     source's hardware profile or (`AllowHwProfileTranslation`) can be translated to it (`HwTranslator`).
   - Picks the first candidate whose rewritten command line re-validates: every encoder/filter the
     translator produced must be present in that agent's own reported `encoders`/`filters`, or the
     candidate is rejected and the next one is tried.
   - On success: mints a per-job ingest token (`IIngestTokenStore.Issue`), rewrites the argv (input path
     remapped, hw flags translated, output replaced with the ingest URL + `-headers "Authorization: Bearer
     <token>"`), and returns a `RoutePlan`.
   - On any failure (exception, no routable candidate, no agent qualifies) it returns `null` and
     `AnemoneTranscodeManager` runs the exact upstream local-transcode path — nothing downstream of this
     decision knows or cares that remote routing was attempted and declined.
4. `AnemoneTranscodeManager` sends the plan as a `job` frame over the chosen `AgentConnection`'s WebSocket,
   registers a `TranscodingJob` in `_activeTranscodingJobs` exactly like upstream (so kill timers, session
   reporting, and the dashboard behave identically for local and remote jobs), and wires the job's `id` into
   `_remoteJobs` so incoming `stderr`/`exit` frames for that id can be dispatched.
5. `AgentConnection` on the plugin side writes the `job` frame; `polyp`'s `ws.rs` dispatches it to `job.rs`,
   which spawns jellyfin-ffmpeg with the given argv, streams stdout/stderr, and forwards `stdin` frames
   (Jellyfin's `q`/`p`/`u`) straight to the child's stdin, unbuffered.
6. ffmpeg on the agent reads the source file from its own local mount and PUTs each HLS segment (and the
   `.m3u8` — see `PROTOCOL.md`'s "Note on the playlist") to the plugin's `IngestHandler`, which validates the
   bearer token and filename (`IngestNames`) and writes `<name>.part` then renames it in place — the same
   atomic-rename pattern Jellyfin's own local ffmpeg run relies on implicitly by writing directly to the
   final path (ffmpeg itself never sees a partial file mid-write there either; here the atomicity has to be
   engineered because the write crosses a process and a network hop).
7. Jellyfin's segment-readiness polling in `DynamicHlsController` (stock, unmodified — "segment N is ready
   once N+1 exists") serves the file from the local transcodes directory exactly as if the plugin's
   `AnemoneTranscodeManager` had run ffmpeg itself.
8. `stderr` frames from the agent are fed into a `Pipe` (`RemoteJobSink`) whose reader end is handed to
   Jellyfin's own `JobLogger.StartStreamingLog` — the same stderr-parsing code that drives the dashboard's
   progress bar and `TranscodingThrottler` for local jobs, unmodified.

## Component map

### Plugin (`plugin/Jellyfin.Plugin.Anemone/`)

| Component | File(s) | Role |
|---|---|---|
| Transcode manager fork | `Transcoding/AnemoneTranscodeManager.cs` | Drop-in `ITranscodeManager`. Fork of upstream `TranscodeManager`; every deviation marked `// anemone:`. Owns the active-job registry (`_activeTranscodingJobs`) and the id→remote-job map (`_remoteJobs`). |
| Router | `Transcoding/JobRouter.cs`, `RoutePlanner.cs` | `RoutePlanner` is pure argv analysis (routable? input paths? hw requirements?), unit-testable with no live services. `JobRouter` wraps it with the parts that need live state: agent placement, token issuance, base-URL resolution. |
| Hardware translator | `Transcoding/HwTranslator.cs` | Pure, static. Translates the hw-specific slice of a command line from the source profile to a target agent's profile per `PROTOCOL.md`'s translation table; refuses (returns no translation) on anything it doesn't model. |
| Path mapper | `Transcoding/MountPathMapper.cs` | Path-segment-boundary matching + prefix rewrite between a job's server-side input path and an agent's mount table. Shared by `AgentHub` (does this agent cover this input?) and `JobRouter` (rewrite the input arg) so the matching rule can't drift between the two call sites. |
| Argv utilities | `Transcoding/ArgumentLine.cs` | Splits/joins the single `commandLineArguments` string using the same quoting rules as .NET's own `ProcessStartInfo.Arguments` parser, so the split is byte-for-byte what Jellyfin itself would produce. |
| Concurrency helper | `Transcoding/KeyedLock.cs` | In-file replacement for the `AsyncKeyedLock` package's `AsyncKeyedLocker<string>` (upstream's `_transcodingLocks`) — avoids shipping a second copy of an assembly Jellyfin already loads. |
| Progress bridge | `Transcoding/RemoteJobSink.cs` | Turns an `AgentConnection`'s per-job callbacks (stderr line, exited) into a `Pipe` consumable by Jellyfin's stock `JobLogger`. |
| Agent hub | `Agents/AgentHub.cs` | Registry of connected agents (`ConcurrentDictionary<string, AgentConnection>`); owns the `hello`/`welcome` handshake and `Candidates(...)` placement (ordered by load, filtered by liveness/capacity/mount/hw). |
| Agent connection | `Agents/AgentConnection.cs` | Wraps one accepted `WebSocket` past the handshake: single writer task draining a channel, a read loop dispatching frames to the job that owns them. |
| WS endpoint | `Agents/AgentWebSocketEndpoint.cs` | Handles `GET /Anemone/agents/ws`, the connection a `polyp` opens. |
| Listener | `Agents/AnemoneListener.cs`, `AnemoneHostedService.cs` | The plugin's own Kestrel instance (control WS + ingest, on `AgentListenPort`) — see `PROTOCOL.md` "Why both channels live on the plugin's own port". `AnemoneHostedService` is the one `AddHostedService` registration that actually starts (only the first one from a plugin registrator runs — see `docs/DEPLOY.md` "Traps"); it starts the listener and reaps dead agents. |
| Ingest | `Ingest/IngestHandler.cs`, `IngestTokenStore.cs`, `IngestNames.cs` | Receives segment/playlist PUTs, validates the bearer token against the in-memory `IngestTokenStore` and the filename against the job's prefix, writes `.part` + rename. |
| Status API | `Api/AnemoneStatusController.cs` | `GET Anemone/status` — dashboard-facing snapshot of config + connected agents (elevation-gated). |
| Config | `Configuration/PluginConfiguration.cs`, `configPage.html` | Settings surfaced in the Jellyfin dashboard — see the table in the top-level `README.md`. |
| Wire types | `Agents/Protocol/*.cs`, `Contracts/Contracts.cs` | The C# side of the frames defined in `PROTOCOL.md`. |

### Agent (`agent/src/`)

| Component | File | Role |
|---|---|---|
| Config | `config.rs` | TOML file + CLI (clap), CLI wins; parses `mounts` (bare-string or `{path, server_path}` table) into `MountSpec`s. |
| Probe | `probe.rs` | Runs `ffmpeg -version -hwaccels -encoders -decoders -filters` once at startup and parses the output; checks each configured mount (exists, directory, readable, non-empty) with a timeout so a wedged mount (see `docs/DEPLOY.md`'s SMB session-scoping trap) can't hang the agent forever. |
| Hwaccel detection | `hwaccel.rs` | Pure decision function plus the `/dev/dri`/`nvidia-smi` probes behind it; only runs when `hwaccel` isn't set explicitly in config. |
| Protocol | `protocol.rs` | Wire frame types (serde), the stderr line splitter (`\n`/`\r`/`\r\n`), ingest filename validation — the Rust side of `PROTOCOL.md`, checked against the C# side by the wire-compat tests on both ends. |
| WS client | `ws.rs` | Handshake (`hello`→`welcome`/`reject`), frame dispatch, periodic `status`, reconnect with backoff (1s→30s, jittered). Calls `JobManager::kill_all` on every disconnect — job liveness is control-connection liveness, per `PROTOCOL.md`. |
| Job supervisor | `job.rs` | Spawns ffmpeg per `job` frame, pipes `stdin` frames to the child unbuffered, streams stderr lines back, enforces `max_sessions`. |
| Mock plugin | `bin/anemone-mock.rs` | Fake server: control WS + ingest PUT + interactive/`--once`/`--job-file` driver, for exercising `polyp` without Jellyfin. |

See [`agent/README.md`](../agent/README.md) for building, running, and testing the agent in detail.

## What state lives where

| State | Lives in | Survives... |
|---|---|---|
| Connected agents, their capabilities, current load | `AgentHub._agents` (in-memory, plugin process) | Nothing — rebuilt from scratch on every agent `hello`. Lost on plugin/server restart; agents reconnect and re-`hello` automatically. |
| Active transcoding jobs (local and remote) | `AnemoneTranscodeManager._activeTranscodingJobs` / `_remoteJobs` (in-memory) | Nothing. A server restart drops all job bookkeeping; ffmpeg processes on agents are orphaned until their control connection is lost (see fallback matrix) and get killed then. |
| Ingest bearer tokens | `IngestTokenStore` (in-memory, plugin process) | Nothing — minted per job, revoked on job exit or agent disconnect. |
| Plugin settings (secret, ports, policy) | `PluginConfiguration` (Jellyfin's own plugin config XML on disk) | Restarts — this is the one piece of Anemone state Jellyfin persists for you. |
| Agent config (`server_url`, `secret`, `mounts`, `hwaccel`, …) | `/etc/polyp.toml` on each agent | Restarts — read fresh on every `polyp` startup. |
| Running ffmpeg processes | OS process table on the agent | Only as long as the control WebSocket to that agent is alive — see below. |

There is no database and no persistence layer anywhere in this system by design: every piece of dynamic
state is either reconstructed from a live connection (agents, jobs, tokens) or lives in an on-disk config
file that isn't Anemone-specific machinery (Jellyfin's plugin config, `polyp.toml`).

## Failure / fallback matrix

| Failure | Detected by | Behavior |
|---|---|---|
| No agent qualifies (none connected, none with capacity/matching mount/hw) | `JobRouter.TryPlan` returns `null` | Job runs the stock local ffmpeg path — the caller can't tell routing was even considered. |
| Command line uses something the hw translator doesn't model (subtitle burn-in, `-filter_complex`, tonemapping, an unrecognized filter) | `RoutePlanner.Analyze` / `HwTranslator` | Never routed to an agent needing translation for that job; runs locally or on an agent whose profile already matches without translation. See `PROTOCOL.md` "Hardware acceleration". |
| Agent's ffmpeg version doesn't match the server's (when `RequireMatchingFfmpeg`) | `JobRouter` candidate filtering | Agent excluded from placement for that job; another candidate is tried, else local. |
| Agent never sends `started` for a placed job | `AgentStartTimeoutSeconds` timer | Falls back to local (exact mechanism: `docs/DEPLOY.md`'s live-test results show this path exercised end to end). |
| Agent's control WebSocket drops (network, crash, `pkill polyp`) | Server: socket close/read error on `AgentConnection`. Agent: `ws.rs`'s read loop returning. | **Server side**: agent removed from `AgentHub`, every job that was placed on it is marked exited (code `-1`), its ingest token(s) revoked. Jellyfin's own controller sees the missing segment on the next client request and restarts the job — which gets re-routed or falls back local through the normal `JobRouter` path. **Agent side**: every locally running job is SIGKILLed immediately (`JobManager::kill_all`) — job liveness is control-connection liveness, not signal forwarding; see `RESEARCH.md` §5 lesson 1. The agent then reconnects with backoff (1s→30s, jittered) and re-`hello`s. |
| Agent over capacity when a `job` frame arrives | `polyp`'s `max_sessions` check | Agent replies `exit` with `code -2`, `error: "capacity"`; server tries the next candidate or falls back local. |
| Ingest PUT with an unknown job id, bad token, or invalid filename | `IngestHandler` / `IngestNames` | `403`/`404`, and the server drops the connection — ffmpeg never reads HTTP status codes on a PUT (see `RESEARCH.md` §4), so a dropped connection is the only way to make the failure visible to it; it then fails the job the normal way (non-zero exit / connection-refused). |
| `stdin q` (graceful stop) sent but no `exit` within 5s | Server-side timer in `AnemoneTranscodeManager` | Server sends `kill` for that job id; agent SIGKILLs it. |
| Client seeks to a segment that was never produced | Stock `DynamicHlsController.GetDynamicSegment` (unmodified) | Kills the current job (local or remote, same code path) and starts a new one with `-start_number N`; if remote, this is a fresh `JobRouter.TryPlan` call, so it can be re-routed to a different agent than the one running the original job. |

Measured behavior for several of these rows (agent death mid-stream, graceful stop, seek-restart) against the
real deployment is in [`docs/DEPLOY.md`](DEPLOY.md), under "Live results".
