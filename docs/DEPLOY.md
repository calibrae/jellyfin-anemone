# Deploy & test guide (first live run: 2026-09-02, speedwagon + trish — all green)

Target: speedwagon (Jellyfin 10.11.0, `Jellyfin.app`) + trish (M1, 10.240.0.2 over Thunderbolt, 10.10.0.8 on LAN).

## 0. Prereqs on trish
1. `sudo pmset -a sleep 0` (done), Homebrew not required.
2. jellyfin-ffmpeg: download the official portable build matching the server's 7.1.x
   (`jellyfin-ffmpeg_7.1.4-3_portable_macarm64-gpl.tar.xz`, tag `v7.1.4-3` — the last 7.1.x; 8.1.x is for the Jellyfin 12 line — from https://github.com/jellyfin/jellyfin-ffmpeg/releases/tag/v7.1.4-3),
   unpack to `/opt/anemone/` so `/opt/anemone/ffmpeg -version` prints `ffmpeg version 7.1.x-Jellyfin`. Then
   `xattr -dr com.apple.quarantine /opt/anemone` and run it once by hand. Alternative for a first smoke test: copy
   `/Applications/Jellyfin.app/Contents/MacOS/ffmpeg` from speedwagon (same build the server uses).
3. Mount the media share at the same path as speedwagon: `//cali@10.10.0.7/data` → `/Volumes/data`
   (reuse speedwagon's `~/smb-mount.sh` + LaunchAgent, or `mount_smbfs`). Verify `ls /Volumes/data/_tvshows | head`.
4. Check VideoToolbox works headless: `ssh trish '/opt/anemone/ffmpeg -f lavfi -i testsrc=duration=3:size=1280x720:rate=25 -c:v h264_videotoolbox -f null -'`.

## 1. Agent without Jellyfin (any box)
```
cd agent && cargo build --release
./target/release/anemone-mock --listen 127.0.0.1:8097 --secret dev --out-dir /tmp/anemone-out --job testsrc --once &
./target/release/polyp --server-url ws://127.0.0.1:8097/Anemone/agents/ws --secret dev --ffmpeg /opt/homebrew/bin/ffmpeg
```
Expect `testjob0.ts … testjob.m3u8` in `/tmp/anemone-out`, no `.part` leftovers, `exit code 0`. Type `q` in the mock's
terminal during a second run to check early stop.

## 2. Plugin in DRY-RUN on the live server
1. `scripts/package-plugin.sh && scripts/install-plugin-local.sh` (restarts Jellyfin).
2. Dashboard → Plugins → Anemone: set **DryRun = on**, generate a SharedSecret, IngestBaseUrl = `http://10.240.0.1:8097`. Save.
3. Play something that transcodes. `grep anemone ~/Library/Application\ Support/jellyfin/log/log_*.log` must show
   `anemone: Anemone plugin loaded` and, per transcode, `dry-run — would route …` or the reason it stays local.
   Playback must be byte-for-byte normal (the fork is running, remote path is not).
4. Rollback at any time: `rm -rf ~/Library/Application\ Support/jellyfin/plugins/Anemone_*` + restart.

## 3. Agent on trish
```
scripts/deploy-agent.sh trish ws://10.240.0.1:8097/Anemone/agents/ws /opt/anemone/ffmpeg
ssh trish  # edit ~/anemone/polyp.toml: secret = <the plugin's SharedSecret>
sudo ~/anemone/install.sh   # LaunchDaemon; logs in /var/log/polyp/
```
Dashboard → Plugins → Anemone: trish shows up in the agents table (hwaccels: videotoolbox, mount ✓, ffmpeg version).

## 4. First remote transcode
1. DryRun = off. Play the same item. Watch `tail -f /var/log/polyp/*.log` on trish and the FFmpeg.Transcode log
   on speedwagon (it says `anemone: routed to agent trish`).
2. Measure: time to first segment vs local; segment cadence; CPU on both boxes (`macmon` on trish).
3. Seek far ahead → Jellyfin kills and restarts the job (`-start_number N`); confirm the old job died on trish
   (`exit` frame) and the new one landed.
4. Stop playback → `q\n` reaches trish, ffmpeg exits 0 within a second; token revoked.
5. `pkill polyp` on trish mid-stream → speedwagon marks the job exited; next segment request restarts locally.

## Known gaps in v0 (by design)
Throttling off for remote jobs; subtitle burn-in, external subs, progressive, live TV, probing/trickplay stay
local; HTTP input fallback not wired (an agent needs the media on a mount of its own). Heterogeneous agents
are supported — see the abbacchio section below for a Linux/VAAPI agent whose media path differs from the
server's.


---

## Traps found during the first live deploy (2026-09-02)

1. **`POST /System/Restart` does NOT reload a plugin.** On macOS it is an *in-process* soft restart, and the
   CLR cannot unload an assembly — Jellyfin keeps serving the plugin build it loaded first, so a freshly
   built DLL is silently ignored and you debug a version that is no longer on disk. Quit and relaunch the
   app instead (`scripts/install-plugin-local.sh` now does this). Symptom: the log keeps reporting an error
   you already fixed, e.g. an assembly-version mismatch that no longer exists in the built DLL.
2. **Build against the server's exact Jellyfin version.** `Jellyfin.Controller` 10.11.11 against a 10.11.0
   server fails with `ReflectionTypeLoadException: Could not load ... MediaBrowser.Controller, Version=10.11.11.0`,
   reported as the misleading "plugin references an incompatible version of one of the shared libraries".
   The csproj now pins 10.11.0.
3. **`meta.json` is not optional.** Jellyfin only loads the DLLs whitelisted in the manifest's `assemblies`
   list; with no `meta.json` the plugin is discovered, logs nothing, and loads zero assemblies.
4. **Only the first `AddHostedService` from a plugin registrator actually starts.** `AnemoneListener` is
   therefore started by `AnemoneHostedService`, not registered separately.
5. Agent-side SMB mount: `/Volumes/<name>` must be created with `sudo` first (`/Volumes` is root-owned), then
   `mount_smbfs -o soft '//user:pass@host/share' /Volumes/data`.

## Live results (2026-09-02, speedwagon ↔ trish over the 10.240.0.0/30 TB4 link)

| Scenario | Result |
|---|---|
| Agent handshake | `agent "trish" connected (platform=macos-arm64 ffmpeg=7.1.4-Jellyfin maxSessions=3)`, mount `/Volumes/data` ok |
| Remote transcode (HEVC 1280 → h264 640x360) | routed to trish; ffmpeg ran **only** on trish with `-hwaccel videotoolbox`/`h264_videotoolbox`/`scale_vt`; segments PUT back to `:8097`; first segment served in **0.70 s**; ~15× realtime (25 segments / 5 s) |
| Delivered segment | probes as `h264 640x360` + `aac` |
| Stop (`DELETE /Videos/ActiveEncodings`) | `stopping remote job … with q command` → agent reports `exited code=0` 2 ms later, graceful |
| Seek (request segment 150) | job killed and restarted remotely with a new `-start_number`; segment served in 1.2 s |
| Agent death (`pkill polyp`) | `agent "trish" disconnected`; next transcode ran **locally** with local paths, served in 0.78 s — no user-visible failure |


## Running polyp on a macOS agent: two OS constraints (learned the hard way, 2026-09-02)

Both bite *only* when polyp is started by launchd; a polyp started from an ssh session works fine.
That is why trish currently runs it from a login shell, and the launchd plists are staged but not
loaded (`~/anemone/launchd-pending/`).

1. **SMB mounts are session-scoped.** A share mounted in an ssh session shows up in `mount` output
   system-wide, but `open()` on it from a *different* session (a launchd agent, or the system domain)
   blocks forever in the kernel. Symptom: polyp logs `ffmpeg probe complete` and then nothing at all,
   with every tokio worker parked and no socket open — it is stuck in `check_mount`. `sample <pid>`
   shows `probe::check_mount → read_dir → __opendir2 → open$NOCANCEL`. polyp now times the probe out
   after 5 s and reports the mount as unusable instead of wedging, but the agent still cannot *read
   media* through a foreign-session mount, so the mount must be made by whatever session runs polyp.
2. **Local Network privacy blocks launchd-started binaries.** A polyp started by launchd (system or
   `gui/<uid>` domain) fails every connection with `No route to host (os error 65)` while `nc` from an
   ssh session on the same host reaches the same port fine. macOS requires Local Network approval, and
   a background binary has no way to prompt for it. To make the LaunchAgent usable, approve it once in
   **System Settings → Privacy & Security → Local Network** (enable `polyp`), then:
   ```sh
   mv ~/anemone/launchd-pending/*.plist ~/Library/LaunchAgents/
   launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/net.calii.anemone-mount.plist
   launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/net.calii.polyp.plist
   ```
   The mount agent must be bootstrapped in the same domain as polyp, and `/Volumes/data` has to exist
   and be owned by the user first (`sudo mkdir -p /Volumes/data && sudo chown cali:staff /Volumes/data`) —
   `/Volumes` is root-owned, and unmounting deletes the mountpoint.

### Current state on trish
- `/opt/anemone/{ffmpeg,ffprobe}` — jellyfin-ffmpeg 7.1.4-3 portable (macarm64)
- `/usr/local/bin/polyp`, `/usr/local/bin/anemone-mount.sh`, `/etc/polyp.toml` (0600, cali)
- polyp started from a login shell; logs at `~/anemone/polyp.log`
- restart by hand: `ssh trish 'pkill -f "polyp --config"; nohup /usr/local/bin/polyp --config /etc/polyp.toml > ~/anemone/polyp.log 2>&1 &'`


---

# Adding a Linux agent with different hardware and a different media path (abbacchio, 2026-09-02)

The fleet is deliberately heterogeneous now, and all three differences are handled by the plugin rather
than by making the boxes match:

| | trish | abbacchio |
|---|---|---|
| OS / arch | macOS 26, arm64 | Debian 13, x86_64 (i5-1240P, Iris Xe) |
| hwaccel | videotoolbox | **vaapi** (`/dev/dri/renderD128`) |
| media | `/Volumes/data` (SMB, same path as the server) | **`/mnt/das/data` — local disk**, mapped to the server's `/Volumes/data` |
| link to the server | Thunderbolt, `10.240.0.1` | LAN, `10.10.0.2` |
| ingest URL it is handed | `http://10.240.0.1:8097` | `http://10.10.0.2:8097` |

abbacchio is the best-placed agent in the fleet: it *is* the storage host, so its ffmpeg reads the
source file off local disk and only the finished segments cross the network.

## Setup performed

```sh
# 1. jellyfin-ffmpeg (portable, matching the server's 7.1.x line)
sudo mkdir -p /opt/anemone && cd /tmp
curl -sL -o jf.tar.xz https://github.com/jellyfin/jellyfin-ffmpeg/releases/download/v7.1.4-3/jellyfin-ffmpeg_7.1.4-3_portable_linux64-gpl.tar.xz
sudo tar xf jf.tar.xz -C /opt/anemone

# 2. VAAPI userspace driver, and the render group (WITHOUT it every VAAPI init fails)
sudo apt-get install -y libva-utils intel-media-va-driver-non-free
sudo usermod -aG render "$USER"     # log out/in: an existing session keeps the old groups

# 3. verify the whole pipeline before involving Jellyfin
/opt/anemone/ffmpeg -init_hw_device vaapi=va:/dev/dri/renderD128 -filter_hw_device va \
  -f lavfi -i testsrc=duration=3:size=1920x1080:rate=25 \
  -vf 'format=nv12,hwupload,scale_vaapi=w=1280:h=720' -c:v h264_vaapi -f null -

# 4. build polyp natively (needs a C toolchain: Rust cannot link without one)
sudo apt-get install -y build-essential
rsync -a --exclude target agent/ abbacchio:~/anemone-build/ && ssh abbacchio 'cd ~/anemone-build && cargo build --release'
```

`/etc/polyp.toml`:
```toml
server_url = "ws://10.10.0.2:8097/Anemone/agents/ws"
secret = "…"                      # same as the plugin's SharedSecret
name = "abbacchio"
ffmpeg = "/opt/anemone/ffmpeg"
max_sessions = 4
hwaccel = "vaapi"                 # omit to auto-detect; it picks vaapi here anyway
hwaccel_device = "/dev/dri/renderD128"

[[mounts]]
path = "/mnt/das/data"            # where the tree is on this agent
server_path = "/Volumes/data"     # what Jellyfin calls it
```

## Measured on abbacchio

| 1080p HEVC → 720p H.264, 60 s | speed |
|---|---|
| VAAPI (Iris Xe) | **25.2× realtime** |
| libx264 veryfast (16 threads) | 14.1× realtime |

A real routed job ran at **51.8× realtime**, first segment served in **0.44 s**, segments arriving at
~18/s (≈54× realtime) with no `.part` left behind.

## Two failures worth knowing about

1. **`format=nv12` must survive translation.** Jellyfin adds it when the source is 10-bit (HEVC Main10
   decodes to p010, and H.264 encoders take 8-bit). Dropping it during translation made VAAPI reject
   exactly the 10-bit half of the library with `No usable encoding profile found`, while 8-bit files
   worked — a partial failure that is easy to misread as a broken file.
2. **The ingest URL is per agent, not per server.** With `IngestBaseUrl` left empty each agent is told
   the address it actually reached the server on. Set globally to the Thunderbolt address, abbacchio
   PUT every segment into a black hole: ffmpeg ignores HTTP status codes on PUT, so there is no error
   anywhere — the transcode runs to completion at full speed and playback simply stalls.

## Not usable as an agent
- **doppio** (i9-9900K + RTX 4070): the 4070 is bound to `vfio_pci` for the mira VM, and the UHD 630
  iGPU exposes decode-only VAAPI profiles (`vainfo` lists no `EncSlice` entrypoint, MPEG2/JPEG/VP8/VP9
  only). It would work as a software (`hwaccel = "none"`) agent; it builds and passes the polyp test
  suite there (Fedora 43).


## Running polyp as a systemd service (abbacchio, done 2026-09-02)

The unit runs as a dedicated unprivileged `polyp` user, which on this host needs two supplementary groups —
neither is optional, and both fail in ways that look like something else:

- `render` for `/dev/dri/renderD128` (`crw-rw---- root:render`). Without it every VAAPI init fails with
  "No VA display found", which reads like a driver problem rather than a permissions one.
- `users` for the media tree (`/mnt/das/data` is `drwxrwx--- cali:users`). Without it the mount probes as
  unusable and the agent simply never gets offered jobs.

`RequiresMountsFor=/mnt/das` orders the service after the media mount, so polyp does not start first and
report the mount unusable.

```sh
sudo useradd --system --no-create-home --shell /usr/sbin/nologin polyp
sudo usermod -aG users,render polyp
sudo chown root:polyp /etc/polyp.toml && sudo chmod 640 /etc/polyp.toml   # it holds the shared secret
sudo install -m 644 agent/systemd/polyp.service /etc/systemd/system/polyp.service
sudo systemctl daemon-reload && sudo systemctl enable --now polyp
```

Verified: transcodes run with `ffmpeg` owned by `polyp`, the service survives `systemctl restart` and
reconnects on its own, and it is enabled for boot.
