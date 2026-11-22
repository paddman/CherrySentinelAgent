from __future__ import annotations

import asyncio
import json
import sqlite3
from datetime import datetime, timedelta, timezone

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

from app.audit import AuditStore
from app.intel_exchange import IntelExchangeStore
from app.investigation import (
    InvestigationPlan,
    InvestigationRequest,
    InvestigationResult,
    ToolCallTrace,
)
from app.investigation_hardened import (
    HardenedInvestigationOrchestrator,
    InvestigationDeadlineExceeded,
)
from app.llm import OpenAICompatibleLlm
from app.models import AnalystDecision, EvidenceCitation, EvidenceItem, EvidenceSource, IncidentInput
from app.playbooks import PlaybookRetriever
from app.routes_v2 import router
from app.security import TenantContext, TenantRegistry


class EmptyCentral:
    async def list_incidents(self, tenant, take=100):
        raise AssertionError("Tests with Central disabled must not call its connector")


def tenant(tenant_id="tenant-a") -> TenantContext:
    return TenantContext(
        tenant_id=tenant_id,
        name=tenant_id,
        central_url=None,
        central_api_key=None,
        central_verify_tls=True,
        metadata={},
    )


def headers(tenant_id="tenant-a", api_key="secret-a") -> dict[str, str]:
    return {"X-Cherry-Tenant": tenant_id, "X-Cherry-Api-Key": api_key}


def snapshot(number=1, *, tenant_id="tenant-a", incident_id="incident-shared", created_at=None):
    created = created_at or datetime(2026, 1, 1, tzinfo=timezone.utc) + timedelta(minutes=number)
    ref_id = f"incident:{incident_id}"
    return InvestigationResult(
        investigation_id=f"inv_{number:032x}",
        tenant_id=tenant_id,
        incident_id=incident_id,
        created_at_utc=created,
        plan=InvestigationPlan(source="deterministic", tools=[]),
        trace=[
            ToolCallTrace(
                step=1,
                tool="sentinel_brain_analysis",
                status="success",
                started_at_utc=created,
                evidence_refs=[ref_id],
            )
        ],
        stopped_reason="completed",
        decision=AnalystDecision(
            analysis_id=f"ai_{number:032x}",
            tenant_id=tenant_id,
            incident_id=incident_id,
            created_at_utc=created,
            classification="Suspicious activity",
            risk_score=40,
            confidence=0.7,
            summary_th="ผลการตรวจสอบจากหลักฐาน",
            model="deterministic",
            analysis_mode="single",
            deterministic_fallback=True,
            evidence=[EvidenceCitation(ref_id=ref_id, reason="Observed incident")],
        ),
        evidence_catalog=[
            EvidenceItem(
                ref_id=ref_id,
                source=EvidenceSource.INCIDENT,
                kind="incident_summary",
                timestamp_utc=created,
                summary="A bounded evidence snapshot",
            )
        ],
        timeline=[{"timestamp_utc": created, "ref_id": ref_id}],
        untimed_evidence_refs=[],
    )


@pytest.fixture
def investigator(settings, store, brain):
    return HardenedInvestigationOrchestrator(
        brain=brain,
        store=store,
        intel=IntelExchangeStore(settings.database_path),
        central=EmptyCentral(),
        llm=OpenAICompatibleLlm(settings),
        playbooks=PlaybookRetriever(settings.playbook_dir),
    )


@pytest.fixture
def history_client(settings, store, investigator):
    settings.allow_dev_tenant = False
    settings.tenants_json = json.dumps(
        [
            {"tenant_id": "tenant-a", "api_key": "secret-a"},
            {"tenant_id": "tenant-b", "api_key": "secret-b"},
        ]
    )
    app = FastAPI()
    app.include_router(router)
    app.state.store = store
    app.state.tenant_registry = TenantRegistry(settings)
    app.state.investigation_orchestrator = investigator
    with TestClient(app, raise_server_exceptions=False) as client:
        yield client


def investigation_audits(settings, *, outcome):
    with sqlite3.connect(settings.database_path) as conn:
        return conn.execute(
            "SELECT detail_json FROM audit_log WHERE action=? AND outcome=?",
            ("ai.investigation.run", outcome),
        ).fetchall()


@pytest.mark.asyncio
async def test_repeated_incident_investigations_keep_immutable_snapshots_after_restart(
    settings, store, investigator
):
    request = InvestigationRequest(
        incident=IncidentInput(incident_id="same-incident", title="First investigation", severity="Low"),
        include_central_related=False,
        max_steps=1,
    )
    first = await investigator.run(tenant(), request)
    first_json = first.model_dump(mode="json")
    request.incident.title = "Second investigation with new evidence"
    request.incident.severity = "Critical"
    second = await investigator.run(tenant(), request)

    assert first.investigation_id != second.investigation_id
    assert first.decision.analysis_id != second.decision.analysis_id
    assert store.get_analysis("tenant-a", "same-incident").analysis_id == second.decision.analysis_id
    reopened = AuditStore(settings.database_path)
    assert reopened.get_investigation("tenant-a", first.investigation_id).model_dump(mode="json") == first_json
    assert reopened.get_investigation("tenant-a", second.investigation_id) == second
    assert {row.investigation_id for row in reopened.list_investigations("tenant-a").items} == {
        first.investigation_id,
        second.investigation_id,
    }
    assert len(investigation_audits(settings, outcome="success")) == 2


def test_snapshot_storage_is_tenant_scoped_even_for_equal_ids(store):
    a = snapshot(1)
    b = snapshot(1, tenant_id="tenant-b")
    b.decision.classification = "Tenant B distinct finding"
    store.save_investigation("tenant-a", a)
    store.save_investigation("tenant-b", b)
    assert store.get_investigation("tenant-a", a.investigation_id) == a
    assert store.get_investigation("tenant-b", b.investigation_id) == b
    assert store.get_investigation("tenant-c", a.investigation_id) is None
    assert [r.classification for r in store.list_investigations("tenant-a").items] == [a.decision.classification]
    assert store.list_investigations("tenant-c").items == []


@pytest.mark.parametrize("mismatch", ["outer_tenant", "decision_tenant", "decision_incident"])
def test_save_rejects_identity_mismatch_without_a_success_record(settings, store, mismatch):
    result = snapshot()
    if mismatch == "outer_tenant":
        result.tenant_id = "tenant-b"
    elif mismatch == "decision_tenant":
        result.decision.tenant_id = "tenant-b"
    else:
        result.decision.incident_id = "other-incident"
    with pytest.raises(ValueError):
        store.save_investigation("tenant-a", result)
    assert store.list_investigations("tenant-a").items == []
    assert store.list_investigations("tenant-b").items == []
    assert investigation_audits(settings, outcome="success") == []


def test_duplicate_save_cannot_replace_an_existing_snapshot(settings, store):
    original = snapshot()
    store.save_investigation("tenant-a", original)
    changed = original.model_copy(deep=True)
    changed.decision.summary_th = "Changed after persistence"
    with pytest.raises(sqlite3.IntegrityError):
        store.save_investigation("tenant-a", changed)
    assert store.get_investigation("tenant-a", original.investigation_id) == original
    assert len(investigation_audits(settings, outcome="success")) == 1


def test_history_pagination_remains_stable_when_a_new_investigation_arrives(store):
    results = [snapshot(i) for i in range(1, 4)]
    for result in results:
        store.save_investigation("tenant-a", result)
    first = store.list_investigations("tenant-a", limit=2)
    assert [r.investigation_id for r in first.items] == [results[2].investigation_id, results[1].investigation_id]
    assert first.next_cursor == results[1].investigation_id

    store.save_investigation("tenant-a", snapshot(4))
    second = store.list_investigations("tenant-a", limit=2, before=first.next_cursor)
    assert [r.investigation_id for r in second.items] == [results[0].investigation_id]
    assert second.next_cursor is None


def test_equal_timestamp_pagination_uses_a_deterministic_tiebreaker(store):
    created = datetime(2026, 1, 1, tzinfo=timezone.utc)
    results = [snapshot(i, created_at=created) for i in range(1, 4)]
    for result in results:
        store.save_investigation("tenant-a", result)
    first = store.list_investigations("tenant-a", limit=1)
    second = store.list_investigations("tenant-a", limit=1, before=first.next_cursor)
    third = store.list_investigations("tenant-a", limit=1, before=second.next_cursor)
    assert [page.items[0].investigation_id for page in (first, second, third)] == [
        results[2].investigation_id,
        results[1].investigation_id,
        results[0].investigation_id,
    ]
    assert third.next_cursor is None


def test_history_filter_is_exact_and_does_not_leak_other_tenants(store):
    for result in [
        snapshot(1, incident_id="incident-1"),
        snapshot(2, incident_id="incident-10"),
        snapshot(3, tenant_id="tenant-b", incident_id="incident-1"),
    ]:
        store.save_investigation(result.tenant_id, result)
    page = store.list_investigations("tenant-a", incident_id="incident-1")
    assert [row.investigation_id for row in page.items] == [snapshot(1).investigation_id]
    assert store.list_investigations("tenant-a", incident_id="absent").items == []


@pytest.mark.parametrize("request_headers,expected", [
    ({}, 422),
    (headers(api_key="wrong-key"), 401),
    (headers(tenant_id="tenant-b", api_key="secret-a"), 401),
    (headers(tenant_id="missing", api_key="secret-a"), 401),
])
def test_history_endpoints_require_the_authenticated_tenant_key(history_client, store, request_headers, expected):
    result = snapshot()
    store.save_investigation("tenant-a", result)
    for path in ["/v1/investigations", f"/v1/investigations/{result.investigation_id}"]:
        response = history_client.get(path, headers=request_headers)
        assert response.status_code == expected


def test_foreign_ids_and_cursors_are_indistinguishable_from_missing(history_client, store):
    foreign = snapshot(1, tenant_id="tenant-b")
    store.save_investigation("tenant-b", foreign)
    missing_id = "inv_ffffffffffffffffffffffffffffffff"
    foreign_get = history_client.get(f"/v1/investigations/{foreign.investigation_id}", headers=headers())
    missing_get = history_client.get(f"/v1/investigations/{missing_id}", headers=headers())
    assert foreign_get.status_code == missing_get.status_code == 404
    assert foreign_get.json() == missing_get.json() == {"detail": "investigation_not_found"}
    for cursor in [foreign.investigation_id, missing_id]:
        response = history_client.get("/v1/investigations", params={"before": cursor}, headers=headers())
        assert response.status_code == 404
        assert response.json() == {"detail": "investigation_cursor_not_found"}


def test_http_history_returns_camel_case_snapshots_and_compact_summaries_without_rerunning(
    history_client, investigator, brain, store, monkeypatch
):
    result = snapshot()
    store.save_investigation("tenant-a", result)

    async def unexpected_run(*args, **kwargs):
        raise AssertionError("History retrieval must not run investigation or analysis")

    monkeypatch.setattr(investigator, "run", unexpected_run)
    monkeypatch.setattr(brain, "analyze", unexpected_run)
    monkeypatch.setattr(brain, "analyze_with_evidence", unexpected_run)
    response = history_client.get(f"/v1/investigations/{result.investigation_id}", headers=headers())
    assert response.status_code == 200
    assert response.json() == result.model_dump(mode="json", by_alias=True)
    assert response.headers["cache-control"] == "no-store"
    listing = history_client.get(
        "/v1/investigations", params={"incident_id": result.incident_id, "limit": 1}, headers=headers()
    )
    assert listing.status_code == 200
    assert listing.headers["cache-control"] == "no-store"
    data = listing.json()
    assert data["nextCursor"] is None
    assert len(data["items"]) == 1
    summary = data["items"][0]
    assert summary["investigationId"] == result.investigation_id
    assert summary["incidentId"] == result.incident_id
    assert summary["analysisId"] == result.decision.analysis_id
    assert summary["riskScore"] == result.decision.risk_score
    assert summary["toolCallCount"] == len(result.trace)
    assert summary["evidenceCount"] == len(result.evidence_catalog)
    assert "trace" not in summary
    assert "evidenceCatalog" not in summary
    assert "decision" not in summary


@pytest.mark.parametrize("limit", [0, -1, 101])
def test_history_http_rejects_unbounded_or_invalid_page_limits(history_client, limit):
    response = history_client.get("/v1/investigations", params={"limit": limit}, headers=headers())
    assert response.status_code == 422


def test_successful_run_returns_the_exact_durable_snapshot_for_the_authenticated_tenant(history_client, store):
    response = history_client.post(
        "/v1/investigations/run",
        headers=headers(),
        json={
            "tenantId": "tenant-b",
            "incident": {"incidentId": "http-success", "title": "Test", "tenantId": "tenant-b"},
            "includeCentralRelated": False,
            "maxSteps": 1,
        },
    )
    assert response.status_code == 200
    data = response.json()
    assert data["tenantId"] == data["decision"]["tenantId"] == "tenant-a"
    assert data["evidenceCatalog"]
    saved = history_client.get(f"/v1/investigations/{data['investigationId']}", headers=headers())
    assert saved.status_code == 200
    assert saved.json() == data
    assert store.get_investigation("tenant-b", data["investigationId"]) is None


def test_snapshot_and_success_audit_are_committed_atomically(settings, store):
    with sqlite3.connect(settings.database_path) as conn:
        conn.executescript(
            """
            CREATE TRIGGER reject_investigation_success
            BEFORE INSERT ON audit_log
            WHEN NEW.action = 'ai.investigation.run' AND NEW.outcome = 'success'
            BEGIN
                SELECT RAISE(ABORT, 'forced audit failure');
            END;
            """
        )
    result = snapshot()
    with pytest.raises(sqlite3.IntegrityError):
        store.save_investigation("tenant-a", result)
    assert store.get_investigation("tenant-a", result.investigation_id) is None
    assert investigation_audits(settings, outcome="success") == []


def test_failed_persistence_does_not_return_success_or_claim_saved_history(
    history_client, settings, store, monkeypatch
):
    def failed_save(*args, **kwargs):
        raise sqlite3.OperationalError("database unavailable")

    monkeypatch.setattr(store, "save_investigation", failed_save)
    response = history_client.post(
        "/v1/investigations/run",
        headers=headers(),
        json={"incident": {"incidentId": "save-failure", "title": "Test"}, "maxSteps": 1},
    )
    assert response.status_code == 500
    assert response.json() == {"detail": "investigation_failed"}
    assert store.list_investigations("tenant-a").items == []
    assert investigation_audits(settings, outcome="success") == []


@pytest.mark.asyncio
async def test_hard_deadline_does_not_persist_a_completed_snapshot(settings, store, investigator, brain, monkeypatch):
    async def slow_analysis(*args, **kwargs):
        await asyncio.sleep(0.5)
        raise AssertionError("The deadline must cancel analysis")

    monkeypatch.setattr(brain, "analyze_with_evidence", slow_analysis)
    request = InvestigationRequest(
        incident=IncidentInput(incident_id="deadline-history", title="Slow analysis"),
        include_central_related=False,
        max_steps=1,
    ).model_copy(update={"max_runtime_seconds": 0.03})
    with pytest.raises(InvestigationDeadlineExceeded):
        await investigator.run(tenant(), request)
    assert store.list_investigations("tenant-a").items == []
    assert investigation_audits(settings, outcome="success") == []
    assert len(investigation_audits(settings, outcome="timeout")) == 1


def test_initialization_adds_history_without_changing_existing_analysis(tmp_path):
    database = tmp_path / "old-brain.db"
    old_analysis = snapshot().decision
    with sqlite3.connect(database) as conn:
        conn.executescript(
            """
            CREATE TABLE analyses (
                tenant_id TEXT NOT NULL, incident_id TEXT NOT NULL,
                analysis_id TEXT NOT NULL, created_at_utc TEXT NOT NULL,
                model TEXT NOT NULL, analysis_mode TEXT NOT NULL,
                deterministic_fallback INTEGER NOT NULL,
                prompt_tokens INTEGER NOT NULL DEFAULT 0,
                completion_tokens INTEGER NOT NULL DEFAULT 0,
                request_json TEXT NOT NULL, analysis_json TEXT NOT NULL,
                PRIMARY KEY (tenant_id, incident_id)
            );
            """
        )
        conn.execute(
            "INSERT INTO analyses VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            (
                old_analysis.tenant_id,
                old_analysis.incident_id,
                old_analysis.analysis_id,
                old_analysis.created_at_utc.isoformat(),
                old_analysis.model,
                old_analysis.analysis_mode,
                1, 0, 0, "{}", old_analysis.model_dump_json(by_alias=True),
            ),
        )
    upgraded = AuditStore(database)
    assert upgraded.get_analysis("tenant-a", old_analysis.incident_id) == old_analysis
    assert upgraded.list_investigations("tenant-a").items == []
    result = snapshot(2)
    upgraded.save_investigation("tenant-a", result)
    reopened = AuditStore(database)
    assert reopened.get_investigation("tenant-a", result.investigation_id) == result
    assert reopened.get_analysis("tenant-a", old_analysis.incident_id) == old_analysis
