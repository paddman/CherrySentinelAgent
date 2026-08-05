from __future__ import annotations

import pytest
from fastapi import HTTPException

from app.config import Settings
from app.product_profile import get_product_profile
from app.security import resolve_tenant_credentials


def test_nt_shield_profile_exposes_honest_service_boundaries(tmp_path):
    settings = Settings(
        environment="test",
        data_dir=tmp_path,
        database_path=tmp_path / "nt-shield.db",
        product_profile="nt_shield",
        app_name="NT Shield AI Incident Commander",
        llm_enabled=False,
    )

    profile = get_product_profile(settings)
    packages = {item.package_id: item for item in profile.services}

    assert profile.key == "nt_shield"
    assert profile.market_name == "NT Shield"
    assert profile.api_prefix == "/nt-shield/v1"
    assert packages["api"].status == "prototype"
    assert packages["web"].status == "partial"
    assert packages["mdr"].status == "pilot_required"
    assert "commercial SLA" in packages["mdr"].validation_required
    assert "human approver" in profile.safety_boundary


def test_nt_shield_routes_are_mounted_in_extended_application():
    from app.main_v2 import app

    paths = set(app.openapi()["paths"])
    assert "/nt-shield/v1/status" in paths
    assert "/nt-shield/v1/catalog" in paths
    assert "/nt-shield/v1/readiness" in paths
    assert "/nt-shield/v1/incidents/analyze" in paths


def test_tenant_header_aliases_are_backward_compatible():
    assert resolve_tenant_credentials("tenant-a", "key-a", None, None) == (
        "tenant-a",
        "key-a",
    )
    assert resolve_tenant_credentials(None, None, "tenant-a", "key-a") == (
        "tenant-a",
        "key-a",
    )
    assert resolve_tenant_credentials("tenant-a", "key-a", "tenant-a", "key-a") == (
        "tenant-a",
        "key-a",
    )


def test_tenant_header_aliases_reject_ambiguous_credentials():
    with pytest.raises(HTTPException) as tenant_error:
        resolve_tenant_credentials("tenant-a", "key-a", "tenant-b", "key-a")
    assert tenant_error.value.status_code == 400
    assert tenant_error.value.detail == "conflicting_tenant_headers"

    with pytest.raises(HTTPException) as key_error:
        resolve_tenant_credentials("tenant-a", "key-a", "tenant-a", "key-b")
    assert key_error.value.status_code == 400
    assert key_error.value.detail == "conflicting_api_key_headers"

    with pytest.raises(HTTPException) as missing_error:
        resolve_tenant_credentials(None, None, None, None)
    assert missing_error.value.status_code == 401
    assert missing_error.value.detail == "tenant_credentials_required"
