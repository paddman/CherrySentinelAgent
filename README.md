# Cherry Sentinel

 https://www.beezachat.com/api/uploads/media/images/g47jgkglhtabnqmcdpr9tyqzy.png
**Server-first endpoint security** — multi-agent fleet, Central API, native Dashboard.  
Windows Server **2012 → 2025** · Linux agent client · self-contained installers (no separate .NET).

Detects **password spray, brute force, lateral movement**, and abnormal process/service activity — and attributes **which host · process · Windows service** connected to **which IP:port**.

Default posture: **IDS / detect-only** (optional IPS auto-block).

---

## Download installers (GitHub Releases)

**Latest:** [**v1.0.11**](https://github.com/paddman/CherrySentinelAgent/releases/tag/v1.0.11) · [All releases](https://github.com/paddman/CherrySentinelAgent/releases)

| File | Platform | Description | Size |
|------|----------|-------------|------|
| [**CherrySentinel-Setup-1.0.11.exe**](https://github.com/paddman/CherrySentinelAgent/releases/download/v1.0.11/CherrySentinel-Setup-1.0.11.exe) | Windows | Full stack: Central + Agent + Dashboard | ~133 MB |
| [**CherrySentinel-Agent-Setup-1.0.14.exe**](https://github.com/paddman/CherrySentinelAgent/releases/download/v1.0.11/CherrySentinel-Agent-Setup-1.0.14.exe) | Windows | Agent only + tray (Edit Central IP/Port) | ~58 MB |
| [**CherrySentinel-Central-Setup-1.0.4.exe**](https://github.com/paddman/CherrySentinelAgent/releases/download/v1.0.11/CherrySentinel-Central-Setup-1.0.4.exe) | Windows | Central server only | ~13 MB |
| [**CherrySentinel-Linux-Agent-1.0.11-linux-x64.tar.gz**](https://github.com/paddman/CherrySentinelAgent/releases/download/v1.0.11/CherrySentinel-Linux-Agent-1.0.11-linux-x64.tar.gz) | Linux x64 | Agent client + `install-agent.sh` (systemd) | ~31 MB |

```mermaid
%%{init: {'theme':'base', 'themeVariables': { 'primaryColor':'#0F68FF','primaryTextColor':'#fff','primaryBorderColor':'#071C3D','lineColor':'#0F68FF','secondaryColor':'#E8F0FE','tertiaryColor':'#F7F9FD'}}}%%
flowchart TB
  subgraph DL["📦 Download what you need"]
    F["Full Setup.exe<br/>Central + Agent + Dashboard"]
    A["Agent Setup.exe<br/>Windows endpoints"]
    C["Central Setup.exe<br/>server only"]
    L["Linux tar.gz<br/>install-agent.sh"]
  end
  subgraph ROLE["Where it runs"]
    Srv["🖥️ Central server<br/>:7443 HTTPS"]
    Win["💻 Windows endpoints"]
    Lin["🐧 Linux endpoints"]
    Ops["👤 Operator PC<br/>Dashboard"]
  end
  F --> Srv
  F --> Win
  F --> Ops
  C --> Srv
  A --> Win
  L --> Lin
  Win -->|heartbeat + ingest| Srv
  Lin -->|heartbeat| Srv
  Ops -->|HTTPS API| Srv
```

### Quick install

| Scenario | Steps |
|----------|--------|
| **One Windows server (lab/POC)** | Full Setup as Admin → **Full stack** → Dashboard `https://localhost:7443` |
| **Extra Windows PC** | Agent Setup → Central **IP** + port **7443** (not `localhost`) |
| **Linux host** | See bash block below |
| **Silent Windows agent** | `CherrySentinel-Agent-Setup-1.0.14.exe /VERYSILENT /ServerHost=10.0.0.5 /Port=7443` |

```bash
# Linux
tar -xzf CherrySentinel-Linux-Agent-1.0.11-linux-x64.tar.gz
cd CherrySentinel-Linux-Agent-1.0.11-linux-x64
sudo ./install-agent.sh --host <CENTRAL_IP> --port 7443
```

---

## Big picture — system architecture

```mermaid
%%{init: {'theme':'base', 'themeVariables': { 'primaryColor':'#0F68FF','lineColor':'#35D880'}}}%%
flowchart TB
  subgraph FLEET["Endpoint fleet"]
    direction LR
    W1["Windows Agent<br/>Service + Tray"]
    W2["Windows Agent<br/>…"]
    LX["Linux Agent<br/>systemd"]
  end

  subgraph CENTRAL["Central Server :7443"]
    direction TB
    API["ASP.NET Core API<br/>register · heartbeat · ingest · actions"]
    CORR["Cross-host Correlator<br/>+ LateralMovementTracker"]
    SIG["Syslog UDP :5514<br/>+ OSS signatures"]
    DB[("SQLite default<br/>or PostgreSQL")]
    API --> CORR --> DB
    SIG --> DB
  end

  subgraph SOC["Operator"]
    DASH["WPF Dashboard<br/>live only · versions · firewall"]
  end

  W1 -->|"HTTPS<br/>telemetry + HB"| API
  W2 -->|"HTTPS"| API
  LX -->|"HTTPS heartbeat"| API
  DASH -->|"HTTPS<br/>read + approve actions"| API
  API -.->|"PendingActions<br/>on next heartbeat"| W1
  API -.->|"PendingActions"| W2
```

> **Important:** Agent never talks to Dashboard. Both talk to **Central**.  
> Remote agents must use Central **IP:7443**, not `localhost`.

---

## Data flow — collection → detection → central → response

```mermaid
sequenceDiagram
  autonumber
  participant OS as Windows OS
  participant Ag as Agent Service
  participant SQ as Local SQLite<br/>offline queue
  participant Ce as Central API
  participant Co as Correlator
  participant Da as Dashboard

  OS->>Ag: EventLog (4624/4625/4688/…)
  OS->>Ag: TCP/UDP table diff
  OS->>Ag: Process / Service / Tasks
  Ag->>Ag: Rule engine (IDS/IPS)
  Ag->>SQ: store + enqueue outbound
  Ag->>Ce: POST /api/v1/ingest
  Ag->>Ce: POST /api/v1/agents/heartbeat
  Ce->>Co: correlate cross-host
  Co->>Ce: incidents / campaigns
  Da->>Ce: GET agents / incidents / threats
  Da->>Ce: POST /api/v1/actions (approved)
  Note over Ce,Ag: Next heartbeat delivers PendingActions
  Ce-->>Ag: BlockIp / Isolate / StopService / …
  Ag->>OS: netsh / WMI / Kill (if allowed)
```

---

## Multi-host lateral path (what makes Cherry distinctive)

```mermaid
flowchart LR
  subgraph SRC["Source host 10.0.105.35"]
    P["PID 1684<br/>svchost"]
    Svc["IPTVManagementService"]
    P --- Svc
  end
  subgraph DST1["Dest 10.0.105.190"]
    E1["4625 × N<br/>password spray"]
  end
  subgraph DST2["Dest 10.0.105.200"]
    E2["4624 type 3<br/>network logon"]
  end
  subgraph CTR["Central"]
    INC["Incident hops"]
    CAMP["Threat Campaign<br/>A → B → C"]
    INC --> CAMP
  end

  Svc -->|"TCP :80 / :445<br/>connection batch"| DST1
  Svc --> DST2
  DST1 -->|"events batch"| INC
  DST2 --> INC
  SRC -->|"connections batch<br/>+ process attribution"| INC
```

```mermaid
sequenceDiagram
  participant S as Source Agent
  participant D as Dest Agent
  participant C as Central

  S->>S: GetExtendedTcpTable diff
  S->>S: Resolve PID → process + services
  S->>C: connections/batch
  D->>D: Security 4625 spray
  D->>C: events/batch
  C->>C: Join by IP + time window + user
  C->>C: FormatDisplay analyst view
  Note over C: Campaign: 10.0.105.35 --spray--> 10.0.105.190 --logon--> 10.0.105.200
```

---

## Agent internals (Windows)

```mermaid
flowchart TB
  subgraph COLLECT["Collectors"]
    EV["EventLogWatcher<br/>Security + System"]
    NET["IP Helper<br/>TCP/UDP snapshot diff"]
    PR["Process / WMI"]
    SV["Service resolver"]
    TK["Scheduled Tasks"]
  end

  subgraph CORE["Core loops"]
    DET["Detection<br/>rules.json"]
    RESP["Response<br/>IDS or IPS"]
    FLUSH["Flush outbound"]
    HB["Heartbeat"]
    ST["status.json"]
  end

  subgraph LOCAL["Local store"]
    DB[("SQLite WAL<br/>agent.db")]
    Q[("outbound_queue")]
  end

  subgraph UI["User on endpoint"]
    TRAY["Tray icon"]
    MINI["Mini dashboard"]
    EDIT["Edit Central IP/Port"]
    TEST["Test connection"]
  end

  EV --> DET
  NET --> DET
  PR --> DB
  SV --> DET
  TK --> DB
  DET --> RESP
  DET --> DB
  DB --> Q
  Q --> FLUSH
  FLUSH -->|HTTPS| CEN["Central"]
  HB --> CEN
  TRAY --> MINI
  TRAY --> EDIT
  TRAY --> TEST
  ST --> TRAY
```

### IDS vs IPS mode

```mermaid
flowchart TD
  A[Alert raised] --> B{Mode?}
  B -->|IDS default| C[LogOnly<br/>alert → Central / syslog]
  B -->|IPS| D{Severity ≥ AutoBlockMin?}
  D -->|Yes| E[netsh BlockSource / BlockDest]
  D -->|No| C
  C --> F[Evidence optional]
  E --> F
  G[Dashboard operator] -->|POST /actions approved| H[PendingActions queue]
  H -->|next heartbeat| I[Agent executes<br/>firewall / stop service / kill]
```

| Mode | Config | Behavior |
|------|--------|----------|
| **IDS** | `Agent:Mode=Ids` / `DetectOnly=true` | Detect + log; no auto contain |
| **IPS** | `Mode=Ips` / sample `config/appsettings.Ips.sample.json` | Auto-block High+ source/dest IP |
| **Operator** | Dashboard Firewall panel | Always via Central → agent heartbeat |

---

## Central internals

```mermaid
flowchart LR
  subgraph IN["Ingress"]
    H1["/api/v1/agents/*"]
    H2["/api/v1/ingest"]
    H3["/api/v1/actions"]
    H4["Syslog UDP :5514"]
  end
  subgraph ENG["Engines"]
    AC["ActionService<br/>durable pending_actions"]
    IG["IngestService"]
    XC["CrossHostCorrelator"]
    LT["LateralMovementTracker<br/>durable campaigns"]
    SG["OpenSourceSignatureEngine"]
  end
  subgraph STORE["Persistence"]
    SQ[(SQLite central.db)]
    PG[(PostgreSQL optional)]
  end

  H1 --> AC
  H1 --> SQ
  H2 --> IG --> XC --> LT
  IG --> SQ
  H3 --> AC --> SQ
  H4 --> SG --> SQ
  XC --> SQ
  LT --> SQ
  SQ -.-> PG
```

### Health & versions

```bash
curl -k https://localhost:7443/api/v1/health
# { "status":"ok", "product":"Cherry Sentinel Central", "version":"1.0.11", ... }

curl -k https://localhost:7443/api/v1/agents
# online, hostIp, centralUrl, agentVersion, platform, lastError
```

| Surface | Shows version |
|---------|----------------|
| Dashboard sidebar / Settings / title | Dashboard vX · Central vY |
| Agent heartbeat / Endpoints grid | `agentVersion` |
| Tray menu | `Cherry Sentinel Agent vX` |
| `CONNECTION.txt` | Central product version |
| `GET /api/v1/health` | `version` / `productVersion` |

---

## Dashboard map

```mermaid
flowchart TB
  subgraph DASH["WPF Dashboard"]
    D1["Dashboard — KPIs live"]
    D2["Incidents"]
    D3["Lateral Paths / Campaigns"]
    D4["Endpoints — fleet inventory"]
    D5["Rules catalog"]
    D6["Firewall control"]
    D7["Settings — URL + About versions"]
  end
  D1 --> API["Central HTTPS"]
  D2 --> API
  D3 --> API
  D4 --> API
  D5 --> API
  D6 --> API
  D7 --> API
```

| Page | Data source |
|------|-------------|
| Dashboard | Live incidents / agents only — **no mock data** |
| Endpoints | `/api/v1/agents` (online/offline, OS, Central URL, last error) |
| Lateral Paths | `/api/v1/threats` + hop path |
| Firewall | `POST /api/v1/actions` → agent on next HB |
| Settings | Central URL + **About / Versions** |

---

## Deploy topology examples

### A) Lab — one machine

```mermaid
flowchart LR
  M["Single Windows host"]
  M --> C["Central :7443"]
  M --> A["Agent"]
  M --> D["Dashboard"]
  A --> C
  D --> C
```

### B) Production-like — server + endpoints

```mermaid
flowchart TB
  subgraph Server["Security server"]
    Ce["Central"]
    Da["Dashboard optional"]
  end
  subgraph Endpoints["Endpoints"]
    E1["Win Agent"]
    E2["Win Agent"]
    E3["Linux Agent"]
  end
  E1 -->|"https://SERVER:7443"| Ce
  E2 --> Ce
  E3 --> Ce
  Da --> Ce
```

| Wrong | Right |
|-------|--------|
| Agent2 `Server.Url = https://localhost:7443` | `https://<Central-IP>:7443` |
| Dashboard different URL than Agent | **Same** Central base URL |
| Central stopped during install | Start Central first; open firewall TCP 7443 |

---

## Threat coverage (chart + table)

```mermaid
mindmap
  root((Cherry detection))
    Credential Access
      Internal password spray
      Distributed spray
      Brute force
      Spray then success
      Suspicious account names
    Lateral Movement
      Multiple internal targets
      Auth port fan-out
      Explicit credentials 4648
      Network logon burst
      RDP logon burst
    Privilege
      Priv logon after failures
      Group change 4728/4732
    Persistence
      New service 4697/7045
      Scheduled task 4698
      Account created 4720
    Execution heuristics
      Process burst 4688
      LOLBins / temp paths
      Suspicious cmdline
    Network noise
      WFP 5156/5157 bursts
    Syslog signatures
      SSH/RDP brute keywords
      Webshell / mimikatz tokens
```

| Category | Example rule IDs | Primary signals |
|----------|------------------|-----------------|
| Credential Access | `INTERNAL_PASSWORD_SPRAY`, `BRUTE_FORCE_SINGLE_ACCOUNT` | 4625 volume / patterns |
| Lateral | `MULTIPLE_INTERNAL_TARGETS`, `RDP_LOGON_BURST` | TCP fan-out + logon types |
| Privilege | `PRIVILEGED_LOGON_AFTER_FAILURES` | 4625→4624 + 4672 |
| Persistence | `NEW_SERVICE_INSTALLED`, `SCHEDULED_TASK_CREATED` | 4697 / 7045 / 4698 |
| Full list | [`config/rules.json`](config/rules.json) · [`docs/threat-coverage.md`](docs/threat-coverage.md) | |

```mermaid
pie showData
  title Detection signal mix focus
  "Credential / logon" : 35
  "Lateral / network" : 30
  "Persistence / privilege" : 20
  "Process heuristics" : 10
  "Syslog signatures" : 5
```

---

## API surface (Central)

```mermaid
flowchart LR
  subgraph Agents["Agent-facing"]
    R["POST /api/v1/agents/register"]
    H["POST /api/v1/agents/heartbeat"]
    I["POST /api/v1/ingest"]
    E["POST /api/v1/events/batch"]
    N["POST /api/v1/connections/batch"]
  end
  subgraph Ops["Operator-facing"]
    GA["GET /api/v1/agents"]
    GI["GET /api/v1/incidents"]
    GT["GET /api/v1/threats…"]
    PA["POST /api/v1/actions"]
    HE["GET /api/v1/health"]
    SG["GET /api/v1/signatures"]
  end
```

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/api/v1/health` | Health + **version** + syslog info |
| POST | `/api/v1/agents/register` | Enroll agent |
| POST | `/api/v1/agents/heartbeat` | Presence + deliver pending actions |
| GET | `/api/v1/agents` | Fleet inventory |
| POST | `/api/v1/ingest` | Telemetry batch |
| GET/POST | `/api/v1/incidents` | Incidents |
| POST/GET | `/api/v1/actions` | Operator response queue |
| GET | `/api/v1/threats` · `/threats/{id}/path` | Campaigns / hops |
| GET | `/api/v1/signatures` | Loaded OSS signatures |
| UDP | `:5514` | Syslog → signature engine |

---

## Solution map (code)

```mermaid
flowchart TB
  subgraph SRC["src/"]
    AG["Agent Windows"]
    TR["Agent.Tray"]
    LX["Agent.Linux"]
    SV["Server Central"]
    DA["Dashboard WPF"]
    DE["Detection"]
    RE["Response"]
    CO["Collectors.Windows"]
    ST["Storage SQLite"]
    TRN["Transport HTTPS + Syslog"]
    SH["Shared contracts"]
  end
  AG --> DE
  AG --> RE
  AG --> CO
  AG --> ST
  AG --> TRN
  TR --> AG
  LX --> SH
  LX --> TRN
  SV --> SH
  DA --> SH
  DE --> SH
  RE --> SH
```

```
CherrySentinel.sln
├── src/
│   ├── CherrySentinel.Agent          # Windows service
│   ├── CherrySentinel.Agent.Tray     # tray + mini UI + IP editor
│   ├── CherrySentinel.Agent.Linux    # Linux client
│   ├── CherrySentinel.Server         # Central API
│   ├── CherrySentinel.Dashboard      # WPF SOC console
│   ├── CherrySentinel.Detection
│   ├── CherrySentinel.Response
│   ├── CherrySentinel.Collectors.Windows
│   ├── CherrySentinel.Storage
│   ├── CherrySentinel.Transport
│   └── CherrySentinel.Shared
├── installer/   # Inno Setup + linux/*.sh
├── config/      # rules.json · signatures · samples
├── docs/
└── tests/
```

---

## Stack

| Layer | Technology |
|-------|------------|
| Language | C# / **.NET 10** |
| Windows Agent | `net10.0-windows`, **win-x64 self-contained** service |
| Linux Agent | `net10.0`, **linux-x64 self-contained**, systemd |
| Local DB | SQLite WAL + offline queue |
| Central DB | **SQLite default** · PostgreSQL optional |
| UI | WPF Dashboard + WinForms tray |
| Transport | HTTPS · optional mTLS · optional syslog UDP |
| Logging | Serilog rolling files |
| Installers | Inno Setup (Windows) · bash + tar.gz (Linux) |

---

## Build from source

```powershell
cd C:\data_nt\CherrySentinelAgent   # or your clone path
dotnet restore
dotnet build CherrySentinel.sln -c Release
dotnet test CherrySentinel.sln -c Release

# Windows packages
.\installer\build-setup.ps1 -Version 1.0.11          # Full
.\installer\build-setup-agent.ps1 -Version 1.0.14    # Agent
.\installer\build-setup-central.ps1                  # Central

# Linux package
.\installer\build-agent-linux.ps1 -Version 1.0.11
# → artifacts\setup\CherrySentinel-Linux-Agent-*-linux-x64.tar.gz
```

### Publish agent only

```powershell
dotnet publish src/CherrySentinel.Agent/CherrySentinel.Agent.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o artifacts/agent-win-x64
```

---

## Install paths (Windows)

| Item | Path |
|------|------|
| Agent service | `CherrySentinelAgent` |
| Central service | `CherrySentinelCentral` |
| Agent install | `C:\Program Files\Cherry Sentinel Agent` or `...\Cherry Sentinel\Agent` |
| Data | `C:\ProgramData\CherrySentinel\` |
| Agent logs | `...\Agent\logs` · `status.json` |
| Central logs | `...\Server\logs` · `central.db` |

**Tray (Windows):** Edit Central IP/Port · Test connection · Mini dashboard · shows **version**.

**Linux:** `/opt/cherrysentinel/agent` · `systemctl status cherrysentinel-agent`

---

## Roadmap (high level)

```mermaid
timeline
  title Cherry Sentinel product roadmap
  section Phase 0
    Durable actions + fleet inventory + versions + Linux client : done
  section Phase 1
    Enrollment token + API auth + policy pull : next
  section Phase 2
    Timeline + process tree + isolate + evidence : next
  section Phase 3
    Light EPP hash/quarantine : planned
  section Phase 4
    Full Linux collectors journald/ss/proc : planned
  section Later
    Scale · reports · XDR connectors : planned
```

Details: [`docs/roadmap-trellix-class.md`](docs/roadmap-trellix-class.md)

---

## Documentation

| Doc | Topic |
|-----|--------|
| [Architecture](docs/architecture.md) | Sequence + modules |
| [Installation](docs/installation.md) | Deploy guide |
| [Detection rules](docs/detection-rules.md) | Rule engine |
| [Threat coverage](docs/threat-coverage.md) | What we detect / track |
| [IDS / IPS mode](docs/ids-ips-mode.md) | Mode switch |
| [Syslog + signatures](docs/syslog-and-signatures.md) | UDP 5514 |
| [Incident response](docs/incident-response.md) | IR workflow |
| [Threat model](docs/threat-model.md) | Security assumptions |
| [Known limitations](docs/known-limitations.md) | Honest limits |
| [Linux agent](installer/linux/README.md) | Linux install |
| [Windows Server 2012](docs/windows-server-2012.md) | Legacy OS notes |

---

## What Cherry does **not** do

```mermaid
flowchart LR
  X1["✗ Attack / scan other hosts from agent"]
  X2["✗ Clear Security Event Log"]
  X3["✗ Auto kill/block in IDS mode"]
  X4["✗ Agent ↔ Dashboard direct link"]
  X5["✗ Full cloud NGAV / email XDR yet"]
```

---

## License

Proprietary — CherryDeskX / internal use.

**Repo:** https://github.com/paddman/CherrySentinelAgent  
**Releases:** https://github.com/paddman/CherrySentinelAgent/releases
