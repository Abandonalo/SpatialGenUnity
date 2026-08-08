"""Region-scoped refinement.

Unity renders the selected region from four canonical cameras and posts RGB + depth +
edges + mask for each. The reconstruction view is inpainted, cropped to the selection's
own footprint, and lifted back to 3D; Unity then splices that mesh in place of the
region's original geometry.

Why only the reconstruction view is inpainted
---------------------------------------------
The lifter in use (TripoSR) reconstructs from a single image, so inpainting the other
three views would cost three extra diffusion passes whose output nothing consumes. They
are still captured and stored as artifacts, and the request carries them, so swapping in
a multi-view lifter is a change here rather than a change to the wire format.

Why the crop matters
--------------------
The cameras deliberately frame more than the selection, so the inpaint has surrounding
geometry to blend against. Lifting that whole frame would produce a mesh covering the
neighbours too, which is exactly the non-locality the region workflow exists to avoid.
Cropping to the selection footprint keeps the reconstruction scoped to what was edited.
"""

from __future__ import annotations

import base64
import io
import random
from typing import Optional, Tuple

from PIL import Image

from .comfy_client import send_to_comfy
from .graph_injector import inject_params
from .graph_loader import load_graph
from .models import (
    REFINEMENT_DEFAULT_NEGATIVE,
    MultiViewRefinementRequest,
    MultiViewRefinementResponse,
    RefinedViewResult,
    RunRequest,
    ViewPayload,
)

_INPAINT_GRAPH = "refinement_inpaint_only.json"
_LIFT_GRAPH = "refinement_tripo_from_rgb.json"
_CANONICAL_VIEWS = ("Front", "Left", "Right", "Top")


def handle_refine(
    req: MultiViewRefinementRequest,
    *,
    tripo_model: str,
    geometry_resolution: int,
    tripo_threshold: float,
) -> MultiViewRefinementResponse:
    _validate(req)

    view = _reconstruction_view(req)
    seed = req.seed if req.seed >= 0 else random.randint(0, 2**31 - 1)
    positive = req.positivePrompt.strip()
    negative = (req.negativePrompt or "").strip() or REFINEMENT_DEFAULT_NEGATIVE

    inpaint = RunRequest(
        mode="refine_inpaint_only",
        positive_prompt=positive,
        negative_prompt=negative,
        rgb_image=view.rgbBase64,
        depth_image=view.depthBase64,
        mask_image=view.maskBase64,
        canny_image=view.edgesBase64 or view.rgbBase64,
        seed=seed,
        steps=req.steps,
        cfg=req.cfg,
        denoise=min(1.0, max(0.0, req.denoise)),
        tripo_model=tripo_model,
        geometry_resolution=geometry_resolution,
        tripo_threshold=tripo_threshold,
    )
    refined_image = send_to_comfy(inject_params(load_graph(_INPAINT_GRAPH), inpaint)).image_base64
    if not refined_image:
        raise RuntimeError("The inpaint pass produced no image")

    crop = _crop_rect(req, refined_image, view)

    lift = RunRequest(
        mode="tripo_from_rgb",
        positive_prompt=positive,
        negative_prompt=negative,
        rgb_image=refined_image,
        seed=seed,
        steps=1,
        cfg=1.0,
        denoise=1.0,
        tripo_model=tripo_model,
        geometry_resolution=geometry_resolution,
        tripo_threshold=tripo_threshold,
        crop_width=crop[2],
        crop_height=crop[3],
        crop_x=crop[0],
        crop_y=crop[1],
    )
    mesh = send_to_comfy(inject_params(load_graph(_LIFT_GRAPH), lift)).mesh_base64
    if not mesh:
        raise RuntimeError("The reconstruction pass produced no mesh")

    return MultiViewRefinementResponse(
        requestId=req.requestId,
        refinedViews=[RefinedViewResult(viewType=view.viewType, refinedImageBase64=refined_image)],
        meshBase64=mesh,
        success=True,
        status="success",
    )


def _validate(req: MultiViewRefinementRequest) -> None:
    if not req.views:
        raise ValueError("Refinement requires at least one view")
    if not req.positivePrompt.strip():
        raise ValueError("positivePrompt is required")

    sizes = {(view.width, view.height) for view in req.views if view.width and view.height}
    if len(sizes) > 1:
        raise ValueError(f"Every view must share one resolution, got {sorted(sizes)}")

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
            raise ValueError(f"View '{view.viewType}' is missing: {', '.join(missing)}")


def _reconstruction_view(req: MultiViewRefinementRequest) -> ViewPayload:
    wanted = _canonicalize(req.reconstructionView)
    for view in req.views:
        if _canonicalize(view.viewType) == wanted:
            return view
    raise ValueError(f"Reconstruction view '{wanted}' is missing from the request")


def _canonicalize(view: str) -> str:
    key = (view or "").strip().lower()
    for canonical in _CANONICAL_VIEWS:
        if canonical.lower() == key:
            return canonical
    return "Front"


def _crop_rect(
    req: MultiViewRefinementRequest, image_base64: str, view: ViewPayload
) -> Tuple[int, int, int, int]:
    """Selection footprint in pixels: ``(x, y, width, height)``.

    Unity sends viewport UV with the origin at the bottom-left; images are indexed from
    the top-left, so the vertical axis is flipped here.
    """
    size = _image_size(image_base64) or (view.width or 512, view.height or 512)
    width, height = size

    min_x = _clamp01(req.cropMinX)
    max_x = _clamp01(req.cropMaxX)
    min_y = _clamp01(req.cropMinY)
    max_y = _clamp01(req.cropMaxY)

    if max_x <= min_x or max_y <= min_y:
        return 0, 0, width, height

    x = int(round(min_x * width))
    y = int(round((1.0 - max_y) * height))
    crop_width = max(8, int(round((max_x - min_x) * width)))
    crop_height = max(8, int(round((max_y - min_y) * height)))

    # Keep the rect inside the image after rounding.
    x = min(x, max(0, width - crop_width))
    y = min(y, max(0, height - crop_height))
    return x, y, crop_width, crop_height


def _clamp01(value: float) -> float:
    return min(1.0, max(0.0, float(value)))


def _image_size(encoded: str) -> Optional[Tuple[int, int]]:
    try:
        raw = base64.b64decode(encoded.split(",", 1)[-1])
        with Image.open(io.BytesIO(raw)) as image:
            return image.size
    except Exception:  # noqa: BLE001 - falls back to the declared view size
        return None


__all__ = ["handle_refine"]
