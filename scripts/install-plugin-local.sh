#!/usr/bin/env bash
# Install the packaged plugin into the local Jellyfin (macOS app layout) and restart the server.
# Usage: scripts/install-plugin-local.sh [--no-restart]
set -euo pipefail
cd "$(dirname "$0")/.."
PLUGINS="$HOME/Library/Application Support/jellyfin/plugins"
VERSION=$(sed -n 's/.*<AssemblyVersion>\(.*\)<\/AssemblyVersion>.*/\1/p' plugin/Jellyfin.Plugin.Anemone/Jellyfin.Plugin.Anemone.csproj)
SRC="dist/Anemone_${VERSION}"
[ -d "$SRC" ] || scripts/package-plugin.sh
[ -d "$PLUGINS" ] || { echo "no Jellyfin plugins dir at $PLUGINS"; exit 1; }
rm -rf "$PLUGINS"/Anemone_*
cp -R "$SRC" "$PLUGINS/"
echo "installed → $PLUGINS/Anemone_${VERSION}"
if [ "${1:-}" != "--no-restart" ]; then
  # NOTE: POST /System/Restart is an *in-process* soft restart on macOS. The CLR cannot unload an
  # assembly, so Jellyfin keeps serving the plugin build it loaded first and your new DLL is ignored
  # (it looks like your changes silently did nothing). The app must actually be quit and relaunched.
  echo "restarting Jellyfin (full app relaunch)…"
  osascript -e 'quit app "Jellyfin"' >/dev/null 2>&1 || pkill -f 'Jellyfin.app/Contents/MacOS/Jellyfin Server' || true
  # wait for the wrapper app itself to be gone, otherwise `open` just re-activates a quitting app
  for i in $(seq 1 25); do sleep 1; pgrep -f 'Jellyfin.app/Contents/MacOS/Jellyfin Server' >/dev/null || break; done
  sleep 1
  open -a Jellyfin
  for i in $(seq 1 45); do sleep 2; curl -sf http://127.0.0.1:8096/System/Ping >/dev/null 2>&1 && { echo "Jellyfin is back"; break; }; done
fi
echo "check: grep -i 'anemone' \"\$HOME/Library/Application Support/jellyfin/log/log_$(date +%Y%m%d).log\" | tail"
