# Coding-agent instructions for Cherry Sentinel and NT Shield

Read `CODEX_PLAN.md`, `NT_SHIELD.md`, `ai-service/README.md` and the relevant source files before changing code.

## Non-negotiable boundaries

1. Keep detect-only or shadow mode as the default.
2. AI output is advisory. Destructive actions require policy validation and an authenticated human approval record.
3. Never give the model arbitrary shell, SQL, HTTP target or tool-selection authority.
4. Every AI conclusion must preserve exact evidence references, confidence, unknowns, model/version and policy trace.
5. Every event, query, analysis, usage record and action must be tenant-scoped. Reject ambiguity instead of guessing a tenant.
6. Do not commit real customer logs, PCAPs, credentials, public service addresses, internal asset lists or API keys.
7. Do not advertise pilot targets as measured production results.

## Working style

- Prefer small vertical slices that leave the repository buildable.
- Reuse Cherry Sentinel Agent, Central and Sentinel Brain. Do not create a parallel NT Shield engine unless the plan explicitly requires a new boundary.
- Maintain backward compatibility for existing `/v1/*` routes and `X-Cherry-*` headers.
- Add migrations, rollback notes, tests and documentation with schema or behavior changes.
- Keep deterministic rules on critical paths. Route only normalized evidence bundles to AI.

## Required checks

```bash
dotnet restore
dotnet build CherrySentinel.sln -c Release
dotnet test CherrySentinel.sln -c Release

python -m pip install -e "./ai-service[dev]"
python -m compileall -q ai-service/app ai-service/tests ai-service/scripts
pytest ai-service/tests
python ai-service/scripts/evaluate_acceptance.py \
  --suite ai-service/evaluation/acceptance-suite.json \
  --out ai-service/acceptance-report.json
ruff check ai-service/app ai-service/tests ai-service/scripts
```

Run the checks relevant to the touched surface. A task is not complete when only the happy-path screenshot exists.

## Definition of done

- Tests prove tenant isolation, evidence validity and approval behavior.
- Failure and degraded-mode behavior is explicit.
- No secret or customer data enters Git history.
- API and operator-visible behavior is documented.
- The PR states what is implemented, what remains a pilot assumption, and how to roll back.
