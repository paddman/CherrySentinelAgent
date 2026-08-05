from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Literal

from .config import Settings

ProductProfileKey = Literal["cherry", "nt_shield"]


@dataclass(frozen=True, slots=True)
class ServicePackage:
    package_id: str
    name: str
    status: Literal["prototype", "partial", "pilot_required"]
    capabilities: tuple[str, ...]
    validation_required: tuple[str, ...] = ()

    def to_dict(self) -> dict[str, Any]:
        return {
            "package_id": self.package_id,
            "name": self.name,
            "status": self.status,
            "capabilities": list(self.capabilities),
            "validation_required": list(self.validation_required),
        }


@dataclass(frozen=True, slots=True)
class ProductProfile:
    key: ProductProfileKey
    product_name: str
    market_name: str
    tagline: str
    platform_core: str
    api_prefix: str
    default_posture: str
    services: tuple[ServicePackage, ...]
    implemented: tuple[str, ...]
    pilot_validation_required: tuple[str, ...]
    safety_boundary: str

    def to_dict(self, *, include_services: bool = True) -> dict[str, Any]:
        payload: dict[str, Any] = {
            "key": self.key,
            "product_name": self.product_name,
            "market_name": self.market_name,
            "tagline": self.tagline,
            "platform_core": self.platform_core,
            "api_prefix": self.api_prefix,
            "default_posture": self.default_posture,
            "implemented": list(self.implemented),
            "pilot_validation_required": list(self.pilot_validation_required),
            "safety_boundary": self.safety_boundary,
        }
        if include_services:
            payload["services"] = [item.to_dict() for item in self.services]
        return payload


def get_product_profile(
    settings: Settings,
    key: ProductProfileKey | None = None,
) -> ProductProfile:
    selected = key or settings.product_profile
    if selected == "nt_shield":
        product_name = (
            settings.app_name
            if settings.app_name != "Cherry Sentinel Brain"
            else "NT Shield AI Incident Commander"
        )
        return ProductProfile(
            key="nt_shield",
            product_name=product_name,
            market_name="NT Shield",
            tagline="Sovereign AI Incident Commander as a Service",
            platform_core="Cherry Sentinel Agent, Central and Sentinel Brain",
            api_prefix="/nt-shield/v1",
            default_posture="detect-only / shadow mode",
            services=(
                ServicePackage(
                    package_id="api",
                    name="NT Shield API",
                    status="prototype",
                    capabilities=(
                        "incident analysis",
                        "cross-layer correlation",
                        "Thai evidence-grounded explanation",
                        "response recommendation",
                        "tenant usage metering",
                    ),
                    validation_required=("quota enforcement", "billing reconciliation"),
                ),
                ServicePackage(
                    package_id="monitor",
                    name="NT Shield Monitor",
                    status="prototype",
                    capabilities=(
                        "Windows and Linux agent telemetry",
                        "detect-only alerts",
                        "fleet health and metrics",
                        "monthly AI summary",
                    ),
                    validation_required=("retention policy", "pilot capacity baseline"),
                ),
                ServicePackage(
                    package_id="web",
                    name="NT Shield Web",
                    status="partial",
                    capabilities=(
                        "web and API evidence ingestion",
                        "Suricata and Zeek context",
                        "AI-WAF risk explanation",
                        "shadow-mode recommendation",
                    ),
                    validation_required=(
                        "managed WAF connector",
                        "replay and benign false-block gate",
                    ),
                ),
                ServicePackage(
                    package_id="xdr",
                    name="NT Shield XDR",
                    status="prototype",
                    capabilities=(
                        "endpoint, network, identity and exposure correlation",
                        "threat graph",
                        "bounded read-only investigation",
                        "approval-gated response proposal",
                    ),
                    validation_required=("PostgreSQL RLS", "load and failover tests"),
                ),
                ServicePackage(
                    package_id="mdr",
                    name="NT Shield MDR",
                    status="pilot_required",
                    capabilities=(
                        "analyst escalation workflow",
                        "threat hunting plan",
                        "compliance and executive reporting",
                    ),
                    validation_required=(
                        "24x7 operating model",
                        "commercial SLA",
                        "analyst workload and cost-to-serve",
                    ),
                ),
            ),
            implemented=(
                "multi-tenant API key authentication",
                "local Qwen through an OpenAI-compatible endpoint",
                "deterministic plus ML plus evidence-grounded AI analysis",
                "exact evidence reference validation",
                "bounded read-only investigation",
                "human approval guard for destructive actions",
                "audit, feedback and usage metering",
                "Cherry Central incident bridge",
            ),
            pilot_validation_required=(
                "tenant isolation backed by PostgreSQL row-level security",
                "OIDC, MFA and production RBAC",
                "distributed quota and rate limiting",
                "immutable usage reconciliation and billing integration",
                "HA, DR, autoscaling and noisy-neighbor tests",
                "PDPA, legal, retention and response-liability approval",
                "customer pilot metrics and willingness-to-pay evidence",
            ),
            safety_boundary=(
                "AI may analyze, explain and recommend. Blocking, isolation, account, "
                "process, service and policy-promotion actions remain behind Cherry "
                "Central policy checks and an authenticated human approver."
            ),
        )

    return ProductProfile(
        key="cherry",
        product_name=settings.app_name,
        market_name="Cherry Sentinel",
        tagline="Evidence-grounded sovereign AI-XDR",
        platform_core="Cherry Sentinel Agent, Central and Sentinel Brain",
        api_prefix="/v1",
        default_posture="detect-only",
        services=(),
        implemented=(
            "endpoint and network telemetry",
            "cross-host correlation",
            "evidence-grounded AI analysis",
            "approval-gated response queue",
        ),
        pilot_validation_required=(
            "production tenant isolation",
            "capacity, resilience and commercial service validation",
        ),
        safety_boundary=(
            "Sentinel Brain is read-only and declarative; Cherry Central remains the "
            "response execution and approval boundary."
        ),
    )
