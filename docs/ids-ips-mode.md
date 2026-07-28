# IDS / IPS Mode

Cherry Sentinel Agent can run as **IDS** (detect) or **IPS** (detect + prevent).

## IDS (default)

- Detects password spray, lateral movement, suspicious process/service activity
- Logs alerts + sends to Central
- **No automatic firewall block**
- Operator can still block from Dashboard (approved actions)

```json
"Agent": { "Mode": "Ids", "DetectOnly": true },
"Response": {
  "Mode": "Ids",
  "DetectOnly": true,
  "LogOnlyMode": true
}
```

## IPS

- Same detection as IDS
- On **High/Critical** alerts (configurable), automatically:
  - **Block Source IP** (inbound) if present
  - **Block Destination IP** (outbound) if present
- Creates Windows Firewall rules named `CSA-IPS-*`
- Dashboard can still open/close ports and manual blocks

```json
"Agent": { "Mode": "Ips", "DetectOnly": false },
"Response": {
  "Mode": "Ips",
  "DetectOnly": false,
  "LogOnlyMode": false,
  "AllowNetworkIsolation": true,
  "AutoBlockMinSeverity": "High",
  "AutoBlockSourceIp": true,
  "AutoBlockDestinationIp": true,
  "AutoBlockDestinationPort": false
}
```

### Enable IPS on installed agent

Edit:

```text
C:\Program Files\Cherry Sentinel Agent\appsettings.json
```

Set Mode as above, then:

```powershell
Restart-Service CherrySentinelAgent
```

Verify:

```powershell
Get-Content C:\ProgramData\CherrySentinel\Agent\status.json | Select-String Mode
Get-Content C:\ProgramData\CherrySentinel\Agent\logs\agent-*.log -Tail 30
netsh advfirewall firewall show rule name=all | findstr CSA-
```

## Safety

| Setting | Risk |
|---------|------|
| AutoBlockSourceIp on internal spray | May block legit jump hosts if mis-tuned — use allowlist |
| AutoQuarantineHost | Off by default (host-wide isolation) |
| AllowProcessTerminate | Off by default |

Detection allowlists: `config/allowlist.json` and `Detection:AllowlistSourceIps`.
