# Deploy & test plan (first live run)

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
2. Dashboard → Plugins → Anemone: set **DryRun = on**, generate a SharedSecret, IngestBaseUrl = `http://10.240.0.1:8096`. Save.
3. Play something that transcodes. `grep anemone ~/Library/Application\ Support/jellyfin/log/log_*.log` must show
   `anemone: Anemone plugin loaded` and, per transcode, `dry-run — would route …` or the reason it stays local.
   Playback must be byte-for-byte normal (the fork is running, remote path is not).
4. Rollback at any time: `rm -rf ~/Library/Application\ Support/jellyfin/plugins/Anemone_*` + restart.

## 3. Agent on trish
```
scripts/deploy-agent.sh trish ws://10.240.0.1:8096/Anemone/agents/ws /opt/anemone/ffmpeg
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
Throttling off for remote jobs; subtitle burn-in, external subs, progressive, live TV, probing/trickplay stay local;
macOS→macOS only; HTTP input fallback not wired (needs the same-path mount).
