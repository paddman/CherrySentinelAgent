from __future__ import annotations

import logging

from fastapi import APIRouter, Depends, HTTPException, Request

from .config import get_settings
from .models import AnalystDecision, IncidentInput
from .product_profile import get_product_profile
from .security import TenantContext, require_tenant

logger = logging.getLogger("nt.shield")
settings = get_settings()
profile = get_product_profile(settings, "nt_shield")

router = APIRouter(prefix="/nt-shield/v1", tags=["NT Shield"])


@router.get("/status")
async def nt_shield_status(
    request: Request,
    tenant: TenantContext = Depends(require_tenant),
) -> dict:
    return {
        "product": profile.to_dict(include_services=False),
        "tenant": {
            "tenant_id": tenant.tenant_id,
            "tenant_name": tenant.name,
            "central_configured": bool(tenant.central_url),
        },
        "model": await request.app.state.llm.health(),
        "analysis_mode": settings.analysis_mode,
        "guardrails": {
            "evidence_reference_validation": True,
            "response_action_allowlist": True,
            "human_approval_for_destructive_actions": True,
            "bounded_read_only_investigation": True,
            "tenant_scoped_storage": True,
            "audit_and_usage_record": True,
        },
    }


@router.get("/catalog")
async def nt_shield_catalog(
    tenant: TenantContext = Depends(require_tenant),
) -> dict:
    del tenant
    return {
        "product": profile.market_name,
        "tagline": profile.tagline,
        "packages": [item.to_dict() for item in profile.services],
        "billing_units": [
            "base subscription",
            "protected app or active sensor",
            "telemetry volume",
            "AI call, token or GPU-second",
            "retention GB-day",
            "approved analyst minute",
        ],
        "note": (
            "Package status is an implementation boundary, not a commercial SLA. "
            "Prototype and partial items still require the listed pilot validation."
        ),
    }


@router.get("/readiness")
async def nt_shield_readiness(
    tenant: TenantContext = Depends(require_tenant),
) -> dict:
    del tenant
    return {
        "product": profile.market_name,
        "implemented": list(profile.implemented),
        "pilot_validation_required": list(profile.pilot_validation_required),
        "safety_boundary": profile.safety_boundary,
    }


@router.post("/incidents/analyze", response_model=AnalystDecision)
async def analyze_nt_shield_incident(
    incident: IncidentInput,
    request: Request,
    tenant: TenantContext = Depends(require_tenant),
) -> AnalystDecision:
    """Branded alias over the existing Sentinel Brain analysis pipeline."""

    try:
        decision = await request.app.state.brain.analyze(tenant.tenant_id, incident)
        request.app.state.store.append_audit(
            tenant.tenant_id,
            "nt_shield.incident.analyze",
            incident.incident_id,
            "success",
            {
                "analysis_id": decision.analysis_id,
                "risk_score": decision.risk_score,
                "profile": profile.key,
            },
        )
        return decision
    except Exception as exc:
        request.app.state.store.append_audit(
            tenant.tenant_id,
            "nt_shield.incident.analyze",
            incident.incident_id,
            "fail",
            {"error": str(exc)[:500], "profile": profile.key},
        )
        logger.exception("NT Shield incident analysis failed incident=%s", incident.incident_id)
        raise HTTPException(status_code=500, detail="analysis_failed") from exc
