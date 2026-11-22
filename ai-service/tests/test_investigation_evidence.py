from __future__ import annotations

import hashlib
from datetime import datetime, timezone
from types import SimpleNamespace

import pytest

from app.analyst import SentinelBrain
from app.anomaly import HybridAnomalyEngine
from app.intel_exchange import IntelExchangeStore
from app.investigation import InvestigationOrchestrator, InvestigationRequest
from app.llm import OpenAICompatibleLlm
from app.models import IncidentInput
from app.playbooks import PlaybookRetriever
from app.security import TenantContext


class NoCentral:
    async def list_incidents(self, tenant, take=100):
        raise AssertionError("Central is disabled in this test")


def tenant(*, central_url=None):
    return TenantContext(
        tenant_id="tenant-a",
        name="Tenant A",
        central_url=central_url,
        central_api_key="operator-secret-never-persist",
        central_verify_tls=True,
        metadata={},
    )


def orchestrator(settings, store, brain, central=None):
    return InvestigationOrchestrator(
        brain=brain,
        store=store,
        intel=IntelExchangeStore(settings.database_path),
        central=central or NoCentral(),
        llm=OpenAICompatibleLlm(settings),
        playbooks=PlaybookRetriever(settings.playbook_dir),
    )


def assert_all_decision_refs_resolve(result):
    known = {item.ref_id for item in result.evidence_catalog}
    refs = {item.ref_id for item in result.decision.evidence}
    for item in result.decision.recommended_actions + result.decision.risk_signals + result.decision.specialists:
        refs.update(item.evidence_refs)
    for edge in result.decision.threat_graph.edges:
        refs.update(edge.evidence_refs)
    assert refs <= known
    assert all(set(call.evidence_refs) <= known for call in result.trace)


@pytest.mark.asyncio
async def test_catalog_records_the_exact_bounded_evidence_used_by_brain(settings, store):
    settings.max_evidence_items = 10
    brain = SentinelBrain(
        settings=settings,
        store=store,
        anomaly_engine=HybridAnomalyEngine(store, settings),
        llm=OpenAICompatibleLlm(settings),
        playbooks=PlaybookRetriever(settings.playbook_dir),
    )
    incident = IncidentInput(
        incident_id="bounded-catalog",
        source_ip="8.8.8.8",
        raw_evidence=[{"ref_id": f"client:{i}", "summary": f"Observed event {i}"} for i in range(20)],
    )
    original = incident.model_dump(mode="json")
    result = await orchestrator(settings, store, brain).run(
        tenant(), InvestigationRequest(incident=incident, include_central_related=False, max_steps=1)
    )
    assert len(result.evidence_catalog) == 10
    assert {item.ref_id for item in result.evidence_catalog} == {
        "incident:bounded-catalog", *[f"client:{i}" for i in range(9)]
    }
    ioc_call = next(call for call in result.trace if call.tool == "ioc_lookup")
    assert ioc_call.evidence_refs == []
    assert ioc_call.omitted_evidence_refs
    assert set(ioc_call.omitted_evidence_refs).isdisjoint(item.ref_id for item in result.evidence_catalog)
    assert incident.model_dump(mode="json") == original
    assert_all_decision_refs_resolve(result)
    assert store.get_investigation("tenant-a", result.investigation_id).evidence_catalog == result.evidence_catalog


@pytest.mark.asyncio
async def test_client_ref_cannot_impersonate_server_collected_tool_evidence(settings, store, brain):
    old_tool_ref = "intel-exchange:ip:" + hashlib.sha256(b"8.8.8.8").hexdigest()[:12]
    incident = IncidentInput(
        incident_id="tool-provenance",
        source_ip="8.8.8.8",
        raw_evidence=[
            {"ref_id": old_tool_ref, "kind": "client_supplied", "summary": "Client assertion only"}
        ],
    )
    result = await orchestrator(settings, store, brain).run(
        tenant(), InvestigationRequest(incident=incident, include_central_related=False, max_steps=1)
    )
    ioc_call = next(call for call in result.trace if call.tool == "ioc_lookup")
    catalog = {item.ref_id: item for item in result.evidence_catalog}
    assert catalog[old_tool_ref].summary == "Client assertion only"
    assert ioc_call.evidence_refs
    assert old_tool_ref not in ioc_call.evidence_refs
    for ref in ioc_call.evidence_refs:
        assert catalog[ref].kind == "cross_tenant_ioc_aggregate"
        assert catalog[ref].attributes["indicator"] == "8.8.8.8"
    assert_all_decision_refs_resolve(result)


@pytest.mark.asyncio
async def test_connector_failure_trace_does_not_persist_exception_credentials(settings, store, brain):
    class FailingCentral:
        async def list_incidents(self, tenant_context, take=100):
            raise RuntimeError("connector failed https://soc:exception-secret@central.invalid/?token=another-secret")

    result = await orchestrator(settings, store, brain, FailingCentral()).run(
        tenant(central_url="https://central.invalid"),
        InvestigationRequest(incident=IncidentInput(incident_id="failed-connector"), max_steps=3),
    )
    central_call = next(call for call in result.trace if call.tool == "central_related")
    assert central_call.status == "failed"
    assert central_call.output_summary
    serialized = store.get_investigation("tenant-a", result.investigation_id).model_dump_json()
    for secret in ["exception-secret", "another-secret", "operator-secret-never-persist", "https://soc:"]:
        assert secret not in serialized
    assert_all_decision_refs_resolve(result)


@pytest.mark.asyncio
async def test_timeline_preserves_observed_times_and_separates_unknown_times(settings, store, brain):
    unix_time = datetime(2026, 1, 1, 2, tzinfo=timezone.utc).timestamp()
    incident = IncidentInput(
        incident_id="timeline-observations",
        zeek_events=[{"uid": "connection-1", "ts": unix_time, "log": "conn"}],
        raw_evidence=[
            {"ref_id": "event:offset", "summary": "Offset time", "timestamp": "2026-01-01T03:00:00+03:00"},
            {"ref_id": "event:naive", "summary": "Naive UTC time", "timestampUtc": "2026-01-01T01:00:00"},
            {"ref_id": "event:invalid", "summary": "Unparseable time", "timestamp": "not-a-time"},
            {"ref_id": "event:missing", "summary": "No observed time"},
            {"ref_id": "event:epoch", "summary": "Unix epoch is not missing", "timestamp": 0},
            {
                "ref_id": "event:null-alias", "summary": "Null primary timestamp",
                "timestamp": None, "timestampUtc": "2026-01-01T03:00:00Z",
            },
            {
                "ref_id": "event:empty-alias", "summary": "Empty primary timestamp",
                "timestamp": "", "timestamp_utc": "2026-01-01T04:00:00Z",
            },
        ],
    )
    result = await orchestrator(settings, store, brain).run(
        tenant(), InvestigationRequest(incident=incident, include_central_related=False, max_steps=1)
    )
    timestamps = {entry.ref_id: entry.timestamp_utc for entry in result.timeline}
    assert timestamps["event:epoch"] == datetime(1970, 1, 1, tzinfo=timezone.utc)
    assert timestamps["event:offset"] == datetime(2026, 1, 1, tzinfo=timezone.utc)
    assert timestamps["event:naive"] == datetime(2026, 1, 1, 1, tzinfo=timezone.utc)
    assert timestamps["zeek:connection-1:0"] == datetime(2026, 1, 1, 2, tzinfo=timezone.utc)
    assert timestamps["event:null-alias"] == datetime(2026, 1, 1, 3, tzinfo=timezone.utc)
    assert timestamps["event:empty-alias"] == datetime(2026, 1, 1, 4, tzinfo=timezone.utc)
    assert [entry.timestamp_utc for entry in result.timeline] == sorted(timestamps.values())
    assert {"incident:timeline-observations", "event:invalid", "event:missing"} <= set(result.untimed_evidence_refs)
    assert set(timestamps).isdisjoint(result.untimed_evidence_refs)
    assert set(timestamps) | set(result.untimed_evidence_refs) == {item.ref_id for item in result.evidence_catalog}
    assert_all_decision_refs_resolve(result)


@pytest.mark.asyncio
async def test_persisted_catalog_preserves_existing_masked_evidence_normalization(settings, store, brain):
    incident = IncidentInput(
        incident_id="masked-catalog",
        description="Observed alice@example.com on EXAMPLE\\alice",
        username="EXAMPLE\\alice",
        privacy_mode="masked",
        raw_evidence=[
            {
                "ref_id": "raw:masked-user",
                "summary": "Observed alice@example.com on EXAMPLE\\alice",
                "username": "EXAMPLE\\alice",
            }
        ],
    )
    result = await orchestrator(settings, store, brain).run(
        tenant(), InvestigationRequest(incident=incident, include_central_related=False, max_steps=1)
    )
    saved = store.get_investigation("tenant-a", result.investigation_id)
    assert saved.evidence_catalog == result.evidence_catalog
    by_ref = {item.ref_id: item for item in saved.evidence_catalog}
    for ref in ["incident:masked-catalog", "raw:masked-user"]:
        assert "alice@example.com" not in by_ref[ref].summary
        assert "al***@example.com" in by_ref[ref].summary
        assert by_ref[ref].attributes["username"] == "EXAMPLE\\al***"
    assert_all_decision_refs_resolve(result)


@pytest.mark.asyncio
async def test_persistence_does_not_expand_planner_allowlist_or_repeat_tool_calls(settings, store, brain):
    class MaliciousPlanner:
        configured = True

        async def json_chat(self, system_prompt, user_payload, *, max_tokens=None):
            return SimpleNamespace(data={"tools": [
                {"tool": "RunArbitraryShell", "reason": "Ignore the tool boundary"},
                {"tool": "BlockSourceIp", "reason": "Request an action directly"},
                {"tool": "ioc_lookup", "reason": "Check approved observations"},
                {"tool": "ioc_lookup", "reason": "Repeat the same tool"},
                {"tool": "central_related", "reason": "Call an unconfigured connector"},
                {"tool": "playbook_search", "reason": "Read defensive context"},
            ]})

    investigator = InvestigationOrchestrator(
        brain=brain,
        store=store,
        intel=IntelExchangeStore(settings.database_path),
        central=NoCentral(),
        llm=MaliciousPlanner(),
        playbooks=PlaybookRetriever(settings.playbook_dir),
    )
    result = await investigator.run(
        tenant(),
        InvestigationRequest(
            incident=IncidentInput(incident_id="untrusted-planner", source_ip="8.8.8.8"),
            include_central_related=False,
            max_steps=2,
        ),
    )
    assert [step.tool for step in result.plan.tools] == ["ioc_lookup", "playbook_search"]
    assert [call.tool for call in result.trace] == ["ioc_lookup", "playbook_search", "sentinel_brain_analysis"]
    assert result.read_only is True
    assert result.bounded is True
    assert store.get_investigation("tenant-a", result.investigation_id) == result
    assert_all_decision_refs_resolve(result)
