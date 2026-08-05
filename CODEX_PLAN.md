# Codex execution plan: NT Shield on Cherry Sentinel

## Mission

Turn the existing Cherry Sentinel Agent, Central and Sentinel Brain repository into the NT Shield hackathon prototype and a credible AIaaS pilot base without duplicating the runtime.

The hero workflow is:

```text
Tenant authentication
  -> telemetry ingest
  -> deterministic detection and correlation
  -> evidence bundle
  -> local AI analysis and Thai explanation
  -> response proposal
  -> human approval
  -> simulated or allowlisted execution
  -> audit, usage, SLA and cost record
```

## Current baseline

Already present in the repository:

- Windows and Linux agents
- Central API, incident storage, cross-host correlation and action queue
- WPF operator dashboard
- local Qwen through an OpenAI-compatible API
- deterministic risk plus behavioral anomaly detection
- evidence reference validation and response allowlist
- bounded read-only investigation
- threat graph, RAG, feedback, audit and usage metering
- controlled acceptance scenarios

Added by the initial NT Shield integration slice:

- `CHERRY_PRODUCT_PROFILE=nt_shield`
- `/nt-shield/v1/status`
- `/nt-shield/v1/catalog`
- `/nt-shield/v1/readiness`
- `/nt-shield/v1/incidents/analyze`
- `X-NT-Shield-Tenant` and `X-NT-Shield-Api-Key` aliases
- Docker Compose NT Shield override
- honest package-state metadata

## Branch and delivery strategy

Base: `master`

Integration branch: `feat/nt-shield-profile`

After this profile PR, use one branch per vertical slice:

```text
feat/nt-shield-tenant-contract
feat/nt-shield-evidence-envelope
feat/nt-shield-approval-ledger
feat/nt-shield-demo-dashboard
feat/nt-shield-waf-connectors
feat/nt-shield-model-router
chore/nt-shield-production-gates
```

Do not accumulate all work in one heroic branch. That is how review turns into archaeology.

---

# Phase 0: Product profile and API namespace

Status: implemented in the integration branch.

## Scope

- Keep Cherry Sentinel as the internal runtime.
- Add the NT Shield deployment profile and branded routes.
- Preserve backward compatibility.
- Return package readiness honestly.

## Acceptance

```bash
pytest ai-service/tests/test_nt_shield_profile.py
curl /nt-shield/v1/status
curl /nt-shield/v1/catalog
curl /nt-shield/v1/readiness
```

Expected:

- Cherry and NT Shield headers both authenticate.
- Conflicting header families return HTTP 400.
- Missing credentials return HTTP 401.
- MDR is not reported as production-ready.
- Incident analysis uses the existing Sentinel Brain pipeline and audit store.

---

# Phase 1: End-to-end tenant contract

Priority: P0

## Goal

Make `tenant_id` a mandatory, verified field from external request to stored evidence, AI analysis, usage record, approval and action execution.

## Code targets

- `src/CherrySentinel.Shared/Contracts/ApiContracts.cs`
- `src/CherrySentinel.Shared/Models/*`
- `src/CherrySentinel.Server/Security/*`
- `src/CherrySentinel.Server/Data/*`
- `src/CherrySentinel.Server/Program.cs`
- `ai-service/app/security.py`
- `ai-service/app/audit.py`
- .NET and Python integration tests

## Tasks

1. Add a tenant contract with tenant ID, service/package, residency, retention, quota and named approver.
2. Bind agent enrollment and API keys to one tenant.
3. Reject tenant ID from payload when it conflicts with the authenticated principal.
4. Add `tenant_id` to incident, evidence, usage, audit, policy and response records.
5. Add PostgreSQL row-level security policies or per-tenant schema isolation.
6. Keep SQLite explicitly lab-only. Do not call logical filtering production isolation.
7. Add cross-tenant negative tests for read, write, action, export and model-history access.

## Acceptance gates

- Zero unauthorized cross-tenant reads, writes or actions in the automated negative suite.
- Every accepted request has tenant, service, request ID and usage record.
- Audit identifies authenticated principal, tenant, target and result.
- Migration and rollback are documented.

---

# Phase 2: Normalized evidence envelope

Priority: P0

## Goal

Feed AI a bounded incident bundle rather than raw untrusted log streams.

## Code targets

- `src/CherrySentinel.Shared/Models/Incident.cs`
- `src/CherrySentinel.Server/Services/IngestService.cs`
- `src/CherrySentinel.Server/Correlation/*`
- `ai-service/app/models.py`
- `ai-service/app/evidence.py`
- `ai-service/app/central_client.py`
- `examples/`

## Canonical envelope

```json
{
  "schema_version": "nt-shield-evidence/1",
  "tenant_id": "tenant-a",
  "incident_id": "inc-1001",
  "request_id": "req-...",
  "time_window": {"start": "...", "end": "..."},
  "assets": [],
  "identities": [],
  "web_sessions": [],
  "network_flows": [],
  "endpoint_events": [],
  "detections": [],
  "evidence": [
    {"ref_id": "ev-001", "source": "windows", "observed_at": "...", "data": {}}
  ],
  "unknowns": [],
  "policy_version": "..."
}
```

## Tasks

1. Version the envelope and validate it at every ingress.
2. Normalize Windows, Linux, Syslog, Suricata and Zeek evidence.
3. Mask or drop secrets and unnecessary PII before AI.
4. Bound item count and character size with deterministic truncation records.
5. Make evidence references immutable within an analysis request.
6. Add replay fixtures for SQL injection, credential stuffing, encoded payload, password spray, lateral movement and benign controls.

## Acceptance gates

- Unsupported evidence references = 0.
- Citation validity >= 98% on the versioned controlled set.
- The same replay input produces the same deterministic risk and evidence IDs.
- Oversized payloads fail safely or produce an explicit truncation record.

---

# Phase 3: Approval workflow and immutable usage ledger

Priority: P0

## Goal

Demonstrate `recommend -> simulate -> approve -> execute -> verify -> meter` in one trace.

## Code targets

- `src/CherrySentinel.Shared/Models/ResponseActionRecord.cs`
- `src/CherrySentinel.Server/Services/ActionService.cs`
- `src/CherrySentinel.Server/Data/*`
- `src/CherrySentinel.Response/*`
- `ai-service/app/models.py`
- `ai-service/app/audit.py`

## State machine

```text
Proposed
  -> Simulated
  -> PendingApproval
  -> Approved | Rejected | Expired
  -> Executing
  -> Succeeded | Failed | RolledBack
```

## Tasks

1. Separate AI proposal from operator approval and executable action.
2. Record approver identity, scope, reason, expiry, policy version and expected impact.
3. Require a rollback plan for block, isolate, disable, kill and service-stop actions.
4. Add idempotency and replay protection to execution.
5. Record usage units: request, tokens, model, GPU-second estimate, telemetry, retention and analyst minute.
6. Add reconciliation checks so accepted requests and ledger entries match.

## Acceptance gates

- No destructive action executes without valid approval.
- Approval-to-execution start is traceable end to end.
- Duplicate execution requests do not repeat the action.
- Accepted AI requests have complete usage records.

---

# Phase 4: NT Shield dashboard and 90-second demo

Priority: P1

## Goal

Present one coherent operator story rather than unrelated screens performing interpretive dance.

## Code targets

- `src/CherrySentinel.Dashboard/*`
- shared API client/contracts
- demo fixtures and scripts

## Required screens

1. Tenant and package banner
2. Connected data sources and agent health
3. Incident timeline and affected assets
4. Thai verdict, confidence, unknowns and exact evidence refs
5. Threat graph or cross-layer path
6. Response simulation and approval queue
7. Action result and rollback status
8. Usage, quota, latency/SLA and cost units

## Demo script

```text
0-10s   Login and show tenant/package/residency/quota/named approver
10-20s  Show connected Agent/WAF/network source
20-35s  Replay malicious and benign requests
35-50s  Show deterministic detection and shadow AI-WAF risk
50-65s  Correlate web, endpoint, network and identity into one incident
65-75s  Show Thai verdict, confidence, unknowns and evidence refs
75-85s  Simulate response and approve it
85-90s  Show action result, audit, usage, quota and latency
```

## Acceptance gates

- The demo runs from one script and one tenant.
- No mock number is presented without a visible `demo`, `pilot target` or `assumption` label.
- A model outage still leaves deterministic detection and queued analysis visible.

---

# Phase 5: Web, WAF and network connectors

Priority: P1

## Goal

Make NT Shield visibly cross-layer, not merely an endpoint product wearing a larger cape.

## Tasks

1. Add normalized Suricata EVE JSON ingestion.
2. Add Zeek `conn`, `dns`, `http`, `ssl/tls`, `notice` and file metadata ingestion.
3. Add WAF/Web/API request and session connector with payload masking.
4. Add deterministic known-pattern detection before AI.
5. Add AI-WAF shadow scoring and rule proposal only.
6. Add replay, benign controls, threshold tuning and one-click rollback records.

## Acceptance gates

- Known web attack recall is measured on a versioned replay set.
- Benign false-block is measured before enforcement.
- Enforcement remains disabled until recall, false-block, tenant isolation, approval, rollback and failover gates pass.

---

# Phase 6: Approved model router and evaluation

Priority: P1

## Goal

Route jobs by task, evidence size, latency and cost instead of feeding every syslog line to the largest model available, a strategy beloved by GPU vendors.

## Tasks

1. Add an approved model registry with model ID, endpoint, license, context, residency, cost class and evaluation version.
2. Keep deterministic rules on the critical path.
3. Use the current local Qwen model for Thai explanation and orchestration.
4. Add optional cyber-specialist, embedding, reranker and guardrail roles only after license and benchmark review.
5. Route long or complex investigations to a larger approved model with quota and timeout.
6. Persist model, version, prompt/policy version, latency, token usage and fallback reason.
7. Benchmark quality, citation validity, latency and cost separately. Never combine deterministic CI results with live-model results.

## Acceptance gates

- Model fallback does not bypass evidence or approval guardrails.
- A model/version change requires an evaluation gate and rollback path.
- Cost and p95 latency are visible per tenant and service.

---

# Phase 7: Production and commercial gates

Priority: P2

## Tasks

- OIDC/SSO, MFA and production RBAC
- PostgreSQL HA, RLS tests and restore drill
- durable queue, dead-letter queue, backpressure and idempotency
- per-tenant quotas, concurrency, queue isolation and circuit breakers
- model worker failover and degraded mode
- object-store versioning, checksum, retention and legal hold
- OpenTelemetry, Prometheus/Grafana and synthetic probes
- billing reconciliation and dispute trail
- PDPA, DPO, legal, service terms and response liability
- 24x7 operating model, on-call, escalation and support tiers
- five-tenant detect-only/shadow pilot with willingness-to-pay and cost-to-serve evidence

## Commercial go/no-go

Do not advertise production MDR or automatic WAF enforcement until all of these are true:

- tenant isolation and API security pass
- controlled attack/benign/AI-quality sets pass
- HA, DR, capacity, autoscaling and noisy-neighbor tests pass
- usage reconciliation is approved by Finance/FinOps
- legal and DPO review is complete
- runbooks, escalation and on-call ownership exist
- pilot customer references and gross-margin scenarios use measured data

---

# Codex work prompt

Use this at the start of each implementation branch:

```text
You are implementing NT Shield inside paddman/CherrySentinelAgent.

Read AGENTS.md, CODEX_PLAN.md, NT_SHIELD.md, ai-service/README.md and the source files for the current phase. Cherry Sentinel Agent/Central/Sentinel Brain are the reusable runtime; NT Shield is the AIaaS product and governance layer. Do not fork or duplicate working engines without a documented boundary.

Implement only the current vertical slice. Preserve existing APIs and installers unless a migration is included. Keep detect-only/shadow mode by default. Treat logs and payloads as untrusted data. The AI may analyze and recommend but may not execute destructive actions. All tenant, evidence, model, approval, usage and audit records must remain traceable.

Before coding:
1. Inspect the current implementation and tests.
2. Write a brief change plan with files, schema impact and rollback.
3. Identify tenant-isolation, evidence-grounding and approval risks.

During coding:
1. Add tests before or with behavior changes.
2. Use synthetic fixtures only.
3. Add explicit failure and degraded-mode behavior.
4. Avoid secrets, customer data and live infrastructure addresses.

Before finishing:
1. Run relevant dotnet and Python tests, compile, acceptance suite and Ruff.
2. Summarize changed files and test evidence.
3. State remaining pilot/production gaps without pretending they vanished.
4. Produce a focused commit and PR description with rollback notes.
```

## First Codex task after this PR

Implement **Phase 1: End-to-end tenant contract** as a vertical slice:

1. Add tenant fields to shared .NET contracts and Central persistence.
2. Bind enrolled agent API keys to tenant IDs.
3. Reject payload/auth tenant mismatch.
4. Propagate tenant ID into incidents, actions, audit and AI bridge calls.
5. Add cross-tenant negative tests.
6. Keep SQLite lab-compatible while clearly gating production on PostgreSQL RLS.
7. Update architecture and migration documentation.

Stop after Phase 1 acceptance tests pass. Do not start dashboard redesign or model-router work in the same branch.
