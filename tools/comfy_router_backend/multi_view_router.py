"""Multi-view refinement router.

The Unity client renders the same selection from four canonical cameras
(Front/Left/Right/Top) and POSTs a bundle of RGB+depth+mask triplets
here. We fuse those four views into a single 2D layout for consistent
quadrant placement, then **run SD inpainting once per view at native
resolution** (four Comfy passes). Each pass gives the model a full
512×512 latent instead of a 256×256 cell inside a fused 1024² grid — this
recovers better color/saturation inside tight masks (“white roof” vs
neutral gray plateau seen in fused single-pass logs). The four refined
tiles are recomposed back into the same 2×2 grid, cropped to the
reconstruction quadrant, and lifted via **one TripoSR** graph on that
crop (tripo-from-RGB subgraph).

Architectural invariant
-----------------------
Four multi-view images still inform one refined mesh: every tile’s
inpaint shares the fused prompt strategy and deterministic seed offsets,
Tripo consumes the reconstruction quadrant of the recomposed RGB only.
"""

from __future__ import annotations

import base64
import io
import random
from typing import Any, Dict, Iterable, Tuple

from PIL import Image

from .comfy_client import send_to_comfy
from .graph_injector import inject_params
from .graph_loader import load_graph
from .models import (
    REFINEMENT_DEFAULT_NEGATIVE,
    MultiViewRefinementRequestModel,
    MultiViewRefinementResponseModel,
    RefinedViewResultModel,
    RunRequest,
)


_INPAINT_TILE_GRAPH_NAME = "refinement_inpaint_only.json"
_TRIPO_FROM_RGB_GRAPH_NAME = "refinement_tripo_from_rgb.json"
_CANONICAL_VIEWS = ("Front", "Left", "Right", "Top")


def _quadrant_png_b64(
    composite_b64: str,
    positions: Dict[str, Tuple[int, int]],
    view_type: str,
    cell_w: int,
    cell_h: int,
) -> str:
    """Extract one quadrant’s RGB from the fused composite as a standalone PNG."""

    image = _decode_base64_png(composite_b64).convert("RGBA")
    col, row = positions[view_type]
    left, top_px = col * cell_w, row * cell_h
    crop = image.crop((left, top_px, left + cell_w, top_px + cell_h))
    buffer = io.BytesIO()
    crop.save(buffer, format="PNG")
    return base64.b64encode(buffer.getvalue()).decode("ascii")


def _composite_rgb_tiles_from_map(
    tile_b64_by_view: Dict[str, str],
    positions: Dict[str, Tuple[int, int]],
    cell_w: int,
    cell_h: int,
    *,
    fill: Tuple[int, int, int, int] = (0, 0, 0, 255),
) -> str:
    """Paste per-view PNGs into the same quadrant layout Unity uses."""

    canvas = Image.new("RGBA", (cell_w * 2, cell_h * 2), fill)
    for view_type, (col, row) in positions.items():
        payload = tile_b64_by_view.get(view_type)
        if not payload:
            continue
        tile = _decode_base64_png(payload).convert("RGBA")
        if tile.size != (cell_w, cell_h):
            tile = tile.resize((cell_w, cell_h), Image.NEAREST)
        canvas.paste(tile, (col * cell_w, row * cell_h))
    buffer = io.BytesIO()
    canvas.save(buffer, format="PNG")
    return base64.b64encode(buffer.getvalue()).decode("ascii")


def handle_multi_view_refine(
    req: MultiViewRefinementRequestModel,
    *,
    tripo_model: str,
    geometry_resolution: int,
    tripo_threshold: float,
) -> MultiViewRefinementResponseModel:
    """Tile canonical views into a 2×2 layout, inpaint **each quadrant** separately,
    recompose RGB, crop the reconstruction quadrant into TripoSR (tripo-from-RGB graph).
    """

    _validate_request(req)

    seed = _resolve_seed(req.seed)
    reconstruction_view = _canonicalize_view(req.reconstructionView)

    positions = _build_quadrant_map(reconstruction_view)
    per_view_w = req.views[0].width or 512
    per_view_h = req.views[0].height or 512

    rgb_composite = _composite_views(req.views, positions, "rgbBase64", (per_view_w, per_view_h), fill=(0, 0, 0, 255))

    rec = reconstruction_view
    if rec not in positions:
        rec = "Front"
    col, row = positions[rec]
    crop_x = col * per_view_w
    crop_y = row * per_view_h
    posix = _multiview_inpaint_positive(_effective_prompt(req.positivePrompt))
    negx = (req.negativePrompt or "").strip()
    if not negx:
        negx = REFINEMENT_DEFAULT_NEGATIVE

    # TripoSR lift: omit tripo_reference_mask_image so graph_injector stages a solid-white mask over
    # the Tripo crop — matches refinement.json Tripo conditioning and avoids degenerate meshes seen
    # with non-solid reference masks (dilated/tight semantic) in backend logs/user runs.
    tripo_sr_lift = max(18.0, float(tripo_threshold) - 3.0)

    by_canonical = {_canonicalize_view(v.viewType): v for v in req.views}
    refined_tiles_b64: Dict[str, str] = {}

    for qi, vt in enumerate(_CANONICAL_VIEWS):
        vp = by_canonical.get(vt)
        if vp is None:
            refined_tiles_b64[vt] = _quadrant_png_b64(
                rgb_composite, positions, vt, per_view_w, per_view_h
            )
            continue

        edges_raw = getattr(vp, "edgesBase64", "") or ""
        canny_tile = edges_raw.strip() if edges_raw.strip() else vp.rgbBase64
        tile_seed = (seed + qi * 1009 + ord(vt[0])) & 0x7FFFFFFF

        tile_req = RunRequest(
            mode="refine_inpaint_only",
            positive_prompt=_positive_for_inpaint_tile(posix, vt),
            negative_prompt=negx,
            rgb_image=vp.rgbBase64,
            depth_image=vp.depthBase64,
            mask_image=vp.maskBase64,
            canny_image=canny_tile,
            seed=tile_seed,
            steps=max(1, req.steps),
            cfg=max(0.0, req.cfg),
            denoise=max(0.0, min(1.0, req.denoise)),
            tripo_model=tripo_model,
            geometry_resolution=geometry_resolution,
            tripo_threshold=tripo_sr_lift,
        )

        inpaint_graph = load_graph(_INPAINT_TILE_GRAPH_NAME)
        inpaint_graph = inject_params(inpaint_graph, tile_req)
        tile_result = send_to_comfy(inpaint_graph)
        out_tile = tile_result.image_base64 or ""
        refined_tiles_b64[vt] = out_tile

    rgb_refined_composite = _composite_rgb_tiles_from_map(
        refined_tiles_b64, positions, per_view_w, per_view_h
    )

    trip_req = RunRequest(
        mode="tripo_from_rgb",
        positive_prompt=posix,
        negative_prompt=negx,
        rgb_image=rgb_refined_composite,
        seed=seed,
        steps=1,
        cfg=1.0,
        denoise=1.0,
        tripo_model=tripo_model,
        geometry_resolution=geometry_resolution,
        tripo_threshold=tripo_sr_lift,
        crop_width=per_view_w,
        crop_height=per_view_h,
        crop_x=crop_x,
        crop_y=crop_y,
    )
    trip_graph = load_graph(_TRIPO_FROM_RGB_GRAPH_NAME)
    trip_graph = inject_params(trip_graph, trip_req)
    trip_result = send_to_comfy(trip_graph)

    return MultiViewRefinementResponseModel(
        requestId=req.requestId,
        refinedViews=[
            RefinedViewResultModel(
                viewType=reconstruction_view,
                refinedImageBase64=rgb_refined_composite,
            )
        ],
        meshBase64=trip_result.mesh_base64 or "",
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


def _multiview_inpaint_positive(prompt: str) -> str:
    """Append compact cues for multi-view refinement (per-tile inpaint + fused preview).

    Avoid saturation-negative phrases; reinforce coherence and explicit materials."""
    p = (prompt or "").strip()
    if not p:
        return p
    low = p.lower()
    cues = ["consistent colors across all four tiled views", "photorealistic sharp detail"]
    if "roof" in low:
        cues.append("same roof geometry visible from front sides and above")
    if "white" in low:
        cues.append("opaque bright white roof material not neutral gray filler")
    return f"{p}, " + ", ".join(cues)


def _positive_for_inpaint_tile(base_positive: str, view_type: str) -> str:
    """Extra CLIP cues per camera tile — logs showed Top/L/R masked RGB barely moved vs Front."""
    p = (base_positive or "").strip()
    if not p:
        return p
    low = p.lower()
    vt = _canonicalize_view(view_type)
    extras: list[str] = []
    if "roof" in low:
        if vt == "Top":
            extras.append(
                "strict top-down orthographic horizontal roof planes opaque bright white diffuse roofing"
            )
        elif vt in ("Left", "Right"):
            extras.append("side elevation roof slope and eaves crisp white roofing material strong directional light")
        elif vt == "Front":
            extras.append("front facade dominant roof mass bright white shingles clearly readable")
    if extras:
        return f"{p}, " + ", ".join(extras)
    return p


def _canonicalize_view(view: str) -> str:
    key = (view or "").strip().lower()
    for canonical in _CANONICAL_VIEWS:
        if canonical.lower() == key:
            return canonical
    return "Front"


def _build_quadrant_map(reconstruction_view: str) -> Dict[str, Tuple[int, int]]:
    """Place the reconstruction view in the top-left (0, 0) cell. Remaining
    canonical views fill the other quadrants in a fixed order; crop_x/y match
    the reconstruction cell used by ImageCrop before TripoSR."""
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


__all__ = [
    "handle_multi_view_refine",
]
