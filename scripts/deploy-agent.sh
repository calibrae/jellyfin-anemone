#!/usr/bin/env bash
# Build polyp and push binary + config + install script to an agent host over ssh. Detects the
# remote OS (macOS -> LaunchDaemon, Linux -> systemd) via `ssh <host> uname -sm` and picks the
# matching packaging.
# Usage: scripts/deploy-agent.sh <host> [server_ws_url] [ffmpeg_path]
#   e.g. scripts/deploy-agent.sh trish  ws://10.240.0.1:8097/Anemone/agents/ws /opt/anemone/ffmpeg
#   e.g. scripts/deploy-agent.sh doppio ws://10.240.0.1:8097/Anemone/agents/ws /opt/anemone/ffmpeg
set -euo pipefail
cd "$(dirname "$0")/.."
HOST=${1:?host}; SERVER=${2:-ws://10.240.0.1:8097/Anemone/agents/ws}; FFMPEG=${3:-/opt/anemone/ffmpeg}
export PATH="$HOME/.cargo/bin:$PATH"

KERNEL=$(ssh "$HOST" uname -s)
MACHINE=$(ssh "$HOST" uname -m)

# jellyfin-ffmpeg portable build matching this host, for the operator's reference -- grab it
# from https://github.com/jellyfin/jellyfin-ffmpeg/releases (not downloaded here).
case "$KERNEL-$MACHINE" in
  Darwin-arm64)   FFMPEG_BUILD="jellyfin-ffmpeg_7.1.4-3_portable_macarm64-gpl.tar.xz" ;;
  Linux-x86_64)   FFMPEG_BUILD="jellyfin-ffmpeg_7.1.4-3_portable_linux64-gpl.tar.xz" ;;
  Linux-aarch64)  FFMPEG_BUILD="jellyfin-ffmpeg_7.1.4-3_portable_linuxarm64-gpl.tar.xz" ;;
  *)              FFMPEG_BUILD="(no known jellyfin-ffmpeg portable build for $KERNEL/$MACHINE)" ;;
esac

case "$KERNEL" in
  Darwin)
    (cd agent && cargo build --release)
    ssh "$HOST" 'mkdir -p ~/anemone'
    scp agent/target/release/polyp agent/launchd/net.calii.polyp.plist agent/install.sh agent/uninstall.sh "$HOST:~/anemone/"
    INSTALL_HINT="sudo ~/anemone/install.sh"
    ;;
  Linux)
    # No cross-compile toolchain wired up here -- build on the target host itself instead,
    # same as this repo's own doppio verification workflow.
    echo "Linux target: building polyp on $HOST itself (cargo must already be installed there)."
    ssh "$HOST" 'mkdir -p ~/anemone-build ~/anemone'
    rsync -a --exclude target agent/ "$HOST:~/anemone-build/agent/"
    ssh "$HOST" 'export PATH="$HOME/.cargo/bin:$PATH"; cd ~/anemone-build/agent && cargo build --release'
    ssh "$HOST" 'cp ~/anemone-build/agent/target/release/polyp ~/anemone-build/agent/systemd/polyp.service ~/anemone-build/agent/install-linux.sh ~/anemone/'
    INSTALL_HINT="sudo ~/anemone/install-linux.sh"
    ;;
  *)
    echo "unsupported remote kernel: $KERNEL" >&2
    exit 1
    ;;
esac

ssh "$HOST" "cat > ~/anemone/polyp.toml" <<EOT
server_url = "$SERVER"
secret = "CHANGE_ME"           # must equal the plugin's SharedSecret
ffmpeg = "$FFMPEG"
max_sessions = 3
mounts = ["/Volumes/data"]
log_level = "info"
EOT

echo "pushed to $HOST:~/anemone ($KERNEL/$MACHINE; jellyfin-ffmpeg build: $FFMPEG_BUILD)"
echo "now: ssh $HOST '$INSTALL_HINT' (needs the secret in ~/anemone/polyp.toml first)"
