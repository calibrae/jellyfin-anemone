#!/usr/bin/env bash
# Build jfc-agent and push binary + config + launchd plist to a Mac agent host over ssh.
# Usage: scripts/deploy-agent.sh <host> [server_ws_url] [ffmpeg_path]
#   e.g. scripts/deploy-agent.sh trish ws://10.240.0.1:8096/Cluster/agents/ws /opt/jfc/ffmpeg
set -euo pipefail
cd "$(dirname "$0")/.."
HOST=${1:?host}; SERVER=${2:-ws://10.240.0.1:8096/Cluster/agents/ws}; FFMPEG=${3:-/opt/jfc/ffmpeg}
export PATH="$HOME/.cargo/bin:$PATH"
(cd agent && cargo build --release)
ssh "$HOST" 'mkdir -p ~/jfc'
scp agent/target/release/jfc-agent agent/launchd/net.calii.jfc-agent.plist agent/install.sh agent/uninstall.sh "$HOST:~/jfc/"
ssh "$HOST" "cat > ~/jfc/jfc-agent.toml" <<EOT
server_url = "$SERVER"
secret = "CHANGE_ME"           # must equal the plugin's SharedSecret
ffmpeg = "$FFMPEG"
max_sessions = 3
mounts = ["/Volumes/data"]
log_level = "info"
EOT
echo "pushed to $HOST:~/jfc — now: ssh $HOST 'sudo ~/jfc/install.sh' (needs the secret in ~/jfc/jfc-agent.toml first)"
