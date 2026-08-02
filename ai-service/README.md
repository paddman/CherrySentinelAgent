# Cherry Sentinel Brain

**Evidence-grounded AI SOC analyst for Cherry Sentinel, Suricata, Zeek and Attack Surface telemetry.**

Sentinel Brain turns many low-level alerts into one explainable incident decision. It combines deterministic risk rules, per-asset anomaly detection, local Qwen analysis, playbook retrieval, a threat graph, guarded response recommendations, multi-tenant API keys, feedback and usage metering.

## What is implemented

- **Local Qwen through an OpenAI-compatible API** such as vLLM, SGLang or an internal model gateway.
- **Multi-agent analysis mode** with Network, Endpoint and Exposure specialists plus an Incident Commander.
- **Hybrid risk engine** that anchors the model to deterministic evidence and limits unsupported score changes.
- **ML behavioral baseline** per tenant and asset using Isolation Forest plus robust median absolute deviation.
- **Evidence guardrails**: the model may cite only supplied `ref_id` values. Unknown citations are discarded.
- **Response guardrails**: only allowlisted actions are returned. Blocking, isolation, account, process and service actions always require human approval.
- **Threat graph** linking hosts, IPs, users, processes, services, domains, IDS alerts, ASM findings, CVEs and deception hits.
- **Silent Hunter deception tokens** for canary credentials, honey files, decoy API keys, URLs and shares; a hit becomes a critical AI-analyzed incident.
- **Local playbook RAG** using TF-IDF over defensive playbooks, with no external data dependency.
- **Monthly AI report**, **read-only threat-hunt planner**, analyst feedback and per-tenant usage metering.
- **Cherry Central bridge** that can fetch an existing incident by ID without changing the .NET server first.
- **Deterministic fallback** so the demo still works when the LLM endpoint is unavailable.

## Start in Docker

From the repository root:

```bash
cp ai-service/.env.example .env
# Edit .env: tenant key, Cherry Central URL/API key and Qwen endpoint.
docker compose -f docker-compose.ai.yml up -d --build
curl http://127.0.0.1:8088/health
```

Default service port: `8088`.

## Start for development

```bash
cd ai-service
python -m venv .venv
# Linux/macOS
source .venv/bin/activate
# Windows PowerShell: .venv\Scripts\Activate.ps1

pip install -e ".[dev]"
uvicorn app.main:app --host 0.0.0.0 --port 8088 --reload
pytest
```

Open API documentation at `http://127.0.0.1:8088/docs`.

## Tenant authentication

All `/v1/*` endpoints require:

```text
X-Cherry-Tenant: demo
X-Cherry-Api-Key: change-me
```

Configure tenants with `CHERRY_TENANTS_JSON`:

```json
[
  {
    "tenant_id": "hospital-a",
    "name": "Hospital A",
    "api_key_sha256": "<sha256 of tenant API key>",
    "central_url": "https://10.0.0.5:7443",
    "central_api_key": "<Cherry Central operator key>",
    "central_verify_tls": false
  }
]
```

Use `api_key_sha256` rather than plaintext `api_key` in production. The Central URL comes only from server-side tenant configuration, preventing callers from turning the bridge into an arbitrary URL fetcher.

## Analyze a combined incident

```bash
curl -X POST http://127.0.0.1:8088/v1/incidents/analyze \
  -H 'Content-Type: application/json' \
  -H 'X-Cherry-Tenant: demo' \
  -H 'X-Cherry-Api-Key: change-me' \
  --data @../examples/ai-incident-password-spray.json
```

The request may combine:

- Cherry Sentinel endpoint incident and Windows event evidence
- Suricata alerts
- Zeek connection, DNS, HTTP, TLS, notice and weird records
- ASM results from subfinder, amass, naabu, nmap, httpx and nuclei
- threat-intelligence verdicts
- deception hits
- numerical behavioral features

The response includes Thai and English summaries, risk and confidence, MITRE mapping, exact evidence citations, specialist findings, a threat graph and guarded actions.

## Analyze an incident already stored in Cherry Central

```bash
curl -X POST http://127.0.0.1:8088/v1/central/incidents/INCIDENT_ID/analyze \
  -H 'X-Cherry-Tenant: demo' \
  -H 'X-Cherry-Api-Key: change-me'
```

The tenant must have `central_url` and `central_api_key` configured.

## Train and score an asset baseline

Send normal observations with `learn: true`:

```bash
curl -X POST http://127.0.0.1:8088/v1/anomaly/observe \
  -H 'Content-Type: application/json' \
  -H 'X-Cherry-Tenant: demo' \
  -H 'X-Cherry-Api-Key: change-me' \
  -d '{
    "asset_id": "web-01",
    "learn": true,
    "features": {
      "connections_per_minute": 95,
      "unique_destination_ports": 4,
      "failed_login_rate": 0,
      "dns_query_entropy": 2.1
    }
  }'
```

After the minimum sample count, the response switches from `learning` to `ready`. Very strong outliers are not written back into the stable baseline, reducing baseline poisoning.


## Silent Hunter deception token

Create a token from an authenticated SOC session:

```bash
curl -X POST http://127.0.0.1:8088/v1/deception/tokens \
  -H 'Content-Type: application/json' \
  -H 'X-Cherry-Tenant: demo' \
  -H 'X-Cherry-Api-Key: change-me' \
  -d '{"name":"Finance backup credential","tokenType":"canary_credential","asset":"backup-vault","ttlDays":365}'
```

The raw token is returned exactly once. Put it only in an authorized decoy location. A sensor reports access with:

```bash
curl -X POST http://127.0.0.1:8088/v1/deception/hits \
  -H 'Content-Type: application/json' \
  -d '{"token":"<raw token>","sourceIp":"10.9.0.7","sourceHost":"srv-x","processName":"powershell.exe","destination":"backup-vault"}'
```

The hit endpoint deliberately does not reveal whether a token was valid. Valid hits are deduplicated, stored, converted into a critical incident and analyzed through the same evidence and approval guardrails.

## Qwen configuration

Example values:

```env
CHERRY_LLM_BASE_URL=http://10.0.0.20:8000/v1
CHERRY_LLM_API_KEY=local
CHERRY_LLM_MODEL=qwen3.5-9b
CHERRY_ANALYSIS_MODE=multi_agent
CHERRY_LLM_ENABLE_THINKING=false
```

`multi_agent` runs three specialists in parallel and then one commander synthesis. Use `single` when low latency matters more than the extra analysis pass.

## Main API

| Method | Path | Purpose |
|---|---|---|
| GET | `/health` | Service and local model reachability |
| GET | `/v1/status` | Tenant, model and guardrail state |
| POST | `/v1/incidents/analyze` | Analyze a combined XDR incident |
| POST | `/v1/central/incidents/{id}/analyze` | Pull and analyze a Cherry Central incident |
| GET | `/v1/analyses/{incident_id}` | Retrieve the latest stored decision |
| POST | `/v1/anomaly/observe` | Learn/score per-asset behavior |
| POST | `/v1/feedback` | Store analyst verdict for later calibration |
| POST/GET | `/v1/deception/tokens` | Create or list defensive canary tokens |
| POST | `/v1/deception/hits` | Sensor callback; token acts as bearer secret |
| POST | `/v1/hunt/plan` | Create a read-only threat-hunt plan |
| POST | `/v1/reports/monthly` | Generate executive and technical summaries |
| GET | `/v1/playbooks/search` | Search local defensive playbooks |
| GET | `/v1/usage/{yyyy-mm}` | Tenant AI usage and token metering |

## Safety model

Sentinel Brain is not allowed to run arbitrary shell commands or execute response actions. It returns declarative recommendations only. The existing Cherry Central action queue and operator approval remain the enforcement boundary. Even when a model tries to mark a destructive action as automatic, the service rewrites it to `requires_human_approval: true`. Humanity retains the final button, which is both comforting and historically questionable.
