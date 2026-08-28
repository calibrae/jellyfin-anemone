# Prior Art Survey: Distributed / Remote FFmpeg Transcoding for Jellyfin & Plex

Compiled 2026-08-28. Scope: everything found relevant to a Jellyfin plugin that offloads
ffmpeg transcoding to remote agent machines and streams the result back to the Jellyfin
server over the network (no shared filesystem).

All repos were cloned into `scratchpad/agent-priorart/` and read directly (not just READMEs)
unless noted otherwise. All GitHub issue/PR/feature-request quotes are verbatim or lightly
trimmed, with links.

---

## 1. rffmpeg ecosystem

### 1.1 joshuaboniface/rffmpeg (the original, still the reference implementation)

- **Repo:** https://github.com/joshuaboniface/rffmpeg
- **Language:** Python (single ~1045-line script, `click` CLI + SQLite/Postgres backend)
- **Activity:** 1068 stars, last push **2025-11-03** (actively maintained)

**Architecture (<=8 lines):**
`rffmpeg` is one Python script symlinked as both `ffmpeg` and `ffprobe` on `$PATH`, ahead of
the real `jellyfin-ffmpeg` binaries. Jellyfin invokes it thinking it's ffmpeg. It reads
`argv[0]` to know which binary it's impersonating, looks up the best target host in a local
SQLite (or Postgres) DB of hosts/processes/states (`get_target_host()`,
[rffmpeg:306](https://github.com/joshuaboniface/rffmpeg/blob/master/rffmpeg#L306)), then either
`subprocess.run()`s the real binary locally or wraps the same argv in an `ssh -t <host>
jellyfin-ffmpeg/ffmpeg <args>` command and runs that instead -- `stdin`/`stdout`/`stderr` are
just inherited from the parent process straight through the SSH pseudo-tty. No RPC protocol,
no serialization: it's a transparent argv-rewrite-and-SSH-exec wrapper.

**Host selection:** prefer any `idle` host; if a host doesn't respond to `ffmpeg -version` over
SSH within a fixed short timeout it's marked `bad` for the lifetime of that rffmpeg process only
(README: [Target Host Selection](https://github.com/joshuaboniface/rffmpeg/blob/master/README.md#target-host-selection)).
Otherwise picks the host with the lowest `(running-process-count / configured-weight)`. This is
pure process-count load balancing -- **no CPU/GPU load awareness at all**.

**Hard requirements (source: [README §Hardware Acceleration](https://github.com/joshuaboniface/rffmpeg/blob/master/README.md#hardware-acceleration), [docs/SETUP.md](https://github.com/joshuaboniface/rffmpeg/blob/master/docs/SETUP.md)):**
- Shared NFS/SMB/SSHFS mount for **both** the media library **and** the transcode/cache dir, with **byte-identical paths** on the Jellyfin host and every worker -- rffmpeg "does not know what media file(s) it is handling or where it's outputting files to, and cannot alter these paths."
- **Exact same jellyfin-ffmpeg package version** must be installed on every worker as on the server (`dpkg -l | grep jellyfin-ffmpeg` must match).
- Same Jellyfin service UID/GID on every worker (or explicit remapping via `usermod`).
- Hardware acceleration must be identically available on **every** configured host, including localhost for fallback to work -- "this is an explicit requirement... there is no easy way around this without rewriting the passed arguments, which is explicitly out-of-scope" ([maintainer, issue #75](https://github.com/joshuaboniface/rffmpeg/issues/75)).
- NFS attribute caching causes Jellyfin to see new `.ts` segments 15-60s late unless you set `sync,actimeo=1` on the mount (README explicitly documents this as a required workaround).

**What breaks (issues, verbatim/paraphrased with citations):**
- **Playback-throttle pause/unpause breaks over SSH.** Jellyfin pauses a transcode by writing the literal character `p` to ffmpeg's stdin and resumes with `u`; a maintainer of jellyfin-ffmpeg confirms this directly. Over the SSH PTY this sometimes "freezes or gets disconnected," and the transcode never resumes -> playback gets permanently stuck. [Issue #76](https://github.com/joshuaboniface/rffmpeg/issues/76) ("Playback stuck after couple of minutes").
- **No real kill propagation on hard kill.** rffmpeg's signal handler (`cleanup()`, [rffmpeg:235](https://github.com/joshuaboniface/rffmpeg/blob/master/rffmpeg#L235)) only deletes DB rows -- it never explicitly kills the SSH child or forwards a kill to the remote ffmpeg. Kill relies entirely on SSH PTY signal/EOF propagation. Confirmed in practice: users report stuck `rffmpeg status` entries needing manual `rffmpeg clear` after "some hours" -- [Issue #89](https://github.com/joshuaboniface/rffmpeg/issues/89).
- **stdout/stderr redirection breaks plugins that parse ffmpeg output** (e.g. Intro Skipper) because rffmpeg routes most ffmpeg invocations' output to stderr instead of stdout to match Jellyfin's quirky parsing, except for a hardcoded `special_flags` allowlist (`-version`, `-muxers`, `-fp_format`, etc.) -- [Issue #56](https://github.com/joshuaboniface/rffmpeg/issues/56), fixed by making the flag list configurable.
- **Hardware acceleration is never negotiated, only "hoped for."** Local fallback fails outright if HWA is on and the fallback host lacks the device -- maintainer confirms this is by design and won't be fixed because argument-mangling is explicitly out of scope: [Issue #3](https://github.com/joshuaboniface/rffmpeg/issues/3), [Issue #75](https://github.com/joshuaboniface/rffmpeg/issues/75).
- **SQLite contention under concurrent stream starts.** Multiple transcodes starting at the exact same instant can hit SQLite lock conflicts, marking healthy hosts "bad" and forcing fallback to localhost -- confirmed as a still-current limitation in a Jan-2026 real-world writeup, [Transcodarr README §Known Limitations](https://github.com/JacquesToT/transcodarr) (see §4.5 below).
- Filename-quoting/escaping edge cases with special characters in media paths: [Issue #28](https://github.com/joshuaboniface/rffmpeg/issues/28).
- No Kubernetes-native pod-per-job model -- only static SSH host lists; a "Kubernetes operator" idea has sat open since 2022: [Issue #34](https://github.com/joshuaboniface/rffmpeg/issues/34).
- **SSH connection-setup latency is real and only partially mitigated.** [PR #12](https://github.com/joshuaboniface/rffmpeg/pull/12) ("Enable SSH multiplexing/persistence", merged) added `ControlMaster`/`ControlPersist` specifically to avoid re-paying full SSH handshake cost on every single invocation (rffmpeg calls out to `ffprobe` constantly, not just `ffmpeg`) -- without it, every probe/transcode call pays a fresh TCP+SSH-auth round trip; even with it, first-connection-per-host setup is still a measurable tax on stream-start latency.
- **Docker/permissions friction when the container's Jellyfin user doesn't map cleanly to a UID rffmpeg can use for its own SSH identity** -- [Issue #90](https://github.com/joshuaboniface/rffmpeg/issues/90) ("Docker doesn't run as Jellyfin?").
- **Startup self-check fragility:** rffmpeg symlinked as `ffmpeg` can fail Jellyfin's own "is this a valid ffmpeg binary" version-string probe at Jellyfin startup, blocking the server from starting transcodes at all until manually fixed -- still open: [Issue #94](https://github.com/joshuaboniface/rffmpeg/issues/94).

**Reusable for our design:** the argv-capture-and-forward pattern (become the binary the media
server calls, capture full argv+cwd+env) is the right shim strategy regardless of transport.
The "mark host bad for the duration of the marking process only" TTL approach to failure
handling is a reasonable, simple pattern worth keeping. Everything about its transport (SSH+PTY,
shared filesystem) is exactly what we want to replace.

### 1.2 aleksasiriski's ecosystem (NOT a fork of rffmpeg -- a parallel toolset)

Important correction to the brief: **aleksasiriski does not maintain a fork named
`aleksasiriski/rffmpeg`** (that repo doesn't exist). Instead they maintain a family of
companion/alternative tools, all confirmed via `gh repo list`:

- **`aleksasiriski/ffmpegof`** ("FFmpeg over Fabrics") -- https://github.com/aleksasiriski/ffmpegof -- Go, 69 stars, **archived**, last push 2024-10-27, AGPL-3.0. This is the full **Go rewrite of rffmpeg** -- originally developed under the name `rffmpeg-go` (confirmed via [rffmpeg issue #64](https://github.com/joshuaboniface/rffmpeg/issues/64), "Rewrite in Go", and [#59](https://github.com/joshuaboniface/rffmpeg/issues/59)), later renamed. Selling points per the author: DB module importable by other tools, "should be more performant." Same fundamental transparent-SSH-wrapper architecture as upstream rffmpeg, just in Go. **It is now archived/unmaintained** -- the rffmpeg-go/ffmpegof line was effectively abandoned in favor of upstream Python rffmpeg continuing development.
- **`aleksasiriski/rffmpeg-worker`** -- https://github.com/aleksasiriski/rffmpeg-worker -- Shell, 21 stars, **archived**, last push 2023-10-03. A minimal SSH-server container (built on [panubo/docker-sshd](https://github.com/panubo/docker-sshd)) meant to run *as* the worker side of upstream rffmpeg, for Docker/Kubernetes deployments. README confirms workers need only three shared paths: `/config/cache`, `/config/transcodes`, `/config/data/subtitles` -- same NFS-identical-path requirement as upstream, and explicitly recommends [OpenEBS dynamic-nfs-provisioner](https://github.com/openebs/dynamic-nfs-provisioner) or Longhorn RWX volumes on Kubernetes, "must be exactly the same mount points!"
- **`aleksasiriski/rffmpeg-autoscaler`** -- 17 stars -- scales the *number* of rffmpeg worker containers/VMs up/down based on load.
- **`aleksasiriski/hcloud-rffmpeg`** -- 12 stars -- spins up ephemeral Hetzner Cloud VMs as rffmpeg workers on demand.
- **`aleksasiriski/jellyfin-kubernetes`** -- 4 stars -- Helm chart wiring Jellyfin + rffmpeg + workers together on Kubernetes.
- **`Shadowghost/jellyfin-rffmpeg`** -- https://github.com/Shadowghost/jellyfin-rffmpeg -- Shell, 28 stars, last push 2022-10-05 (stale). Prebuilt Jellyfin Docker images with rffmpeg baked in, for people who don't want to hand-patch the official image.

Confirmed by direct maintainer quote in the same issue thread: a Jellyfin community member
asking about a Kubernetes operator was told by aleksasiriski (in
[rffmpeg#34](https://github.com/joshuaboniface/rffmpeg/issues/34)): *"you can take a look at my
repos for some attempts on doing this (ffmpeg-of & jellyfin kubernetes helm chart)... I'm not
maintaining any of it anymore since I had other priorities."* -- i.e. the author himself
considers this whole line of work abandoned as of that comment.

**What breaks / lessons specific to this line:** all the same shared-storage and
version-matching requirements as upstream rffmpeg (it's the same wire protocol, just a
different implementation). No new failure modes beyond what's documented in §1.1, other than
this ecosystem being noticeably less maintained than upstream.

### 1.3 CrystalNET-org/grpc-ffmpeg (tangential, but worth noting)

https://github.com/CrystalNET-org/grpc-ffmpeg -- 13 stars, last updated 2026-03-29. A gRPC-based
service for executing ffmpeg commands on remote hosts/containers. Not Jellyfin-specific and
not deeply investigated here, but notable as the only prior-art example found that replaces
SSH-argv-forwarding with an actual typed RPC transport (gRPC) -- validates that "define a small
RPC contract instead of shelling to SSH" is a design direction others have independently
reached for.

---

## 2. ClusterPlex (pabloromeo/clusterplex)

- **Repo:** https://github.com/pabloromeo/clusterplex
- **Language:** JavaScript (Node.js)
- **Activity:** 587 stars, last push **2026-02-12** (actively maintained, unlike kube-plex/Unicorn)

**Architecture (<=8 lines), read directly from source
([pms/app/transcoder.js](https://github.com/pabloromeo/clusterplex/blob/master/pms/app/transcoder.js),
[worker/app/worker.js](https://github.com/pabloromeo/clusterplex/blob/master/worker/app/worker.js),
[orchestrator/orchestrator.js](https://github.com/pabloromeo/clusterplex/blob/master/orchestrator/orchestrator.js)):**
On the PMS container, "Plex Transcoder" is renamed to `originalTranscoder` and replaced by a
Node.js shim. The shim captures full `argv`+`cwd`+`env`, rewrites Plex's embedded progress-callback
URL (`http://127.0.0.1:<port>`) to point at a Local Relay (an nginx forward-proxy on the PMS box),
and POSTs the whole job over a Socket.IO WebSocket connection to an **Orchestrator** service. The
Orchestrator picks an available **Worker** (load-balanced by active-task count) and forwards the
job over its own WebSocket. The Worker spawns the *real* Plex Transcoder binary locally with the
same argv/cwd/env, reads media and writes transcode output from/to **the same shared RWX volume**
mounted at identical paths on PMS and every worker, and reports task status back over the socket.
If the orchestrator round-trip fails (or `TRANSCODE_OPERATING_MODE=local`), the shim transcodes
locally as a synchronous child process instead.

**Hard requirements** (from
[README §Requirements/§Shared Storage](https://github.com/pabloromeo/clusterplex#requirements)):
RWX shared storage (NFS/SMB/Ceph/GlusterFS/Longhorn) for media libraries **and** the `/transcode`
dir, identical paths on PMS and every worker, mandatory. Codecs are downloaded per-worker
architecture at container startup (not shared). PMS's own `/config` (SQLite DB etc.) explicitly
does **not** need to be shared -- Plex's SQLite handles network storage/locking badly, so this is
called out as a thing to *avoid* sharing.

**What breaks (issues):**
- **Plex sends SIGKILL, not SIGTERM, on quick start/stop.** Maintainer confirms directly:
  *"It seems plex sends the unfriendly sigkill instead of sigterm signal, so it's difficult to
  handle being killed and do cleanup consistently. I'm gonna try a different approach..."* -- an
  "experimental" branch had to track processes differently to shut down gracefully "even if
  plex sends sigkill." [Issue #257](https://github.com/pabloromeo/clusterplex/issues/257)
  ("PMS originalTranscoder does not exit; cannot delete session directory"). This is the same
  fundamental problem rffmpeg has, hit independently by a different project.
- **Plex's bundled ffmpeg ("Plex Transcoder") is closed-source and incompatible with vanilla
  ffmpeg** -- a contributor tried building a 3rd-party Worker for NVIDIA Jetson using stock
  ffmpeg and found *"Plex's version of FFMPEG is incompatible with normal FFMPEG. Even removing
  all of the unrecognized options the stream would not work"* -- believed related to the
  progress-callback ("progressUrl") behavior baked into Plex's private ffmpeg build. [Issue #153](https://github.com/pabloromeo/clusterplex/issues/153).
- **No codec/hardware-capability-aware worker selection** -- load balancing is purely by active
  task count, not by which worker can hardware-decode a given codec; maintainer confirms this
  isn't implemented. [Issue #370](https://github.com/pabloromeo/clusterplex/issues/370).
- **Plex version pinning is brittle** -- a Plex point release changed how codec-version strings
  are exposed, breaking automatic codec downloads for workers until patched.
  [Issue #317](https://github.com/pabloromeo/clusterplex/issues/317).
- Various HW-passthrough/driver-mismatch reports across Docker Swarm and k8s
  ([#336](https://github.com/pabloromeo/clusterplex/issues/336), [#321](https://github.com/pabloromeo/clusterplex/issues/321), [#310](https://github.com/pabloromeo/clusterplex/issues/310) HDR HW transcode failures, [#223](https://github.com/pabloromeo/clusterplex/issues/223) worker can't find `iHD_drv_video.so`).
- **Intra-file/parallel work-splitting was requested and rejected as unimplemented** -- a single
  large (e.g. 4K) file can't be chunked across multiple workers, only whole-session-to-one-worker
  dispatch exists. [Issue #262](https://github.com/pabloromeo/clusterplex/issues/262) ("Distributed
  single-file transcoding") -- see §6 lesson 8 for why this keeps getting rejected everywhere it's
  tried, not just here.
- **Jobs sometimes bounce to a second worker right at playback start**, causing large start-of-playback
  delays -- an orchestration race/health-check flakiness bug, closed stale, unresolved.
  [Issue #266](https://github.com/pabloromeo/clusterplex/issues/266).

**Reusable for our design (this is the most directly useful prior art of the bunch):**
1. **Explicit out-of-band kill RPC.** `orchestrator.js` implements `worker.task.kill` -- a
   discrete WebSocket message the orchestrator sends to a specific worker socket, which calls
   `.kill()` on the tracked child process
   ([worker.js:230-238](https://github.com/pabloromeo/clusterplex/blob/master/worker/app/worker.js#L230-L238)).
   This is triggered when the PMS-side job-poster's WebSocket disconnects (i.e. Plex killed the
   local shim process, which drops its socket, which the orchestrator's
   `jobPosterDisconnectionHandler` detects and turns into a kill message to whichever worker was
   running that job) -- [orchestrator.js:440-490](https://github.com/pabloromeo/clusterplex/blob/master/orchestrator/orchestrator.js#L440-L490).
   This is a fundamentally better pattern than rffmpeg's "hope the SSH PTY forwards the signal":
   an explicit control-plane RPC with the process reference held in a map (`taskMap`), independent
   of the transport used to relay stdin/stdout.
2. Rewriting the embedded "call me back at 127.0.0.1" URL to route through a local reverse
   proxy is a clean pattern for any callback-URL-bearing transcoder invocation.
3. Special-casing operations that must always run locally regardless of remote availability
   (ClusterPlex hardcodes this for its EasyAudioEncoder audio codec and Plex's credits-detection
   feature) is a pattern worth carrying forward for anything Jellyfin does that assumes local
   filesystem access mid-transcode.

---

## 3. UnicornTranscoder / UnicornLoadBalancer, and kube-plex

### 3.1 UnicornTranscoder (github.com/UnicornTranscoder org)

- **UnicornTranscoder/UnicornTranscoder** -- https://github.com/UnicornTranscoder/UnicornTranscoder -- JS, 726 stars, last push 2023-03-02 (**stale, ~3 years dead**)
- **UnicornTranscoder/UnicornFFMPEG** -- https://github.com/UnicornTranscoder/UnicornFFMPEG -- JS, 95 stars, last push 2022-12-09
- **UnicornTranscoder/UnicornLoadBalancer** -- https://github.com/UnicornTranscoder/UnicornLoadBalancer -- JS, 125 stars, last push 2023-03-04

**Architecture (per [README](https://github.com/UnicornTranscoder/UnicornTranscoder/blob/master/README.md), 5 lines):**
Client requests a stream from Plex -> `UnicornLoadBalancer` intercepts and responds with an **HTTP
redirect** (307 for stream-chunk requests, verified in source at
[`routes/transcode.js:26`](https://github.com/UnicornTranscoder/UnicornLoadBalancer/blob/master/src/routes/transcode.js#L26)
-- see §5.1 for the precise distinction from the separate 302 download-link path) straight to a
chosen `UnicornTranscoder` instance's own address -> the transcoder
requests the source media from the real PMS over HTTP -> PMS launches its "Plex Transcoder" binary,
which has been replaced by `UnicornFFMPEG`, a stub that forwards the ffmpeg argv to
`UnicornLoadBalancer` -> the target `UnicornTranscoder` pulls those args, runs real ffmpeg **on its
own local disk**, and **serves the resulting HLS stream directly to the client itself** -- the
client's connection never routes back through PMS at all for the data plane, only the initial
redirect does.

**Key distinction -- this is the standout "no shared storage" architecture in this whole survey.**
It needs *no* shared filesystem whatsoever: the transcoder node fetches source bytes over HTTP and
serves output bytes over HTTP, both directly, using itself as the terminus rather than relaying
through the media-server. The tradeoff: the transcoder must be reachable directly by the client
(public DNS/TLS per node, per the README's `instance_address`/routing config for
GeoIP-based multi-region routing), and Plex's private ffmpeg build must be extracted/matched
exactly (`plex_build`, `codecs_build`, `eae_version` must all match the PMS's own binary strings,
per the README's `strings "Plex Media Server" | grep ...` instructions) -- same version-lock
problem as everyone else, just solved by literally scraping version identifiers out of the PMS
binary at setup time.

**Status:** confirmed dead -- no pushes since March 2023, no evidence it was ever ported to
Jellyfin (Jellyfin's community explicitly wished for this: see [Cluster Support feature request
comment, §4.1](https://features.jellyfin.org/posts/259) below -- *"There is a git on multi-node
transcoding for plex called unicorn-transcoder... It would be great to see this forked off and
used for Jellyfin"*, 2020, never happened).

### 3.2 kube-plex (munnerz/kube-plex)

- **Repo:** https://github.com/munnerz/kube-plex -- Go, 1243 stars, last push 2023-03-28 (not
  archived, but **effectively dead** -- a Jellyfin community member states directly: *"kube-plex...
  no longer works due to changes in plex"*, [rffmpeg#34 comment](https://github.com/joshuaboniface/rffmpeg/issues/34)).

**Architecture (5 lines, confirmed by reading `main.go`):**
Same "replace Plex Transcoder with a shim" pattern as everyone else, but the shim (a Go binary,
not a script) calls the Kubernetes API directly (`client-go`) to create a **Pod per transcode
job** rather than talking to a persistent worker fleet -- one pod, one transcode session, torn
down after the shim blocks on polling pod phase to completion. The pod runs the **identical PMS
container image** (pinned via a `PMS_IMAGE` env var), which gets ffmpeg-build parity "for free"
by construction rather than by manual version-matching. The pod mounts three volumes matching
PMS's own: media data (read-only), `/config` (read-only), and `/transcode` (read-write) -- all
backed by a single ReadWriteMany PersistentVolume (NFS/EFS-class) shared with PMS. The shim
rewrites the transcoder's `-progressurl`/`-manifest_name`/`-segment_list` arguments from
`127.0.0.1:32400` to point at PMS's real internal address, which requires disabling Plex's
auth-on-local-network protection for the pod CIDR so pods can report progress back to PMS
unauthenticated (no relay/proxy trick like ClusterPlex's Local Relay). No load-balancing logic at
all beyond "let Kubernetes scheduler place the pod" -- simplest of all the architectures surveyed,
and the first to break when Plex's transcoder invocation contract changed.

---

## 4. Jellyfin-specific attempts and maintainer stance

**Bottom line up front:** on the public feature-request tracker, the picture is stark. Jellyfin's
own feature-request tracker (https://features.jellyfin.org, a Fider instance, queried directly
via its API) shows **zero** feature requests related to distributed/remote/offloaded transcoding
have ever received an official team response (`"response": null` on every single one below) or a
status other than `open`/`declined`-by-the-community. No roadmap commitment exists there. The
community's answer to all of them, consistently since 2020, is "use rffmpeg." But on GitHub
proper, the picture is more nuanced: core team members *have* engaged substantively with the
architecture question, just not on the Fider board -- see §4.2's `jellyfin-meta` discussion,
which is the most important single finding in this section.

### 4.1 GitHub Feature Requests (features.jellyfin.org, Fider)

| # | Title | Status | Votes | Link |
|---|---|---|---|---|
| 873 | Cluster Support | open | 82 | https://features.jellyfin.org/posts/873/cluster-support |
| 259 | Add support for multi-machine encoding/converting | open | 18 | https://features.jellyfin.org/posts/259 |
| 2310 | FFMPEG Workers/Load Balancing like Tdarr | open | 7 | https://features.jellyfin.org/posts/2310 |
| 1127 | Allow multiple transcode devices/fallback | open | 5 | https://features.jellyfin.org/posts/1127 |
| 2836 | Docker rffmpeg support | open | 1 | https://features.jellyfin.org/posts/2836 |

Notable comments (all direct quotes):
- On #873 (2024, `vipervire`): *"Clustering support is a whole bunch of complexity and scope
  you're asking of a community project... Having a Jellyfin VM in an HA hypervisor cluster and
  offloading transcoding using rffmpeg is probably the best solution we have"* -- i.e. even the
  community's own answer to "can Jellyfin cluster" is "no, use rffmpeg as a workaround," not "yes,
  this is planned."
- On #873 (2020, `Christopher`): true multi-instance Jellyfin (shared state, not just shared
  transcoding) is blocked on migrating off SQLite to an external DB -- as of the last visible
  comment (2022) that rewrite had no visible progress ("Is this just a pipe dream?"). This is a
  separate axis from ffmpeg-offload (a transcode-offload plugin doesn't need this), but explains
  why Jellyfin itself has never built first-party clustering.
- On #259 (2020, `wazzaguy`): explicitly asks for Plex's UnicornTranscoder (see §3.1) to be
  ported to Jellyfin -- never happened.
- On #2836: confirms rffmpeg is "mentioned in the official documentation" as the recommended
  approach, but not bundled in official Docker images -- asks for first-party inclusion, still
  open, no response.

### 4.2 jellyfin-meta discussion #36 -- the actual Jellyfin core architectural debate on this exact topic

**[jellyfin/jellyfin-meta discussion #36](https://github.com/jellyfin/jellyfin-meta/discussions/36)**,
"[Proposal] FFmpeg call handling refactoring and FFmpeg remote integration" -- opened by
community member `JPVenson` on **2023-02-02**, still **open with no implementation** as of the
most recent comment (**2026-05-24**). This is the single most directly relevant piece of prior
art found in this whole survey: it is Jellyfin's own core team debating, in public, more or less
exactly what we're trying to build.

- **The original proposal**: consolidate Jellyfin's scattered ffmpeg-invocation code into a
  structured `JellyfinFfmpegService` abstraction that could dispatch to either a local process or
  a separate "JFRS" (Jellyfin Remote FFmpeg Server) -- a standalone service that accepts
  serialized transcode requests, runs them independently, and returns results via a push or pull
  mechanism. Also raises whether this should be first-party or a third-party plugin.
- **`gnattu` (Jellyfin org member), 2024-12-29, pushes back hard on wrapping ffmpeg itself**:
  > "FFmpeg, at least in the context of jellyfin, isn't an executable on its own." It depends
  > heavily on the specific kernel, drivers, and hardware of the machine it runs on -- "the
  > transcoder's behavior varies significantly across different environments... **This is what
  > rffmpeg currently failed to handle and is already causing issues for users.**"

  His counter-proposal is the closest thing to official design guidance available anywhere in
  this survey, and it validates our project's basic shape: abstract the **whole transcoding
  service** behind a defined task API (not the raw ffmpeg command line), each remote node reads
  media from **its own local mount** ("ffmpeg works best with filesystem inputs directly" --
  i.e. don't stream *input* to the worker over the network either), and each node **serves its
  own output/cache via its own API** rather than writing back to a shared filesystem -- with
  message-queue transport (RabbitMQ/Kafka) suggested as plausible plumbing. He explicitly says
  the media source should *not* be streamed from the main server to the worker.
- **`nyanmisaka` (jellyfin-ffmpeg maintainer), 2026-05-24**: separately notes a plain
  argv-wrapping approach "cannot handle `-filter_complex`" cleanly, reinforcing gnattu's point
  that passthrough-the-argv designs (rffmpeg's whole model) hit a structural ceiling; also
  mentions resuming work on HW-capability probing in a modified `ffprobe` (see jellyfin-ffmpeg
  [PR #733](https://github.com/jellyfin/jellyfin-ffmpeg/pull/733), "Add HW caps detection and
  usage monitoring to ffprobe", open/WIP) as a step toward an eventual transcoder refactor.
- **Bottom line**: three years of open discussion, real architectural convergence on
  "independent task-based service, not argv-wrapping," zero implementation. Nobody has built the
  thing gnattu described. That's the gap this project would fill.

### 4.3 jellyfin/jellyfin#481 -- the closest thing to an explicit maintainer roadmap statement

**[Issue #481](https://github.com/jellyfin/jellyfin/issues/481)**, "Multiple Servers Support"
(closed). Maintainer `cvium` first asks for the use case, then maintainer **`joshuaboniface`**
(Jellyfin core team, also rffmpeg's own author) states directly:

> "The idea of clustering servers (to provide better transcoding scalability and redundancy) has
> been discussed previously, however that's not a short-term feature." Later: "This does seem
> like a cool idea... it wouldn't be on the roadmap very soon... probably the best way to
> implement this is actually the DB work" -- i.e. real clustering is gated behind Jellyfin first
> getting external/shared-database support, which still doesn't exist as of this survey.

Read together with §4.2, the picture is consistent: the person best positioned to build
first-party distributed transcoding into Jellyfin (joshuaboniface) instead built rffmpeg as an
*external* wrapper, and has said elsewhere that a real stateless distributed tool is "way beyond"
rffmpeg's own scope ([rffmpeg#29](https://github.com/joshuaboniface/rffmpeg/issues/29)) --
nobody at Jellyfin core has claimed they're building the real thing.

### 4.4 jellyfin/jellyfin GitHub issues on transcoder failover and throttle/pause fragility

**[Issue #4389](https://github.com/jellyfin/jellyfin/issues/4389)**, "Implement
(hardware)transcoder failover/prioritisation" (open since 2020-10-29, 39+ comments). Not about
remote offload, but the richest thread on how Jellyfin actually invokes ffmpeg today, useful
ground truth for anyone building a shim/plugin:

> "HLS requests -> `DynamicHlsController.cs` -> `GetDynamicSegment()` -> `GetSegmentResult()` ->
> `StartFfMpeg(ffmpeg_cli)` ... `EncodingHelper.cs` -> `GetCommandLineArguments()` -> `ffmpeg_cli`"

Also confirmed in that thread: every retry of a failed transcode creates a **brand-new
StreamState** (session) -- Jellyfin does not resume or retry an existing ffmpeg invocation, and
whether/how many times the client retries a failed HLS segment fetch is entirely client-side
(HLS player) behavior, not server-driven. This matters for a remote-offload design: there is no
existing "resume this transcode on a different node" concept to hook into -- every fallback is a
cold restart from Jellyfin's perspective. `nyanmisaka` states the intended failover chain is
NVENC -> QSV -> other HWA -> `libx264`, and in 2024 committed to spending time on it, "this
requires many changes, including ffmpeg, server, and web" (tracked partly via jellyfin-ffmpeg
[PR #733](https://github.com/jellyfin/jellyfin-ffmpeg/pull/733)). Contributor `PrplHaz4` notes
proper failover "might also solve" [rffmpeg#3](https://github.com/joshuaboniface/rffmpeg/issues/3)
(remote hosts needing different HW-accel parameters than the primary server). User `themcv`
reports personally trying to "cluster Jellyfin" and hitting the same wall every project in this
survey hits -- remote nodes without the primary's exact NVENC/GPU setup simply don't work, and
their own workaround was a shell-script wrapper hack, not a real solution.

**Throttle/pause fragility** -- two related issues confirm §6 lesson 1 independently of rffmpeg:
[Issue #11465](https://github.com/jellyfin/jellyfin/issues/11465) ("Playback stops after a while
when throttle transcodes is enabled") and [Issue #8082](https://github.com/jellyfin/jellyfin/issues/8082)
("Throttle Transcodes stops transcoding completely after slowing it down first"). `nyanmisaka`
clarifies the mechanism is **not** a process signal (not SIGSTOP) -- jellyfin-ffmpeg reads a
stdin keyboard-command protocol (`Enter command: <target>|all <time>|-1 <command>[ <argument>]`,
keys `c`/`u`/`p`) and the root cause of the stuck-playback bug is that the *client* doesn't
reliably report playback position back to the server, so the server has no signal to send the
resume command on. `gnattu` on the feature's fundamental fragility:

> "The throttle transcoding will break for a lot of reasons and it is almost impossible to
> resolve because the way it works is just that fragile. If it continues to cause playback
> issues, **we are even considering the complete removal of this feature.**"

Separately, contributor `ericswpark` proposed splitting a single transcode into parallel
chunked ffmpeg invocations (to speed up a single stream by using multiple encoders/workers at
once) in the same issue thread; `nyanmisaka` rejected it, citing VRAM exhaustion from running
multiple concurrent HW encode sessions, realtime constraints on consumer iGPUs, and the fact
that HLS's keyframe-only seek model causes visible skip/repeat artifacts at chunk boundaries
under a naive split. **This is independent confirmation, from inside Jellyfin core, of the same
conclusion ClusterPlex reached with its own users** (§2, issue #262): nobody has made
intra-file parallel transcoding work, for real technical reasons, not just lack of effort.

### 4.5 Third-party plugin/tool attempts found

- **JacquesToT/transcodarr** -- https://github.com/JacquesToT/transcodarr -- Shell, 4 stars,
  created **2026-01-15**, last push 2026-01-16 (one day of activity, very new/small). *Not* an
  independent architecture -- it's an install/automation script wrapping upstream rffmpeg
  specifically for a Synology NAS + Apple Silicon Mac (VideoToolbox) worker setup. Useful as a
  **current, real-world confirmation of rffmpeg's known pain points**, stated in its own README:
  *"rffmpeg uses SQLite to track active transcoding jobs. Starting 4+ streams at the exact same
  moment can cause database lock conflicts, resulting in nodes being marked as 'bad'"*, and
  *"rffmpeg doesn't actively load balance based on current CPU usage. It distributes transcodes
  sequentially, roughly following the weight ratio... the slower Mac may become overloaded while
  the faster one sits idle."* Also explicitly documents the security tradeoffs of SSH-key-in-
  container + open NFS + `chmod 777` transcode dirs that this style of setup requires. First
  prior-art example targeting Apple Silicon/VideoToolbox specifically.
- **mugurc/jellyfin-plugin-pre-transcode** -- pre-transcodes library media in the background
  (Tdarr/Unmanic-style, not live playback offload); README lists "distributed/off-box encoding"
  as a *future* feature, not implemented.
- No other genuinely independent (non-rffmpeg-based) third-party Jellyfin plugin for live
  transcode offload was found. This appears to be a real gap -- despite 82 votes on the cluster
  feature request since 2020, nobody has built a Jellyfin-native equivalent of ClusterPlex or
  UnicornTranscoder.

---

## 5. Streaming output back over the network instead of shared storage

This is the part of the design space most sparsely covered by existing prior art -- almost
everything surveyed above defaults to shared NFS/SMB. What was found:

1. **UnicornTranscoder (§3.1) is the closest real match**, but it solves a different problem
   than "stream output back to the media server": it makes the *transcoder itself* the terminus
   the client talks to, rather than pushing bytes back to a central server that still fronts the
   client. Verified directly in source
   ([`UnicornLoadBalancer/src/routes/transcode.js:26`](https://github.com/UnicornTranscoder/UnicornLoadBalancer/blob/master/src/routes/transcode.js#L26)):
   the main stream-chunk redirect is `res.redirect(307, server + req.url)` -- **HTTP 307**, not
   302 (307 preserves the request method, which matters for range-request chunk fetches); a
   *separate*, optional `CUSTOM_DOWNLOAD_FORWARD` config path uses a 302 specifically for
   direct-play/download links (per `UnicornLoadBalancer/README.md`). Either way, that's a valid
   alternative architecture but changes Jellyfin's trust/TLS/network model significantly (every
   worker becomes a public-facing streaming endpoint) -- worth knowing about, probably not what
   we want to replicate directly.

2. **FFmpeg's HLS muxer has a first-class, built-in "push over HTTP" mode that nothing surveyed
   here actually uses, and it was empirically verified working in this survey, not just read from
   docs.** `-method PUT` on the `hls` muxer: `ffmpeg -i in.ts -f hls -method PUT
   http://example.com/live/out.m3u8` uploads every `.ts`/`.m4s` segment *and* updates the `.m3u8`
   playlist via HTTP PUT as they're produced -- no local file is ever written on the transcoding
   machine if the receiving HTTP server accepts arbitrary PUT paths. A live experiment run in
   this survey (`exp/put_server.py`, a logging Python HTTP server, against real `jellyfin-ffmpeg`
   HLS output) confirmed this in practice: ffmpeg PUT-uploaded numbered `.ts` segments and
   repeatedly re-PUT the updated `stream.m3u8` playlist after each new segment, using **chunked
   Transfer-Encoding** (no `Content-Length` header -- consistent with `chunked_post` defaulting
   to on); fMP4 segmented output (`init.mp4` + `seg_NNN.m4s`) was tested and works identically.
   Connection reuse was also confirmed: runs with `-http_persistent 1` (the default) show
   `Connection: keep-alive` and reuse one TCP/HTTP connection across all segment+playlist PUTs of
   a session, versus `Connection: close` without it -- meaningful for reducing per-segment
   overhead at scale. One `ConnectionResetError` was observed server-side during a
   persistent-connection run in the raw experiment log -- treat mid-session connection drops as
   expected, not exceptional, in production. Relevant additional native ffmpeg options confirmed
   in the docs: `ignore_io_errors` ("useful for long-duration runs with network output" -- exactly
   this use case), and the `reconnect`/`reconnect_on_network_error`/`reconnect_delay_max`
   family (input-side, but the same protocol-layer resilience options exist generally). This is
   the single most directly reusable technical finding of this survey: rather than inventing a
   custom segment-relay protocol, a remote agent can point ffmpeg's own output at
   `http://<jellyfin-host>:<port>/agent-upload/<session>/segment%d.ts` and let ffmpeg do the
   network I/O itself, with Jellyfin server-side running a small receiver that writes into
   (or serves straight out of) its existing transcode-cache path. None of rffmpeg, ClusterPlex,
   kube-plex, or UnicornTranscoder use this -- they all either share a filesystem or make the
   worker the client-facing origin.

3. **`rffmpeg` itself explicitly endorses SSHFS as a drop-in NFS replacement** -- maintainer
   confirms *"If you mean SSHFS instead of NFS... yes this will work fine"*
   ([rffmpeg#92](https://github.com/joshuaboniface/rffmpeg/issues/92)) -- but this is still a
   shared-mount model, just over a different transport; it doesn't remove the "identical paths
   everywhere" constraint, and a requester in the same thread wanted automatic reverse-SSHFS
   mounting specifically to avoid manual NFS setup, not to avoid shared storage conceptually.

4. **Generic distributed-ffmpeg "farm" tools exist but solve a different problem: batch
   re-encoding, not live on-demand playback transcoding.** All of these split a whole file into
   segments, farm the segments out for parallel one-shot encoding, then concatenate -- they are
   not built for a single continuous transcode tied to a live, seekable playback session with a
   real-time deadline on the first segment:
   - [Rouji/ffmpeg_distributed](https://github.com/Rouji/ffmpeg_distributed) (Python, 4 stars) -- splits input into segments, SSHes each to a remote host, concatenates results.
   - [ccremer/clustercode](https://github.com/ccremer/clustercode) (Go, 185 stars, actively maintained -- last push 2025-11-03) -- Kubernetes-native, one Pod per segment.
   - [michaelelleby/ffmpeg-farm](https://github.com/michaelelleby/ffmpeg-farm) (C#, 13 stars, dead since 2020).
   - [bfansports/CloudTranscode](https://github.com/bfansports/CloudTranscode) (PHP, 300 stars, active) -- AWS Step Functions-orchestrated; notably **does support pulling source media over HTTP** rather than requiring shared storage for input, confirming ffmpeg-over-HTTP-input is a well-trodden pattern even if HTTP-push-output isn't.
   None of these were investigated further in depth (out of scope: batch, not live), but they
   confirm the general pattern of "no shared storage, HTTP in, HTTP or object-storage out" is
   viable for ffmpeg workloads generally -- it just hasn't been applied to the live-transcode
   case that Jellyfin/Plex need.

5. **No project found writes ffmpeg's own stdout directly as the transport with zero local
   file** for HLS/segmented output specifically -- and there's a structural reason why: ffmpeg's
   `segment`/`hls` muxers manage multiple discrete output files (open/close/rename per segment),
   which is incompatible with a single `pipe:1` stdout stream. The `-method PUT` mechanism in
   point 2 above is the closest ffmpeg gets to "no local file," and it still goes through
   ffmpeg's own HTTP client rather than stdout piping.

6. **The sending side of "no shared storage" is a solved problem (point 2); the receiving side
   is not, and nobody surveyed here has built it.** Jellyfin's own `DynamicHlsController`
   currently trusts ordinary filesystem semantics -- a segment file exists on disk, or it doesn't
   -- as its readiness signal for serving a segment to a client. A network-received segment must
   never be exposed to a client while still mid-upload (partial PUT body, chunked-encoding
   in-flight, etc.), which means the receiver needs write-to-temp-then-atomic-rename (or
   equivalent) semantics on PUT completion, plus a policy for what happens to a client request
   that arrives for a segment the receiver hasn't finished accepting yet (block-and-wait vs.
   404 vs. fall back to a placeholder). This was analyzed conceptually only in this survey, not
   prototyped or tested -- it is the genuine greenfield engineering work this project has to do;
   everything else in §5 is either "already solved by ffmpeg" or "solved differently by someone
   else's architecture."

**Adjacent live-streaming precedent** (directional context only, not deeply researched with the
same rigor as the rest of this survey): distributed live-broadcast tooling built on SRS,
OvenMediaEngine, or generic RTMP/SRT push solves a structurally similar "encoder output needs to
reach a central server over the network, progressively, in near-real-time" problem using push
protocols rather than shared storage -- reinforcing that push-based delivery is a well-worn
pattern in adjacent domains, even though nobody has applied it inside the Jellyfin/Plex
remote-transcoding niche specifically.

---

## 6. Lessons for our design -- top 10 gotchas, each with a source

1. **A media-server-triggered "quit"/"pause" isn't a signal -- it's often a raw character
   written to the ffmpeg process's stdin** (`p`/`u` to pause/resume in Jellyfin's case, per
   jellyfin-ffmpeg maintainer `nyanmisaka`). Any transport that doesn't transparently forward
   stdin bytes in real time (or that buffers/delays them) will silently break pause/resume and
   can strand playback indefinitely. Jellyfin core is aware this feature is fundamentally
   fragile -- `gnattu` on the throttle/pause mechanism: *"it is almost impossible to resolve
   because the way it works is just that fragile... we are even considering the complete removal
   of this feature."* If it does get removed upstream, this specific problem disappears; if it
   doesn't, a remote-offload design still has to solve it. -- [rffmpeg#76](https://github.com/joshuaboniface/rffmpeg/issues/76), [jellyfin/jellyfin#11465](https://github.com/jellyfin/jellyfin/issues/11465), [#8082](https://github.com/jellyfin/jellyfin/issues/8082)

2. **The media server will SIGKILL your local shim process on quick stop/seek, not SIGTERM --
   so the fix is an explicit, addressable "kill this job" control-plane message, not signal
   forwarding.** Both rffmpeg (indirectly, via unowned SSH children going orphaned) and
   ClusterPlex (directly confirmed by its maintainer: *"it seems plex sends the unfriendly
   sigkill instead of sigterm signal, so it's difficult to handle being killed and do cleanup
   consistently"*) hit this. SIGKILL cannot be caught, so any "clean up the remote job in my
   signal handler" design is fundamentally unreliable. ClusterPlex's actual fix -- a
   `worker.task.kill` WebSocket RPC (tied to a `taskId` in a server-side map), triggered when the
   orchestrator detects the PMS-side control socket disconnect -- is a strictly more robust
   pattern than rffmpeg's "hope `ssh -t` forwards SIGINT/EOF correctly," and it's the only
   project surveyed that gets this right by construction rather than by luck: liveness of the
   *control connection*, not a local process's chance to run cleanup code, is what should drive
   remote-job teardown. -- [ClusterPlex#257](https://github.com/pabloromeo/clusterplex/issues/257), [worker.js:230-238](https://github.com/pabloromeo/clusterplex/blob/master/worker/app/worker.js#L230-L238)

3. **A Jellyfin core maintainer has already sketched an architecture close to ours -- treat it
   as the closest thing to official design guidance that exists.** In a three-year-old, still-open
   internal debate about exactly this problem, `gnattu` rejected wrapping ffmpeg's argv (rffmpeg's
   whole model) as unworkable long-term -- *"FFmpeg... isn't an executable on its own... This is
   what rffmpeg currently failed to handle and is already causing issues for users"* -- and
   proposed instead: an independent transcode-node service behind a task API (not raw argv), each
   node reading media from its **own local mount** (not streamed from the main server), each node
   serving its **own output/cache via its own API** (not writing to shared storage). Nobody has
   built this. It remains an open discussion, not a roadmap item -- but it's real validation that
   Jellyfin core's own thinking converges on our project's basic shape. --
   [jellyfin-meta discussion #36](https://github.com/jellyfin/jellyfin-meta/discussions/36)

4. **Hardware-acceleration compatibility is never negotiated by any of these tools -- it's
   entirely the operator's job to keep it identical everywhere, and every project treats
   "rewriting/mangling the transcode arguments to fit the chosen host" as explicitly out of
   scope.** If our design wants smarter placement (route AV1/HEVC decode to the one node with
   the right GPU), we're doing something none of rffmpeg, ClusterPlex, or kube-plex ever
   attempted -- it's an open, repeatedly-requested feature nobody has shipped, though jellyfin-ffmpeg's
   own WIP HW-capability-probing work (`ffprobe` reporting GPU/codec caps as JSON) is a first
   sign this could eventually become tractable from Jellyfin's own side. --
   [rffmpeg#75](https://github.com/joshuaboniface/rffmpeg/issues/75),
   [rffmpeg#66](https://github.com/joshuaboniface/rffmpeg/issues/66),
   [ClusterPlex#370](https://github.com/pabloromeo/clusterplex/issues/370),
   [jellyfin-ffmpeg PR #733](https://github.com/jellyfin/jellyfin-ffmpeg/pull/733)

5. **The exact ffmpeg build matters, not just the ffmpeg version number.** Both rffmpeg
   (jellyfin-ffmpeg package version must match `dpkg -l` output exactly) and ClusterPlex/Unicorn
   (Plex's ffmpeg is a private, closed-source build with baked-in progress-callback behavior
   that a contributor confirmed is *"incompatible with normal FFMPEG"*) hit version/build
   mismatches as a recurring, hard-to-diagnose failure class. A remote-offload design should
   either pin/ship the exact jellyfin-ffmpeg binary to agents itself, or validate the agent's
   ffmpeg build fingerprint before dispatching work to it. -- [rffmpeg docs/SETUP.md](https://github.com/joshuaboniface/rffmpeg/blob/master/docs/SETUP.md), [ClusterPlex#153](https://github.com/pabloromeo/clusterplex/issues/153), [ClusterPlex#317](https://github.com/pabloromeo/clusterplex/issues/317)

6. **Shared-filesystem designs pay a real, user-visible latency tax that's easy to miss in
   testing.** rffmpeg's own docs call out 15-60 second playback-start delays caused by NFS
   attribute caching alone, fixable only by tuning `actimeo`/`sync` mount options -- a subtlety
   that will bite anyone who doesn't specifically test playback-start latency on a network mount
   under realistic conditions. This is itself a strong argument for a network-native
   (HTTP-push) design over a shared-mount one, since it sidesteps this whole failure class. --
   [rffmpeg docs/SETUP.md §NFS Setup](https://github.com/joshuaboniface/rffmpeg/blob/master/docs/SETUP.md)

7. **Naive load and state management is universal across this ecosystem, and known-inadequate.**
   Every project surveyed load-balances purely by "how many jobs are currently running" (no
   CPU/GPU/queue-depth awareness) -- users report faster nodes sitting idle while slower ones are
   overloaded, as recently as a Jan-2026 real-world deployment writeup. Separately, naive local
   state stores don't survive concurrency: rffmpeg's SQLite state DB has confirmed lock-contention
   failures when 4+ streams start at the exact same instant, incorrectly marking healthy hosts
   "bad." Real CPU/GPU-load-aware scheduling and a concurrency-safe state store both remain
   unimplemented, repeatedly-requested gaps across the whole ecosystem. -- [Transcodarr README](https://github.com/JacquesToT/transcodarr), [ClusterPlex#370](https://github.com/pabloromeo/clusterplex/issues/370), [features.jellyfin.org #2310 "like Tdarr"](https://features.jellyfin.org/posts/2310)

8. **Intra-file/parallel splitting of a single transcode across multiple workers has been
   proposed and rejected at least twice, independently, for real technical reasons -- don't
   attempt it.** ClusterPlex's own users asked for it and it was closed unimplemented
   ([#262](https://github.com/pabloromeo/clusterplex/issues/262)). Inside Jellyfin core, the
   same idea (`ericswpark`, in the #4389 thread) was rejected by `nyanmisaka` citing VRAM
   exhaustion from concurrent HW encode sessions, realtime limits on consumer iGPUs, and visible
   skip/repeat artifacts from HLS's keyframe-only seek model at naive chunk boundaries. Distribute
   whole sessions to one worker each; don't try to parallelize within a file. -- [jellyfin/jellyfin#4389](https://github.com/jellyfin/jellyfin/issues/4389), [ClusterPlex#262](https://github.com/pabloromeo/clusterplex/issues/262)

9. **FFmpeg already has a built-in way to push segmented output over HTTP as it's produced
   (`-f hls -method PUT <url>`), and this survey confirmed it actually works via a live
   experiment -- nobody in the Jellyfin/Plex ecosystem uses it.** Every project either shares a
   filesystem or makes the remote worker the client-facing origin (UnicornTranscoder). The
   sending side (chunked-encoding segment/playlist PUT, persistent connections, `ignore_io_errors`
   for long-running network output) is a solved problem. The receiving side -- exposing an
   uploaded segment to clients only once it's fully and atomically written, never mid-upload --
   is not solved anywhere in this survey and is the actual greenfield engineering work. --
   live experiment (`exp/put_server.py` + real jellyfin-ffmpeg runs, this survey); absence
   confirmed across rffmpeg, ClusterPlex, kube-plex, UnicornTranscoder source read directly.

10. **Every one of these projects treats "distributed transcoding" as strictly out of scope for
    the upstream media server itself, and every maintainer conversation found ends the same
    way: community workaround tool, not first-party feature -- and the upstream ffmpeg-invocation
    contract you're built on can change without notice.** Jellyfin's own feature tracker shows
    82 votes on clustering since 2020 with zero official response (`joshuaboniface`, Jellyfin
    core and rffmpeg's own author, on record: *"that's not a short-term feature... it wouldn't be
    on the roadmap very soon"*); ClusterPlex, kube-plex, and UnicornTranscoder are all unofficial
    Plex forks/shims Plex Inc. has never adopted or endorsed, and kube-plex died outright when
    Plex changed its transcoder invocation contract without warning. Build for graceful
    degradation to local transcoding as the permanent fallback, not just the initial-rollout
    fallback. -- [jellyfin/jellyfin#481](https://github.com/jellyfin/jellyfin/issues/481), [features.jellyfin.org/posts/873](https://features.jellyfin.org/posts/873/cluster-support), [rffmpeg#34 comment re: kube-plex](https://github.com/joshuaboniface/rffmpeg/issues/34)

---

## What I did NOT verify

- **The exact current wording of Jellyfin's official transcoding docs page**
  (`jellyfin.org/docs/general/server/transcoding/`) regarding rffmpeg -- the page is a
  client-side-rendered SPA and both WebFetch and a raw `curl` returned only a ~380-byte shell
  with no rendered content. I could not confirm the precise text Jellyfin's own docs use to
  describe/endorse rffmpeg; I only have the community's characterization of it (a Fider commenter
  states it's "mentioned in the official documentation").
- **ClusterPlex's "experimental" SIGKILL-tolerant process-tracking branch** -- referenced by the
  maintainer in issue #257 but I did not locate or read that branch's actual code; I cannot
  describe its mechanism beyond the maintainer's one-sentence description ("tracks processes
  differently").
- **`-method PUT`'s happy path was verified live (§5.2), its failure/retry behavior was not.**
  The experiment harness (`exp/put_server.py`) supports `FAIL_PATHS`/`FAIL_COUNT` env vars to
  simulate a receiving server returning an error mid-segment-upload, but no run exercising that
  exists in the scratchpad logs -- this is the single biggest open verification gap before
  committing to a PUT-based design. Also not tested: whether ffmpeg's PUT mode is compatible with
  Jellyfin's specific segment-serving model end-to-end (byte-range seeking mid-segment, live
  playlist windowing) -- only the upload side was exercised, not a full loop back through a
  Jellyfin-like server to a real HLS client.
- **Receiving-side atomic segment exposure (§5.6)** -- analyzed conceptually only; no prototype
  receiver implementing write-to-temp-then-atomic-rename semantics was built or tested.
- **`jellyfin-meta` discussion #36's exact wording** (§4.2) was retrieved via an AI-summarizing
  fetch tool rather than reading the raw discussion HTML/API directly; quotes are reported as
  given by that tool and are very likely accurate but were not independently re-verified
  character-for-character against the GitHub page.
- **Cross-run issue-number consistency for ClusterPlex.** This survey's ClusterPlex findings were
  produced across multiple independent research passes; a spot-check of 9 issue numbers cited
  prominently (rffmpeg #76/#89/#75/#92, ClusterPlex #257/#266/#262/#317/#324/#223, jellyfin/jellyfin
  #481/#4389/#11465/#8082, rffmpeg PR #12/#90/#94, jellyfin-ffmpeg PR #733) confirmed all resolve
  to real issues/PRs with matching titles/themes via `gh issue view`/`gh pr view`, but not every
  citation in this document was individually re-verified this way -- treat issue numbers not
  explicitly listed here as sourced-but-not-independently-spot-checked.
- **CrystalNET-org/grpc-ffmpeg** -- found via search, repo description read, but I did not clone
  or read its source; included only as evidence that a gRPC-based transport has been attempted
  by someone else.
- **The full comment history on `features.jellyfin.org` posts beyond what the API returned** --
  I pulled `commentsCount` and the comments endpoint per post, which appeared complete for the
  posts checked, but did not cross-check against the web UI for pagination edge cases.
- **Reddit r/jellyfin threads specifically** -- targeted Reddit searches returned no on-topic
  results (search engine limitation, not confirmation of absence); I did not browse
  r/jellyfin directly via old.reddit.com or the Reddit API/search to rule out relevant threads
  existing.
- **jellyfin-plugin-pre-transcode's actual code** -- only its README was read (via search-result
  summary), not the source, to confirm the "future: distributed/off-box encoding" claim is
  genuinely unimplemented rather than partially started.
- Star counts, "last push" dates, and archived-status flags above are accurate as of
  **2026-08-28** (query time) and will drift.

---

## Addendum (main session, 2026-08-28) — second-look search

- **ZoltyMat/jellyfin-ha** (https://github.com/ZoltyMat/jellyfin-ha, 22★, tracks jellyfin master): a **server fork**, not a
  plugin. Runs multiple identical Jellyfin replicas (k8s) with an `ITranscodeSessionStore` / `RedisTranscodeSessionStore`
  (Lua takeover scripts, TTL leases); when a pod dies another pod resumes the HLS stream from the last durable segment.
  Each pod runs its own ffmpeg; the transcode volume must be `ReadWriteMany` because pods read each other's segments.
  → HA of a monolith, still shared storage, no transcode distribution. Not our thing, but the session-lease idea is
  reusable if we ever want two Jellyfin servers.
- **forum.jellyfin.org "Remote Transcoding"** (last post 2024-03-29): core member crobibero confirms the ffmpeg path can
  still be set via file config (`encoding.xml`), only the API/dashboard path was disabled for security — irrelevant to
  the plugin route, relevant to why wrapper setups are getting harder.
- rffmpeg README (2026) now notes Jellyfin ≥ 10.10 needs `TMPDIR` exported to the remote as well — another shared-path
  requirement piling onto the wrapper model.
- Searches run: "jellyfin plugin distributed transcoding remote worker agent 2026", "jellyfin ITranscodeManager plugin
  override remote ffmpeg", "jellyfin remote transcoding without shared storage HTTP segments push github". No project
  found that (a) is a plugin, (b) overrides the transcode manager in-process, or (c) pushes segments back over HTTP.
