#!/bin/sh
# Install polyp as a systemd service on Linux. Idempotent -- safe to re-run after rebuilding.
#
# Usage: sudo ./install-linux.sh
set -eu

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
BIN_SRC="$SCRIPT_DIR/target/release/polyp"
BIN_DST="/usr/local/bin/polyp"
CONFIG_SRC="$SCRIPT_DIR/polyp.example.toml"
CONFIG_DST="/etc/polyp.toml"
UNIT_SRC="$SCRIPT_DIR/systemd/polyp.service"
UNIT_DST="/etc/systemd/system/polyp.service"
SERVICE="polyp"
SERVICE_USER="polyp"

if [ "$(id -u)" -ne 0 ]; then
	echo "run as root (sudo ./install-linux.sh)" >&2
	exit 1
fi

if [ ! -x "$BIN_SRC" ]; then
	echo "error: $BIN_SRC not found -- run 'cargo build --release' first" >&2
	exit 1
fi

if ! id "$SERVICE_USER" >/dev/null 2>&1; then
	echo "creating system user '$SERVICE_USER'"
	if command -v useradd >/dev/null 2>&1; then
		useradd --system --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER"
	elif command -v adduser >/dev/null 2>&1; then
		adduser --system --no-create-home --shell /usr/sbin/nologin --group "$SERVICE_USER"
	else
		echo "warning: no useradd/adduser found -- create the '$SERVICE_USER' user manually before starting the service" >&2
	fi
else
	echo "system user '$SERVICE_USER' already exists, leaving it alone"
fi

echo "installing binary -> $BIN_DST"
install -m 755 "$BIN_SRC" "$BIN_DST"

if [ ! -f "$CONFIG_DST" ]; then
	echo "installing example config -> $CONFIG_DST (edit this before starting the service)"
	install -m 600 "$CONFIG_SRC" "$CONFIG_DST"
	chown "$SERVICE_USER" "$CONFIG_DST" 2>/dev/null || true
else
	echo "config already present, leaving it alone: $CONFIG_DST"
fi

echo "installing systemd unit -> $UNIT_DST"
install -m 644 "$UNIT_SRC" "$UNIT_DST"

echo "reloading systemd and enabling $SERVICE"
systemctl daemon-reload
systemctl enable --now "$SERVICE"

echo "done. check status with: systemctl status $SERVICE"
echo "logs: journalctl -u $SERVICE -f"
if grep -q 'CHANGE-ME' "$CONFIG_DST" 2>/dev/null; then
	echo "warning: $CONFIG_DST still has the placeholder secret -- edit it, then:"
	echo "  sudo systemctl restart $SERVICE"
fi
