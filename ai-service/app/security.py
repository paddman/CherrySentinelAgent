from __future__ import annotations

import hashlib
import hmac
import json
from dataclasses import dataclass
from typing import Any

from fastapi import Header, HTTPException, Request, status
from pydantic import BaseModel, ConfigDict, Field, ValidationError

from .config import Settings


class TenantConfig(BaseModel):
    model_config = ConfigDict(extra="ignore")

    tenant_id: str = Field(min_length=1, max_length=120)
    name: str = Field(default="", max_length=300)
    api_key: str | None = None
    api_key_sha256: str | None = None
    central_url: str | None = None
    central_api_key: str | None = None
    central_verify_tls: bool = True
    enabled: bool = True
    metadata: dict[str, Any] = Field(default_factory=dict)


@dataclass(frozen=True, slots=True)
class TenantContext:
    tenant_id: str
    name: str
    central_url: str | None
    central_api_key: str | None
    central_verify_tls: bool
    metadata: dict[str, Any]


class TenantRegistry:
    def __init__(self, settings: Settings) -> None:
        self._settings = settings
        self._tenants = self._load(settings)

    @staticmethod
    def _load(settings: Settings) -> dict[str, TenantConfig]:
        try:
            raw = json.loads(settings.tenants_json or "[]")
        except json.JSONDecodeError as exc:
            raise RuntimeError("CHERRY_TENANTS_JSON is not valid JSON") from exc

        if isinstance(raw, dict):
            raw = raw.get("tenants", [])
        if not isinstance(raw, list):
            raise RuntimeError("CHERRY_TENANTS_JSON must be a JSON list")

        configs: list[TenantConfig] = []
        for item in raw:
            try:
                configs.append(TenantConfig.model_validate(item))
            except ValidationError as exc:
                raise RuntimeError(f"Invalid tenant configuration: {exc}") from exc

        if not configs and settings.allow_dev_tenant and settings.environment != "production":
            configs.append(
                TenantConfig(
                    tenant_id=settings.dev_tenant_id,
                    name="Cherry Sentinel Demo",
                    api_key=settings.dev_api_key,
                    central_verify_tls=False,
                )
            )

        return {cfg.tenant_id: cfg for cfg in configs if cfg.enabled}

    @property
    def tenant_ids(self) -> list[str]:
        return sorted(self._tenants)

    def authenticate(self, tenant_id: str, api_key: str) -> TenantContext:
        cfg = self._tenants.get(tenant_id)
        if cfg is None or not cfg.enabled:
            raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="unknown_tenant")

        supplied = api_key.encode("utf-8", errors="ignore")
        valid = False
        if cfg.api_key is not None:
            valid = hmac.compare_digest(supplied, cfg.api_key.encode("utf-8"))
        elif cfg.api_key_sha256 is not None:
            digest = hashlib.sha256(supplied).hexdigest()
            valid = hmac.compare_digest(digest, cfg.api_key_sha256.lower())

        if not valid:
            raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="invalid_api_key")

        return TenantContext(
            tenant_id=cfg.tenant_id,
            name=cfg.name or cfg.tenant_id,
            central_url=cfg.central_url.rstrip("/") if cfg.central_url else None,
            central_api_key=cfg.central_api_key,
            central_verify_tls=cfg.central_verify_tls,
            metadata=dict(cfg.metadata),
        )


def _clean_header(value: str | None) -> str | None:
    if value is None:
        return None
    cleaned = value.strip()
    return cleaned or None


def resolve_tenant_credentials(
    cherry_tenant: str | None,
    cherry_api_key: str | None,
    nt_shield_tenant: str | None,
    nt_shield_api_key: str | None,
) -> tuple[str, str]:
    """Resolve legacy Cherry headers and the NT Shield branded aliases safely.

    Supplying both header families is allowed only when their values agree. This
    prevents a reverse proxy, SDK or browser from accidentally authenticating one
    tenant while displaying another tenant in logs or user interfaces.
    """

    cherry_tenant = _clean_header(cherry_tenant)
    cherry_api_key = _clean_header(cherry_api_key)
    nt_shield_tenant = _clean_header(nt_shield_tenant)
    nt_shield_api_key = _clean_header(nt_shield_api_key)

    if cherry_tenant and nt_shield_tenant and not hmac.compare_digest(
        cherry_tenant, nt_shield_tenant
    ):
        raise HTTPException(status_code=400, detail="conflicting_tenant_headers")

    if cherry_api_key and nt_shield_api_key and not hmac.compare_digest(
        cherry_api_key, nt_shield_api_key
    ):
        raise HTTPException(status_code=400, detail="conflicting_api_key_headers")

    tenant_id = nt_shield_tenant or cherry_tenant
    api_key = nt_shield_api_key or cherry_api_key
    if not tenant_id or not api_key:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="tenant_credentials_required",
        )

    return tenant_id, api_key


async def require_tenant(
    request: Request,
    x_cherry_tenant: str | None = Header(default=None, alias="X-Cherry-Tenant"),
    x_cherry_api_key: str | None = Header(default=None, alias="X-Cherry-Api-Key"),
    x_nt_shield_tenant: str | None = Header(default=None, alias="X-NT-Shield-Tenant"),
    x_nt_shield_api_key: str | None = Header(default=None, alias="X-NT-Shield-Api-Key"),
) -> TenantContext:
    registry: TenantRegistry = request.app.state.tenant_registry
    tenant_id, api_key = resolve_tenant_credentials(
        x_cherry_tenant,
        x_cherry_api_key,
        x_nt_shield_tenant,
        x_nt_shield_api_key,
    )
    return registry.authenticate(tenant_id, api_key)
