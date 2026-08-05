# NT Shield on Cherry Sentinel

NT Shield is the AIaaS product profile built on the existing Cherry Sentinel runtime. It is not a second endpoint agent, a forked detection engine, or a new pile of services created merely so the diagram looks expensive.

## Product boundary

| Layer | Responsibility |
|---|---|
| Cherry Sentinel Agent | Collect Windows/Linux telemetry, run deterministic detection, keep an offline queue, receive approved response jobs |
| Cherry Sentinel Central | Register agents, ingest evidence, correlate hosts, store incidents, audit operators, queue approved actions |
| Sentinel Brain | Deterministic risk, behavioral ML, local Qwen analysis, evidence validation, RAG, threat graph, bounded read-only investigation and usage metering |
| NT Shield profile | Product identity, branded API namespace, service catalog, tenant header aliases, deployment defaults and pilot/production gates |

The runtime remains backward compatible. Existing `/v1/*` routes and `X-Cherry-*` headers continue to work. NT Shield adds:

- API prefix: `/nt-shield/v1`
- Header aliases: `X-NT-Shield-Tenant` and `X-NT-Shield-Api-Key`
- Product status, package catalog and readiness endpoints
- Branded incident-analysis route over the existing evidence-grounded pipeline
- Docker Compose deployment override

## Start the NT Shield profile

```bash
cp ai-service/.env.nt-shield.example .env
# Edit .env before sharing the service. The sample key is deliberately useless.

docker compose \
  -f docker-compose.ai.yml \
  -f docker-compose.nt-shield.yml \
  up -d --build
```

Check the container and model endpoint:

```bash
docker compose \
  -f docker-compose.ai.yml \
  -f docker-compose.nt-shield.yml \
  ps

curl http://127.0.0.1:8088/health
```

## NT Shield API examples

```bash
curl http://127.0.0.1:8088/nt-shield/v1/status \
  -H 'X-NT-Shield-Tenant: nt-demo' \
  -H 'X-NT-Shield-Api-Key: change-me'

curl http://127.0.0.1:8088/nt-shield/v1/catalog \
  -H 'X-NT-Shield-Tenant: nt-demo' \
  -H 'X-NT-Shield-Api-Key: change-me'

curl http://127.0.0.1:8088/nt-shield/v1/readiness \
  -H 'X-NT-Shield-Tenant: nt-demo' \
  -H 'X-NT-Shield-Api-Key: change-me'

curl -X POST http://127.0.0.1:8088/nt-shield/v1/incidents/analyze \
  -H 'Content-Type: application/json' \
  -H 'X-NT-Shield-Tenant: nt-demo' \
  -H 'X-NT-Shield-Api-Key: change-me' \
  --data @examples/ai-incident-password-spray.json
```

When both Cherry and NT Shield header families are supplied, their tenant IDs and API keys must match. Conflicting values are rejected instead of letting a proxy produce an identity crisis in the audit trail.

## Current package state

| Package | Repository state | Before commercial claim |
|---|---|---|
| NT Shield API | Prototype | Distributed quota, billing reconciliation, API/SLA load test |
| NT Shield Monitor | Prototype | Retention, capacity baseline, customer pilot |
| NT Shield Web | Partial | Managed WAF connector, controlled replay, false-block gate |
| NT Shield XDR | Prototype | PostgreSQL RLS, OIDC/MFA/RBAC, failover and noisy-neighbor tests |
| NT Shield MDR | Pilot required | 24x7 staffing model, escalation runbook, analyst workload, commercial SLA |

These labels are returned by `/nt-shield/v1/catalog`. They are intentionally less exciting than fictional production readiness.

## Safety boundary

NT Shield AI may analyze, correlate, explain and recommend. It does not gain a shell, arbitrary tools, or permission to execute destructive actions. Blocking, isolation, account changes, process/service actions and policy promotion remain behind Cherry Central policy checks and an authenticated human approver.

Use synthetic or formally approved pilot data. Do not commit customer payloads, private PCAPs, credentials, internal IP inventories or real API keys.

## Next implementation work

The executable sequence, file targets, test gates and Codex prompt are in [`CODEX_PLAN.md`](CODEX_PLAN.md). Repository-specific rules for coding agents are in [`AGENTS.md`](AGENTS.md).
