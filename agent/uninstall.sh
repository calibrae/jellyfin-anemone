#!/bin/sh
# Remove the polyp LaunchDaemon. Leaves /etc/polyp.toml in place unless -c is given.
#
# Usage: sudo ./uninstall.sh [-c]
#   -c  also remove /etc/polyp.toml
set -eu

BIN_DST="/usr/local/bin/polyp"
CONFIG_DST="/etc/polyp.toml"
PLIST_DST="/Library/LaunchDaemons/net.calii.polyp.plist"
LABEL="net.calii.polyp"
REMOVE_CONFIG=0

while getopts "c" opt; do
	case "$opt" in
	c) REMOVE_CONFIG=1 ;;
	*) ;;
	esac
done

if [ "$(id -u)" -ne 0 ]; then
	echo "run as root (sudo ./uninstall.sh)" >&2
	exit 1
fi

if launchctl print "system/$LABEL" >/dev/null 2>&1; then
	echo "stopping and unloading daemon"
	launchctl bootout "system/$LABEL" || true
fi

if [ -f "$PLIST_DST" ]; then
	echo "removing plist $PLIST_DST"
	rm -f "$PLIST_DST"
fi

if [ -x "$BIN_DST" ]; then
	echo "removing binary $BIN_DST"
	rm -f "$BIN_DST"
fi

if [ "$REMOVE_CONFIG" -eq 1 ] && [ -f "$CONFIG_DST" ]; then
	echo "removing config $CONFIG_DST"
	rm -f "$CONFIG_DST"
fi

echo "done."
