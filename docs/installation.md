# Installation

## Prerequisites

### Build machine

- .NET 10 SDK
- Windows x64

### Endpoint (agent)

- Windows Server **2012 / 2012 R2 / 2016 / 2019 / 2022 / 2025** x64
- Local Administrator for install
- **Microsoft Visual C++ Redistributable x64** (2015–2022 recommended)
- No Docker, Python, or Node.js required
- Self-contained publish embeds the .NET runtime

### Central server

- Windows or Linux hosting ASP.NET Core
- PostgreSQL 14+
- TLS certificate for HTTPS

## Publish Agent (self-contained win-x64)

```powershell
cd C:\data_nt\CherrySentinelAgent
dotnet publish src\CherrySentinel.Agent\CherrySentinel.Agent.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o .\publish\agent
```

Copy `rules\default-rules.json` into the publish folder if not already present:

```powershell
Copy-Item .\rules\default-rules.json .\publish\agent\ -Force
```

## Install Agent

```powershell
# Elevated PowerShell
.\installer\install-agent.ps1 `
  -SourceDir .\publish\agent `
  -CentralUrl https://sentinel.example.local:7443
```

Service name: `CherrySentinelAgent`  
Data: `C:\ProgramData\CherrySentinel\Agent`  
Logs: `C:\ProgramData\CherrySentinel\Agent\logs`

## Upgrade / Uninstall

```powershell
.\installer\upgrade-agent.ps1 -SourceDir .\publish\agent
.\installer\uninstall-agent.ps1
# optional wipe of SQLite + logs:
.\installer\uninstall-agent.ps1 -RemoveData
```

## Publish / run Central Server

```powershell
dotnet publish src\CherrySentinel.Server\CherrySentinel.Server.csproj -c Release -o .\publish\server
```

Configure PostgreSQL connection in `appsettings.json`:

```json
"Postgres": {
  "ConnectionString": "Host=db;Port=5432;Database=cherrysentinel;Username=cherrysentinel;Password=***"
}
```

```powershell
.\publish\server\CherrySentinel.Server.exe
```

API (default port 7443):

- `GET  /api/v1/health`
- `POST /api/v1/heartbeat`
- `POST /api/v1/ingest`
- `GET  /api/v1/incidents`
- `GET  /api/v1/agents`

## Audit policy (endpoints)

For rich detection, enable advanced audit on monitored servers:

- Logon/Logoff success & failure
- Account Management
- Process Creation (4688) — optionally with command line auditing
- Object Access / Filtering Platform Connection (5156/5157) if using Windows Filtering Platform auditing

## mTLS (optional)

1. Issue client certs per agent; place PFX on endpoint.
2. Set agent `CentralServer:EnableMtls=true` and certificate path.
3. Set server `Security:EnableMtls=true` and configure Kestrel client certificate mode.
