# Roadmap: Trellix-class Server EDR (Windows + Linux)

See session plan for full strategy. Summary:

## Positioning
**Cherry Sentinel** = Server-first EDR + Central SOC (on-prem / hybrid / air-gap first), Windows + Linux.

## Phase 0 (now)
- Durable pending actions + threat campaigns
- Fleet inventory (online/offline, central URL, last error)
- Tray: Test Central connection
- Linux agent skeleton (heartbeat only)

## Phase 1
- Enrollment token + API keys
- Policy pull on heartbeat
- Audit log

## Phase 2
- Host timeline, process tree, hunt
- True isolate + evidence download

## Phase 3
- Light EPP: hash IOC + quarantine

## Phase 4
- Full Linux collectors (journald, ss, /proc)

## Phase 5–6
- Scale, reports, XDR connectors
