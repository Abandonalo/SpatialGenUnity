"""Multi-view refinement router.

This module owns the server-side logic for the 2D-based multi-view
refinement pipeline. The Unity client renders the same selection from
four canonical cameras (Front/Left/Right/Top), bundles per-view
RGB+depth+mask triplets, and POSTs them to /refine_multi_view. For
every view we build a ComfyUI graph from ``refinement.json`` with the
SAME seed, the same prompt, the same width/height and the same
KSampler settings, then run the graph and collect the refined image.

The view declared as ``reconstructionView`` (Front by default) is the
one whose refined image becomes the reference for the TripoSR
reconstruction; the other views run the refinement pass only and are
returned to Unity for visual verification / potential texture fusion
later.

Critical invariants enforced here:

* deterministic shared seed - all views see the same ``seed`` value.
* consistent resolution - every view payload is validated to share
  width/height with the rest of the request.
* never mutate the Unity cameras - the router never mutates inputs
  beyond what the inpaint needs, so repeated calls with the same
  request produce identical outputs.
"""

from __future__ import annotations

import random
from typing import Any, Dict, List, Optional

from .comfy_client import send_to_comfy
from .graph_injector import inject_params
from .graph_loader import load_graph
from .models import (
    MultiViewRefinementRequestModel,
    MultiViewRefinementResponseModel,
    RefinedViewResultModel,
    RunRequest,
    ViewPayload,
)


_REFINE_GRAPH_NAME = "refinement.json"


def handle_multi_view_refine(
    req: MultiViewRefinementRequestModel,
    *,
    tripo_model: str,
    geometry_resolution: int,
    tripo_threshold: float,
) -> MultiViewRefinementResponseModel:
    """Run the refinement graph for every view in ``req`` and collect results.

    Parameters
    ----------
    req:
        The parsed multi-view payload from Unity.
    tripo_model:
        TripoSR checkpoint name to use for any view that produces a mesh
        (the graph always runs TripoSR; we keep that behaviour and only
        surface the mesh from ``reconstructionView``).
    geometry_resolution / tripo_threshold:
        TripoSR sampler parameters.

    Returns
    -------
    MultiViewRefinementResponseModel with refined images for every view
    and the mesh extracted from ``reconstructionView``.
    """

    _validate_request(req)

    seed = _resolve_seed(req.seed)
    reconstruction_view = (req.reconstructionView or "Front").strip() or "Front"

    refined_views: List[RefinedViewResultModel] = []
    mesh_base64: Optional[str] = None

    for view in req.views:
        run_request = RunRequest(
            mode="refine",
            positive_prompt=_effective_prompt(req.positivePrompt),
            negative_prompt=req.negativePrompt or "",
            rgb_image=view.rgbBase64,
            depth_image=view.depthBase64,
            mask_image=view.maskBase64,
            seed=seed,
            steps=max(1, req.steps),
            cfg=max(0.0, req.cfg),
            denoise=max(0.85, min(1.0, req.denoise)),
            tripo_model=tripo_model,
            geometry_resolution=geometry_resolution,
            tripo_threshold=tripo_threshold,
        )

        graph = _build_graph(run_request)
        result = send_to_comfy(graph)

        refined_views.append(
            RefinedViewResultModel(
                viewType=view.viewType,
                refinedImageBase64=result.image_base64 or "",
            )
        )

        if _matches_view(view.viewType, reconstruction_view):
            mesh_base64 = result.mesh_base64 or mesh_base64

    # If the reconstruction view did not emit a mesh (unlikely but
    # possible when the graph fails from that specific camera), leave
    # meshBase64 empty; Unity falls back to the 2D preview on the
    # reconstructionView instead of the mesh swap.
    return MultiViewRefinementResponseModel(
        requestId=req.requestId,
        refinedViews=refined_views,
        meshBase64=mesh_base64 or "",
        success=True,
        errorMessage="",
    )


def _validate_request(req: MultiViewRefinementRequestModel) -> None:
    if not req.views:
        raise ValueError("multi-view refinement requires at least one view")

    # Width/height consistency: the inpainter requires same-sized inputs
    # for cross-view consistency (mask alignment + seed reproducibility).
    widths = {view.width for view in req.views if view.width > 0}
    heights = {view.height for view in req.views if view.height > 0}
    if len(widths) > 1 or len(heights) > 1:
        raise ValueError(
            "multi-view refinement requires every view to share the same resolution "
            f"(got widths={sorted(widths)}, heights={sorted(heights)})"
        )

    for view in req.views:
        missing = [
            name
            for name, value in (
                ("rgbBase64", view.rgbBase64),
                ("depthBase64", view.depthBase64),
                ("maskBase64", view.maskBase64),
            )
            if not value
        ]
        if missing:
            joined = ", ".join(missing)
            raise ValueError(f"view '{view.viewType}' is missing: {joined}")


def _resolve_seed(seed: int) -> int:
    if seed >= 0:
        return seed
    return random.randint(0, 2**31 - 1)


def _effective_prompt(prompt: str) -> str:
    prompt = (prompt or "").strip()
    if not prompt:
        raise ValueError("positivePrompt is required for multi-view refinement")
    return prompt


def _build_graph(run_request: RunRequest) -> Dict[str, Any]:
    graph = load_graph(_REFINE_GRAPH_NAME)
    return inject_params(graph, run_request)


def _matches_view(view_type: str, reconstruction_view: str) -> bool:
    return (view_type or "").strip().lower() == (reconstruction_view or "").strip().lower()


__all__ = [
    "handle_multi_view_refine",
]
