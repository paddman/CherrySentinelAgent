# Cherry Sentinel — Linux Agent

Full Linux client: **CPU / RAM / disk / network / disk I/O**, multi-stack **logs** (nginx, PHP, Docker, Node.js, syslog, …), Central **heartbeat + ingest**, and **allowlisted remediation**.

See also: [docs/linux-agent.md](../../docs/linux-agent.md)

## Package

```powershell
cd C:\data_nt\CherrySentinelAgent
.\installer\build-agent-linux.ps1 -Version 1.0.12
# → artifacts\setup\CherrySentinel-Linux-Agent-1.0.12-linux-x64.tar.gz
```

## Install (on Linux)

```bash
tar -xzf CherrySentinel-Linux-Agent-*-linux-x64.tar.gz
cd CherrySentinel-Linux-Agent-*-linux-x64

# Interactive:
sudo ./install-agent.sh

# Non-interactive:
sudo ./install-agent.sh --host 192.168.56.210 --port 7443
sudo ./install-agent.sh --url https://10.0.0.5:7443
```

Installs to `/opt/cherrysentinel/agent`, systemd unit `cherrysentinel-agent`.

## Change Central URL

```bash
sudo /opt/cherrysentinel/agent/set-central-url.sh --host 10.0.0.5 --port 7443
```

## Status / logs

```bash
systemctl status cherrysentinel-agent
journalctl -u cherrysentinel-agent -f
cat /var/lib/cherrysentinel/status.json   # metrics snapshot
ls -la /var/log/cherrysentinel/
```

Heartbeat `Status` on Dashboard Endpoints includes live metrics, e.g.  
`Healthy | cpu=12.3% mem=45.1% disk=/ 62.0% net_rx=… io_r=…`

## What it monitors

- **Metrics:** CPU, memory, disk mounts, network RX/TX rate, disk I/O rate, load average  
- **Logs:** nginx, Apache, PHP-FPM, Docker JSON logs, PM2/Node, auth/syslog, DB logs (if present)  
- **Alerts:** high resource use, HTTP 5xx, PHP fatal, container OOM, SSH failures, disk full, etc.  
- **Remediation:** restart services/containers, block IPs (iptables), vacuum journal, kill PID — **allowlist only**

## Uninstall

```bash
sudo /opt/cherrysentinel/agent/uninstall-agent.sh
```

## Notes

- Do **not** use `localhost` unless Central runs on the same host.
- Self-signed Central certs accepted by default.
- Run as root for full remediation (systemctl / iptables / docker).
- Set `Linux:AutoRemediate` to `false` in `appsettings.json` to disable local auto-restart.
