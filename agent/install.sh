#!/bin/sh
# Install polyp as a macOS LaunchDaemon. Idempotent -- safe to re-run after rebuilding.
#
# Usage: sudo ./install.sh
set -eu

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
BIN_SRC="$SCRIPT_DIR/target/release/polyp"
BIN_DST="/usr/local/bin/polyp"
CONFIG_SRC="$SCRIPT_DIR/polyp.example.toml"
CONFIG_DST="/etc/polyp.toml"
PLIST_SRC="$SCRIPT_DIR/launchd/net.calii.polyp.plist"
PLIST_DST="/Library/LaunchDaemons/net.calii.polyp.plist"
LOG_DIR="/var/log/polyp"
LABEL="net.calii.polyp"

if [ "$(id -u)" -ne 0 ]; then
	echo "run as root (sudo ./install.sh)" >&2
	exit 1
fi

if [ ! -x "$BIN_SRC" ]; then
	echo "error: $BIN_SRC not found -- run 'cargo build --release' first" >&2
	exit 1
fi

echo "installing binary -> $BIN_DST"
install -m 755 "$BIN_SRC" "$BIN_DST"

if [ ! -f "$CONFIG_DST" ]; then
	echo "installing example config -> $CONFIG_DST (edit this before starting the daemon)"
	install -m 600 "$CONFIG_SRC" "$CONFIG_DST"
else
	echo "config already present, leaving it alone: $CONFIG_DST"
fi

echo "creating log dir -> $LOG_DIR"
mkdir -p "$LOG_DIR"

echo "installing launchd plist -> $PLIST_DST"
install -m 644 "$PLIST_SRC" "$PLIST_DST"

# Idempotent (re)load: bootout if already loaded, then bootstrap fresh.
if launchctl print "system/$LABEL" >/dev/null 2>&1; then
	echo "unloading existing daemon"
	launchctl bootout "system/$LABEL" || true
fi

echo "bootstrapping daemon"
launchctl bootstrap system "$PLIST_DST"
launchctl enable "system/$LABEL"

echo "done. check status with: sudo launchctl print system/$LABEL"
echo "logs: $LOG_DIR/polyp.log, $LOG_DIR/polyp.err.log"
if grep -q 'CHANGE-ME' "$CONFIG_DST" 2>/dev/null; then
	echo "warning: $CONFIG_DST still has the placeholder secret -- edit it, then:"
	echo "  sudo launchctl kickstart -k system/$LABEL"
fi
