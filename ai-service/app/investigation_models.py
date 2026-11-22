from __future__ import annotations

from datetime import datetime
from typing import Literal

from pydantic import Field

from .intel_exchange import IntelReputation
from .models import AnalystDecision, ApiModel, EvidenceItem, IncidentInput


ToolName = Literal["ioc_lookup", "analysis_history", "central_related", "playbook_search"]


class InvestigationRequest(ApiModel):
    incident: IncidentInput
    include_central_related: bool = True
    max_steps: int = Field(default=4, ge=1, le=6)
    max_runtime_seconds: float = Field(default=20.0, ge=2.0, le=120.0)
    central_take: int = Field(default=200, ge=1, le=500)
    analysis_history_limit: int = Field(default=100, ge=1, le=500)
    max_related_results: int = Field(default=10, ge=1, le=30)


class PlannedTool(ApiModel):
    tool: ToolName
    reason: str = Field(default="", max_length=500)


class InvestigationPlan(ApiModel):
    source: Literal["llm", "deterministic"]
    tools: list[PlannedTool] = Field(default_factory=list)


class ToolCallTrace(ApiModel):
    step: int
    tool: str
    status: Literal["success", "skipped", "failed", "timeout"]
    reason: str = ""
    started_at_utc: datetime
    duration_ms: int = 0
    input_summary: str = ""
    output_summary: str = ""
    evidence_refs: list[str] = Field(default_factory=list)
    omitted_evidence_refs: list[str] = Field(default_factory=list)


class InvestigationTimelineEntry(ApiModel):
    """An observed evidence timestamp, never an inferred attack stage or event time."""

    timestamp_utc: datetime
    ref_id: str


class InvestigationResult(ApiModel):
    investigation_id: str
    tenant_id: str
    incident_id: str
    created_at_utc: datetime
    bounded: bool = True
    read_only: bool = True
    plan: InvestigationPlan
    trace: list[ToolCallTrace]
    indicators: list[dict[str, str]] = Field(default_factory=list)
    intel_reputation: list[IntelReputation] = Field(default_factory=list)
    related_incident_count: int = 0
    prior_analysis_count: int = 0
    stopped_reason: str
    decision: AnalystDecision
    evidence_catalog: list[EvidenceItem] = Field(default_factory=list)
    timeline: list[InvestigationTimelineEntry] = Field(default_factory=list)
    untimed_evidence_refs: list[str] = Field(default_factory=list)


class InvestigationSummary(ApiModel):
    investigation_id: str
    incident_id: str
    analysis_id: str
    created_at_utc: datetime
    classification: str
    risk_score: int = Field(ge=0, le=100)
    confidence: float = Field(ge=0.0, le=1.0)
    model: str
    deterministic_fallback: bool
    stopped_reason: str
    tool_call_count: int = Field(ge=0)
    evidence_count: int = Field(ge=0)


class InvestigationPage(ApiModel):
    items: list[InvestigationSummary] = Field(default_factory=list)
    next_cursor: str | None = None
