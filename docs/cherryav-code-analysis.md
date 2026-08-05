# CherryAV Code Analysis in Cherry Sentinel Brain

Cherry Sentinel Brain accepts privacy-bounded source-scan findings from CherryAV, applies a deterministic risk baseline, and optionally asks the configured local/OpenAI-compatible LLM for a guarded second-pass review.

## Endpoint

```http
POST /v1/code-scans/analyze
X-Cherry-Tenant: <tenant-id>
X-Cherry-Api-Key: <tenant-api-key>
Content-Type: application/json
```

The endpoint uses the existing Sentinel tenant registry. Configure a tenant through `CHERRY_TENANTS_JSON`, for example:

```json
[
  {
    "tenant_id": "production",
    "name": "Production Applications",
    "api_key": "replace-this-key",
    "enabled": true
  }
]
```

The LLM uses the existing settings:

```text
CHERRY_LLM_ENABLED=true
CHERRY_LLM_BASE_URL=http://127.0.0.1:8000/v1
CHERRY_LLM_API_KEY=local
CHERRY_LLM_MODEL=qwen3.5-9b
```

Optional code-review limits default to:

```text
CHERRY_CODE_SCAN_MAX_LLM_FINDINGS=80
CHERRY_CODE_SCAN_MAX_LLM_CHARS=60000
CHERRY_CODE_SCAN_LLM_MAX_TOKENS=2800
```

The implementation reads these settings when present and otherwise uses the defaults above.

## Other routes

```http
GET /v1/code-scans?limit=50
GET /v1/code-scans/{scan_id}
```

All records are tenant-scoped in the same SQLite database used by Sentinel Brain. A tenant cannot retrieve another tenant's scan merely by knowing the scan ID, a useful feature that should not need saying but history has been unkind.

## Analysis behavior

1. Validate a strict JSON schema. Unknown fields, duplicate finding IDs, inconsistent finding counts, absolute paths and parent traversal are rejected.
2. Re-redact common credentials, private keys, bearer tokens and credentials embedded in URLs.
3. Build a deterministic finding-by-finding assessment and risk score.
4. Select the highest-priority bounded subset for the LLM.
5. Mark snippets as untrusted evidence and instruct the model not to follow code comments or strings.
6. Accept only structured JSON from the model.
7. Drop assessments or derived findings that cite evidence outside the subset shown to the model.
8. Fill omitted findings with deterministic assessments.
9. Enforce a minimum risk score for confirmed/likely High and Critical findings.
10. Persist the sanitized request and final analysis and append an audit event.

When the LLM is disabled, unreachable or returns invalid JSON, the endpoint still returns and stores the deterministic analysis with `deterministicFallback=true`. It does not manufacture an AI result to preserve appearances.

## Response outline

```json
{
  "accepted": true,
  "scanId": "cas-...",
  "analysisId": "caa-...",
  "verdict": "critical",
  "riskScore": 92,
  "confidence": 0.94,
  "summaryTh": "...",
  "model": "qwen3.5-9b",
  "llmUsed": true,
  "deterministicFallback": false,
  "assessments": [
    {
      "findingId": "caf-...",
      "verdict": "confirmed",
      "adjustedSeverity": "critical",
      "confidence": 0.96,
      "rationaleTh": "...",
      "remediation": "...",
      "evidenceRefs": ["caf-..."]
    }
  ],
  "derivedFindings": [],
  "attackSurface": ["nodejs", "command-injection"],
  "priorityActions": ["..."],
  "warnings": []
}
```

## Operational notes

- Keep Sentinel Brain behind TLS and do not put tenant keys in source repositories.
- The endpoint intentionally receives findings and bounded snippets, not complete repositories.
- Static patterns plus an LLM are triage aids. Build gates should use an explicit severity threshold and still allow a reviewed waiver process.
- Run the service through `app.main_v2:app`; that module registers the code-scan router.
- CherryAV should target Sentinel Brain's service port, normally `8088`, not the .NET Central agent-ingest port `7443`.
