"""Multi-view refinement router.

The Unity client renders the same selection from four canonical cameras
(Front/Left/Right/Top) and POSTs a bundle of RGB+depth+mask triplets
here. We fuse those four views into a single 2D image and lift THAT
single image into 3D via TripoSR - this is the architectural invariant
the caller cares about: "four multi-view images of the original geometry
are first used to generate a new 2D image, then lifted to be the refined
3D geometry" (rather than running TripoSR independently on every view).

Fusion strategy
---------------
The views are tiled into a 2x2 grid for RGB, depth and mask. The inpaint
graph runs once on these composite images, so Stable Diffusion /
ControlNet attend jointly across all four views (deterministic shared
seed). Before the RemBG/TripoSR stage the graph crops one quadrant of the
refined 2x2 composite (top-right for the current layout) so the mesh
reconstruction is driven by a single-view image - TripoSR expects a
coherent reference image, not a grid.

This gives us:

* one inpaint pass (all four views participate via shared attention),
* one refined 2D image (the reconstruction-view quadrant of the
  composite), and
* one TripoSR mesh per request, with the multi-view inputs actually
  influencing that mesh instead of each view producing an independent
  (and conflicting) .glb as the previous implementation did.
"""

from __future__ import annotations

import base64
import io
import random
from typing import Any, Dict, Iterable, List, Optional, Tuple

from PIL import Image

from .comfy_client import send_to_comfy
from .graph_injector import inject_params
from .graph_loader import load_graph
from .models import (
    MultiViewRefinementRequestModel,
    MultiViewRefinementResponseModel,
    RefinedViewResultModel,
    RunRequest,
)


_REFINE_GRAPH_NAME = "refinement.json"
_CANONICAL_VIEWS = ("Front", "Left", "Right", "Top")


def handle_multi_view_refine(
    req: MultiViewRefinementRequestModel,
    *,
    tripo_model: str,
    geometry_resolution: int,
    tripo_threshold: float,
) -> MultiViewRefinementResponseModel:
    """Fuse the four Unity views into a 2x2 composite and run the refine
    graph ONCE. Returns the composite's reconstruction-view quadrant as
    the refined image and the single TripoSR mesh produced from it.
    """

    _validate_request(req)

    seed = _resolve_seed(req.seed)
    reconstruction_view = _canonicalize_view(req.reconstructionView)

    positions = _build_quadrant_map(reconstruction_view)
    per_view_w = req.views[0].width or 512
    per_view_h = req.views[0].height or 512

    rgb_composite = _composite_views(req.views, positions, "rgbBase64", (per_view_w, per_view_h), fill=(0, 0, 0, 255))
    depth_composite = _composite_views(req.views, positions, "depthBase64", (per_view_w, per_view_h), fill=(0, 0, 0, 255))
    mask_composite = _composite_views(req.views, positions, "maskBase64", (per_view_w, per_view_h), fill=(0, 0, 0, 255))

    # Crop the top-right quadrant: with the default quadrant map, Unity's
    # leftCamera (user-facing "front" for common house orientations) lands
    # in that cell; TripoSR then lifts that view.
    crop_x = per_view_w
    crop_y = 0
    run_request = RunRequest(
        mode="refine",
        positive_prompt=_effective_prompt(req.positivePrompt),
        negative_prompt=req.negativePrompt or "",
        rgb_image=rgb_composite,
        depth_image=depth_composite,
        mask_image=mask_composite,
        # Uses the same fused composite until the client sends explicit edge maps per view.
        canny_image=rgb_composite,
        seed=seed,
        steps=max(1, req.steps),
        cfg=max(0.0, req.cfg),
        denoise=max(0.25, min(0.6, req.denoise)),
        tripo_model=tripo_model,
        geometry_resolution=geometry_resolution,
        tripo_threshold=tripo_threshold,
        crop_width=per_view_w,
        crop_height=per_view_h,
        crop_x=crop_x,
        crop_y=crop_y,
    )

    graph = _build_graph(run_request)
    result = send_to_comfy(graph)

    return MultiViewRefinementResponseModel(
        requestId=req.requestId,
        # We return a single refined view: the reconstruction quadrant that
        # the graph cropped and actually fed into TripoSR. The client uses
        # this as a 2D preview when the mesh path fails; the other quadrants
        # are not surfaced separately because they exist only as the fusion
        # context for this single refined image.
        refinedViews=[
            RefinedViewResultModel(
                viewType=reconstruction_view,
                refinedImageBase64=result.image_base64 or "",
            )
        ],
        meshBase64=result.mesh_base64 or "",
        success=True,
        errorMessage="",
    )


def _validate_request(req: MultiViewRefinementRequestModel) -> None:
    if not req.views:
        raise ValueError("multi-view refinement requires at least one view")

    widths = {view.width for view in req.views if view.width > 0}
    heights = {view.height for view in req.views if view.height > 0}
    if len(widths) > 1 or len(heights) > 1:
        raise ValueError(
            "multi-view refinement requires every view to share the same resolution "
            f"(got widths={sorted(widths)}, heights={sorted(heights)})"
        )

    required = _canonicalize_view(req.reconstructionView)
    if not any(_canonicalize_view(v.viewType) == required for v in req.views):
        raise ValueError(f"reconstruction view '{required}' is missing from the request payload")

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


def _canonicalize_view(view: str) -> str:
    key = (view or "").strip().lower()
    for canonical in _CANONICAL_VIEWS:
        if canonical.lower() == key:
            return canonical
    return "Front"


def _build_quadrant_map(reconstruction_view: str) -> Dict[str, Tuple[int, int]]:
    """Place the reconstruction view in the top-left (0, 0) cell so the
    composite layout stays consistent with the canonical view order. The
    graph crops a different quadrant (top-right) at runtime because the
    user's geometry's actual front facade is captured by Unity's
    leftCamera, which lands at (1, 0) in this map - see the crop_x setting
    in handle_multi_view_refine."""
    rv = _canonicalize_view(reconstruction_view)
    ordered = [rv] + [v for v in _CANONICAL_VIEWS if v != rv]
    cells = [(0, 0), (1, 0), (0, 1), (1, 1)]
    return dict(zip(ordered, cells))


def _composite_views(
    views: Iterable[Any],
    positions: Dict[str, Tuple[int, int]],
    field: str,
    cell_size: Tuple[int, int],
    *,
    fill: Tuple[int, int, int, int] = (0, 0, 0, 255),
) -> str:
    """Tile the per-view images named by ``field`` into a 2x2 grid.
    Returns a base64-encoded PNG of the composite. Missing views leave
    their quadrant as ``fill`` so the graph still has geometrically
    consistent input (we never silently drop quadrants without telling
    the diffusion where they should have been)."""

    cell_w, cell_h = cell_size
    canvas = Image.new("RGBA", (cell_w * 2, cell_h * 2), fill)

    by_view: Dict[str, Image.Image] = {}
    for view in views:
        data = getattr(view, field, None) or ""
        if not data:
            continue
        canonical = _canonicalize_view(view.viewType)
        img = _decode_base64_png(data).convert("RGBA")
        if img.size != (cell_w, cell_h):
            img = img.resize((cell_w, cell_h), Image.NEAREST)
        by_view[canonical] = img

    for view_type, (col, row) in positions.items():
        img = by_view.get(view_type)
        if img is None:
            continue
        canvas.paste(img, (col * cell_w, row * cell_h))

    buffer = io.BytesIO()
    canvas.save(buffer, format="PNG")
    return base64.b64encode(buffer.getvalue()).decode("ascii")


def _decode_base64_png(data: str) -> Image.Image:
    raw = data.split(",", 1)[-1]
    return Image.open(io.BytesIO(base64.b64decode(raw)))


def _build_graph(run_request: RunRequest) -> Dict[str, Any]:
    graph = load_graph(_REFINE_GRAPH_NAME)
    return inject_params(graph, run_request)


__all__ = [
    "handle_multi_view_refine",
]
