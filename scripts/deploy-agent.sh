#!/usr/bin/env bash
# Build polyp and push binary + config + launchd plist to a Mac agent host over ssh.
# Usage: scripts/deploy-agent.sh <host> [server_ws_url] [ffmpeg_path]
#   e.g. scripts/deploy-agent.sh trish ws://10.240.0.1:8096/Anemone/agents/ws /opt/anemone/ffmpeg
set -euo pipefail
cd "$(dirname "$0")/.."
HOST=${1:?host}; SERVER=${2:-ws://10.240.0.1:8096/Anemone/agents/ws}; FFMPEG=${3:-/opt/anemone/ffmpeg}
export PATH="$HOME/.cargo/bin:$PATH"
(cd agent && cargo build --release)
ssh "$HOST" 'mkdir -p ~/anemone'
scp agent/target/release/polyp agent/launchd/net.calii.polyp.plist agent/install.sh agent/uninstall.sh "$HOST:~/anemone/"
ssh "$HOST" "cat > ~/anemone/polyp.toml" <<EOT
server_url = "$SERVER"
secret = "CHANGE_ME"           # must equal the plugin's SharedSecret
ffmpeg = "$FFMPEG"
max_sessions = 3
mounts = ["/Volumes/data"]
log_level = "info"
EOT
echo "pushed to $HOST:~/anemone — now: ssh $HOST 'sudo ~/anemone/install.sh' (needs the secret in ~/anemone/polyp.toml first)"
