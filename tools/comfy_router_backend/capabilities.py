"""Detect refinement lifters exposed by the connected ComfyUI instance."""

from __future__ import annotations

from .comfy_client import get_object_info
from .models import RefinementCapability


HY3D_NODE = "SpatialGenHunyuan2MV"


def refinement_capabilities() -> list[RefinementCapability]:
    try:
        nodes = get_object_info()
    except Exception as exc:  # noqa: BLE001 - health reports the exact upstream reason
        detail = f"ComfyUI node registry is unavailable: {exc}"
        return [
            RefinementCapability(id="hunyuan3d_2mv", available=False, detail=detail),
            RefinementCapability(id="tripo_sr", available=False, detail=detail),
        ]

    hy3d = nodes.get(HY3D_NODE)
    hy3d_available = isinstance(hy3d, dict) and _has_model_choice(hy3d)
    if not isinstance(hy3d, dict):
        hy3d_detail = "Install the SpatialGen node with ./tools/setup_hunyuan2mv.sh, then restart ComfyUI."
    elif not hy3d_available:
        hy3d_detail = "The SpatialGen Hunyuan node is loaded but its pinned model is missing. Run ./tools/setup_hunyuan2mv.sh."
    else:
        hy3d_detail = ""

    tripo_available = "TripoSRSampler" in nodes and "TripoSRModelLoader" in nodes
    return [
        RefinementCapability(id="hunyuan3d_2mv", available=hy3d_available, detail=hy3d_detail),
        RefinementCapability(
            id="tripo_sr",
            available=tripo_available,
            detail="" if tripo_available else "ComfyUI-Flowty-TripoSR is not loaded.",
        ),
    ]


def lifter_available(lifter_id: str) -> tuple[bool, str]:
    for capability in refinement_capabilities():
        if capability.id == lifter_id:
            return capability.available, capability.detail
    return False, f"Unknown refinement lifter: {lifter_id}"


def _has_model_choice(node: dict) -> bool:
    required = ((node.get("input") or {}).get("required") or {})
    spec = required.get("model")
    if not isinstance(spec, (list, tuple)) or not spec:
        return False
    choices = spec[0]
    if not isinstance(choices, (list, tuple)):
        return False
    return any(isinstance(choice, str) and not choice.startswith("<") for choice in choices)


__all__ = ["lifter_available", "refinement_capabilities"]
