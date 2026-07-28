#!/usr/bin/env bash
# Uninstall Cherry Sentinel Linux Agent
set -euo pipefail

SERVICE_NAME="cherrysentinel-agent"
INSTALL_ROOT="/opt/cherrysentinel/agent"
SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"

if [[ "$(id -u)" -ne 0 ]]; then
  echo "Please run as root: sudo $0"
  exit 1
fi

systemctl stop "$SERVICE_NAME" 2>/dev/null || true
systemctl disable "$SERVICE_NAME" 2>/dev/null || true
rm -f "$SERVICE_FILE"
systemctl daemon-reload

if [[ -d "$INSTALL_ROOT" ]]; then
  rm -rf "$INSTALL_ROOT"
fi

# optional: keep logs
# rm -rf /var/log/cherrysentinel

echo "Cherry Sentinel Linux Agent removed."
