#!/bin/bash
set -euo pipefail

PURGE=0
[ "${1:-}" = "--purge" ] && PURGE=1
[ "$(id -u)" -eq 0 ] || { echo "Run as root." >&2; exit 1; }

if [ -x /etc/init.d/cherrysentinel-agent ]; then
    /etc/init.d/cherrysentinel-agent stop >/dev/null 2>&1 || true
fi
if command -v chkconfig >/dev/null 2>&1; then
    chkconfig cherrysentinel-agent off >/dev/null 2>&1 || true
    chkconfig --del cherrysentinel-agent >/dev/null 2>&1 || true
fi
rm -f /etc/init.d/cherrysentinel-agent /etc/logrotate.d/cherrysentinel-agent
rm -rf /opt/cherrysentinel/centos6-agent

if [ "$PURGE" -eq 1 ]; then
    rm -rf /etc/cherrysentinel-agent /var/lib/cherrysentinel-agent \
        /var/spool/cherrysentinel-agent /var/log/cherrysentinel
    echo "Agent and all local state purged."
else
    echo "Agent removed. Config, spool and logs were preserved. Use --purge to delete them."
fi
