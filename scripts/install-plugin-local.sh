#!/usr/bin/env bash
# Install the packaged plugin into the local Jellyfin (macOS app layout) and restart the server.
# Usage: scripts/install-plugin-local.sh [--no-restart]
set -euo pipefail
cd "$(dirname "$0")/.."
PLUGINS="$HOME/Library/Application Support/jellyfin/plugins"
VERSION=$(sed -n 's/.*<AssemblyVersion>\(.*\)<\/AssemblyVersion>.*/\1/p' plugin/Jellyfin.Plugin.Cluster/Jellyfin.Plugin.Cluster.csproj)
SRC="dist/Cluster_${VERSION}"
[ -d "$SRC" ] || scripts/package-plugin.sh
[ -d "$PLUGINS" ] || { echo "no Jellyfin plugins dir at $PLUGINS"; exit 1; }
rm -rf "$PLUGINS"/Cluster_*
cp -R "$SRC" "$PLUGINS/"
echo "installed → $PLUGINS/Cluster_${VERSION}"
if [ "${1:-}" != "--no-restart" ]; then
  echo "restarting Jellyfin…"; curl -s -X POST http://127.0.0.1:8096/System/Restart -o /dev/null || true
  for i in $(seq 1 30); do sleep 2; curl -sf http://127.0.0.1:8096/System/Ping >/dev/null 2>&1 && { echo "Jellyfin is back"; break; }; done
fi
echo "check: grep -i 'jfc' \"\$HOME/Library/Application Support/jellyfin/log/log_$(date +%Y%m%d).log\" | tail"
