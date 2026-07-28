# Cherry Sentinel — Linux Agent Client

Lightweight Linux agent that **registers + heartbeats** to Central (`https://<ip>:7443`).  
Appears on Dashboard → **Endpoints** with `platform: linux`.

## Package

Built on Windows build host:

```powershell
cd C:\data_nt\CherrySentinelAgent
.\installer\build-agent-linux.ps1 -Version 1.0.11
# → artifacts\setup\CherrySentinel-Linux-Agent-1.0.11-linux-x64.tar.gz
```

## Install (on Linux)

```bash
# copy tarball to the Linux host, then:
tar -xzf CherrySentinel-Linux-Agent-*-linux-x64.tar.gz
cd CherrySentinel-Linux-Agent-*-linux-x64

# Interactive:
sudo ./install-agent.sh

# Or non-interactive:
sudo ./install-agent.sh --host 192.168.56.210 --port 7443
# Full URL:
sudo ./install-agent.sh --url https://10.0.0.5:7443
```

Installs to `/opt/cherrysentinel/agent` and enables systemd service `cherrysentinel-agent`.

## Change Central IP later

```bash
sudo /opt/cherrysentinel/agent/set-central-url.sh --host 10.0.0.5 --port 7443
```

## Status / logs

```bash
systemctl status cherrysentinel-agent
journalctl -u cherrysentinel-agent -f
ls -la /var/log/cherrysentinel/
```

## Uninstall

```bash
sudo /opt/cherrysentinel/agent/uninstall-agent.sh
# or from package:
sudo ./uninstall-agent.sh
```

## Notes

- Do **not** use `localhost` unless Central runs on the same Linux host.
- Self-signed Central certs are accepted by default (`AllowUntrustedServerCertificate`).
- Collectors (journald, network, processes) will expand in later releases; this package is a working **client** for fleet inventory / multi-agent.
