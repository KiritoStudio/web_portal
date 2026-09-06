#!/usr/bin/env bash
# Installs or updates MyPortal on the target machine.
# Usage: copy the published directory and this script over, then sudo ./install.sh <publish dir>
set -euo pipefail

SRC="${1:-}"
APP_DIR=/opt/home-portal
UNIT=/etc/systemd/system/home-portal.service

if [[ -z "$SRC" || ! -x "$SRC/HomePortal" ]]; then
  echo "Usage: sudo $0 <publish dir>   (the directory should contain the HomePortal binary)" >&2
  exit 1
fi
if [[ $EUID -ne 0 ]]; then
  echo "Needs root: sudo $0 $SRC" >&2
  exit 1
fi

# Stop first, so an update is not overwriting a binary that is currently running
systemctl stop home-portal.service 2>/dev/null || true

# NOTE: wipe before copying so nothing from the previous version lingers; the data lives
#       in PostgreSQL and is untouched
rm -rf "$APP_DIR"
install -d "$APP_DIR"
cp -a "$SRC"/. "$APP_DIR"/
chmod +x "$APP_DIR/HomePortal"

# The connection string is required: without it the service cannot start, so fail here
# rather than after installation
ENV_SRC="$(dirname "$0")/home-portal.env"
if [[ -f "$ENV_SRC" ]]; then
  install -m 600 -o root -g root "$ENV_SRC" /etc/home-portal.env
elif [[ ! -f /etc/home-portal.env ]]; then
  echo "No database connection string: ship home-portal.env along, or create /etc/home-portal.env on the target first" >&2
  exit 1
fi

install -m 644 "$(dirname "$0")/home-portal.service" "$UNIT"
systemctl daemon-reload
systemctl enable --now home-portal.service

echo
systemctl --no-pager --lines=0 status home-portal.service
echo
echo "Installed: http://$(hostname -I 2>/dev/null | awk '{print $1}')/"
