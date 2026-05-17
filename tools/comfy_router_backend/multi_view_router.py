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
from typing import Any, Dict, Iterable, Optional, Tuple

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
_DEBUG_LOG_PATH = "/Users/alo/SpatialGenUnity/.cursor/debug-06fda4.log"


def _agent_ndjson(entry: Dict[str, Any]) -> None:
    # #region agent log
    import json
    import time

    row = {"sessionId": "06fda4", "timestamp": int(time.time() * 1000), **entry}
    try:
        with open(_DEBUG_LOG_PATH, "a", encoding="utf-8") as fh:
            fh.write(json.dumps(row) + "\n")
    except OSError:
        pass
    # #endregion


def _mv_input_stats(
    rgb_b64: str,
    mask_b64: str,
    positions: Dict[str, Tuple[int, int]],
    cell_w: int,
    cell_h: int,
) -> None:
    # #region agent log
    try:
        rgb = _decode_base64_png(rgb_b64).convert("RGBA")
        mask = _decode_base64_png(mask_b64).convert("RGBA")
    except Exception as exc:
        _agent_ndjson(
            {
                "hypothesisId": "H_INPUT_DECODE_FAIL",
                "location": "multi_view_router._mv_input_stats",
                "message": "composite_decode_failed",
                "data": {"error": str(exc)},
            }
        )
        return

    per_view: Dict[str, Any] = {}
    for vt, (col, row) in positions.items():
        left, top_px = col * cell_w, row * cell_h
        rg = rgb.crop((left, top_px, left + cell_w, top_px + cell_h))
        mk = mask.crop((left, top_px, left + cell_w, top_px + cell_h))
        rpix = list(rg.getdata())
        mpix = list(mk.getdata())
        n = len(rpix)
        mask_hi = 0
        rgb_sum = [0.0, 0.0, 0.0]
        rgb_in_mask = [0.0, 0.0, 0.0]
        mi = 0
        for i in range(n):
            mr = mpix[i][0] / 255.0
            rgb_sum[0] += rpix[i][0]
            rgb_sum[1] += rpix[i][1]
            rgb_sum[2] += rpix[i][2]
            if mr > 0.5:
                mask_hi += 1
                rgb_in_mask[0] += rpix[i][0]
                rgb_in_mask[1] += rpix[i][1]
                rgb_in_mask[2] += rpix[i][2]
                mi += 1
        frac_hi = mask_hi / max(1, n)
        mean_rgb = [rgb_sum[j] / max(1, n) / 255.0 for j in range(3)]
        mean_in_mask = (
            [rgb_in_mask[j] / max(1, mi) / 255.0 for j in range(3)] if mi > 0 else [-1.0, -1.0, -1.0]
        )
        per_view[vt] = {
            "maskWhiteFrac": round(frac_hi, 4),
            "quadMeanRgb": [round(x, 4) for x in mean_rgb],
            "maskedPixMeanRgb": [round(x, 4) for x in mean_in_mask],
            "maskedPixelCount": mi,
        }

    _agent_ndjson(
        {
            "hypothesisId": "H_COMPOSITE_INPUT_MASK_RGB",
            "location": "multi_view_router.handle_multi_view_refine",
            "message": "pre_comfy_composite",
            "data": {"perView": per_view, "gridSize": [cell_w * 2, cell_h * 2]},
        }
    )
    # #endregion


def _mv_graph_snapshot(
    graph: Dict[str, Any],
    run_request: RunRequest,
    *,
    extra: Optional[Dict[str, Any]] = None,
) -> None:
    # #region agent log
    ks = graph.get("3", {}).get("inputs", {})
    v9 = graph.get("9", {}).get("inputs", {})
    payload: Dict[str, Any] = {
        "runRequestPromptLen": len(run_request.positive_prompt or ""),
        "runRequestPositiveHead": (run_request.positive_prompt or "")[:96],
        "runRequestNegativeLen": len(run_request.negative_prompt or ""),
        "runRequestDenoise": run_request.denoise,
        "ksampler_denoise": ks.get("denoise"),
        "ksampler_denoise_type": type(ks.get("denoise")).__name__,
        "steps": ks.get("steps"),
        "cfg": ks.get("cfg"),
        "sampler_name": ks.get("sampler_name"),
        "scheduler": ks.get("scheduler"),
        "vae_encode_grow_mask_by": v9.get("grow_mask_by"),
        "latent_from_node": ks.get("latent_image"),
        "tripo_threshold_injected": run_request.tripo_threshold,
    }
    if extra:
        payload.update(extra)
    _agent_ndjson(
        {
            "hypothesisId": "H_GRAPH_KSAMPLER_INPAINT",
            "location": "multi_view_router.handle_multi_view_refine",
            "message": "post_inject_snapshot",
            "data": payload,
        }
    )
    # #endregion


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


def _masked_mean_rgb_small(rgb_b64: str, mask_b64: str) -> Optional[Dict[str, Any]]:
    try:
        ri = _decode_base64_png(rgb_b64).convert("RGBA")
        mk = _decode_base64_png(mask_b64).convert("RGBA")
    except Exception:
        return None
    pix_r = list(ri.getdata())
    pix_m = list(mk.getdata())
    si = [0.0, 0.0, 0.0]
    cnt = 0
    for i in range(len(pix_r)):
        if pix_m[i][0] <= 127:
            continue
        cnt += 1
        si[0] += pix_r[i][0]
        si[1] += pix_r[i][1]
        si[2] += pix_r[i][2]
    if cnt <= 0:
        return {"maskedPixels": 0}
    return {"maskedPixels": cnt, "maskedMeanRgb": [round(si[c] / cnt / 255.0, 4) for c in range(3)]}


def _log_tile_inpaint_hypothesis(
    *,
    view_type: str,
    tile_seed: int,
    rgb_in_b64: str,
    mask_b64: str,
    rgb_out_b64: Optional[str],
) -> None:
    # #region agent log
    before = _masked_mean_rgb_small(rgb_in_b64, mask_b64)
    after = _masked_mean_rgb_small(rgb_out_b64, mask_b64) if rgb_out_b64 else None
    _agent_ndjson(
        {
            "hypothesisId": "H_TILE_INPAINT_MASKED_RGB",
            "location": "multi_view_router.handle_multi_view_refine",
            "message": "per_tile_inpaint_masked_rgb",
            "data": {
                "viewTile": view_type,
                "tileSeed": tile_seed,
                "before": before,
                "after": after,
                "strategy": "per_tile_inpaint_then_tripo",
            },
        }
    )
    # #endregion


def _mv_output_stats(
    image_b64: Optional[str],
    crop_x: int,
    crop_y: int,
    crop_w: int,
    crop_h: int,
    positions: Dict[str, Tuple[int, int]],
    cell_w: int,
    cell_h: int,
) -> None:
    # #region agent log
    if not image_b64:
        _agent_ndjson(
            {
                "hypothesisId": "H_OUTPUT_IMAGE_MISSING",
                "location": "multi_view_router._mv_output_stats",
                "message": "no_image_from_comfy",
                "data": {},
            }
        )
        return
    try:
        im = _decode_base64_png(image_b64).convert("RGBA")
    except Exception as exc:
        _agent_ndjson(
            {
                "hypothesisId": "H_OUTPUT_DECODE_FAIL",
                "location": "multi_view_router._mv_output_stats",
                "message": "decode_failed",
                "data": {"error": str(exc)},
            }
        )
        return

    quad_means: Dict[str, Any] = {}
    gray_frac_quad: Dict[str, float] = {}

    def _neutral_gray(px: Any) -> bool:
        r, g, b = px[0] / 255.0, px[1] / 255.0, px[2] / 255.0
        return (
            0.38 <= r <= 0.62
            and 0.38 <= g <= 0.62
            and 0.38 <= b <= 0.62
            and abs(r - g) < 0.08
            and abs(g - b) < 0.08
        )

    for vt, (col, row) in positions.items():
        left, top_px = col * cell_w, row * cell_h
        box = im.crop((left, top_px, left + cell_w, top_px + cell_h))
        pix = list(box.getdata())
        n = len(pix)
        sr = sg = sb = 0.0
        gray_ct = 0
        for p in pix:
            sr += p[0]
            sg += p[1]
            sb += p[2]
            if _neutral_gray(p):
                gray_ct += 1
        quad_means[vt] = [round(sr / max(1, n) / 255.0, 4), round(sg / max(1, n) / 255.0, 4), round(sb / max(1, n) / 255.0, 4)]
        gray_frac_quad[vt] = round(gray_ct / max(1, n), 4)

    crop_box = im.crop((crop_x, crop_y, crop_x + crop_w, crop_y + crop_h))
    cpix = list(crop_box.getdata())
    cn = len(cpix)
    cr = cg = cb = 0.0
    cg_ct = 0
    for p in cpix:
        cr += p[0]
        cg += p[1]
        cb += p[2]
        if _neutral_gray(p):
            cg_ct += 1
    trip_mean = [round(cr / max(1, cn) / 255.0, 4), round(cg / max(1, cn) / 255.0, 4), round(cb / max(1, cn) / 255.0, 4)]
    trip_gray_frac = round(cg_ct / max(1, cn), 4)

    _agent_ndjson(
        {
            "hypothesisId": "H_OUTPUT_REFINED_GRID_TRIPO",
            "location": "multi_view_router.handle_multi_view_refine",
            "message": "post_comfy_output",
            "data": {
                "outSize": [im.width, im.height],
                "quadMeanRgb": quad_means,
                "quadNeutralGrayFrac": gray_frac_quad,
                "tripoCropRect": [crop_x, crop_y, crop_w, crop_h],
                "tripoCropMeanRgb": trip_mean,
                "tripoCropNeutralGrayFrac": trip_gray_frac,
            },
        }
    )
    # #endregion


def _mv_masked_io_rgb(
    rgb_in_b64: str,
    mask_b64: str,
    rgb_out_b64: Optional[str],
    positions: Dict[str, Tuple[int, int]],
    cell_w: int,
    cell_h: int,
) -> None:
    # #region agent log
    if not rgb_out_b64:
        return
    try:
        ri = _decode_base64_png(rgb_in_b64).convert("RGBA")
        mk = _decode_base64_png(mask_b64).convert("RGBA")
        ro = _decode_base64_png(rgb_out_b64).convert("RGBA")
    except Exception as exc:
        _agent_ndjson(
            {
                "hypothesisId": "H_MASKED_IO_DECODE_FAIL",
                "location": "multi_view_router._mv_masked_io_rgb",
                "message": "decode_failed",
                "data": {"error": str(exc)},
            }
        )
        return

    per_v: Dict[str, Any] = {}
    for vt, (col, row) in positions.items():
        left, top_px = col * cell_w, row * cell_h
        box_i = ri.crop((left, top_px, left + cell_w, top_px + cell_h))
        box_m = mk.crop((left, top_px, left + cell_w, top_px + cell_h))
        box_o = ro.crop((left, top_px, left + cell_w, top_px + cell_h))
        pi, pm, po = list(box_i.getdata()), list(box_m.getdata()), list(box_o.getdata())
        n = len(pi)
        si = [0.0, 0.0, 0.0]
        so = [0.0, 0.0, 0.0]
        cnt = 0
        for i in range(n):
            if pm[i][0] <= 127:
                continue
            cnt += 1
            for c in range(3):
                si[c] += pi[i][c]
                so[c] += po[i][c]
        if cnt <= 0:
            per_v[vt] = {"maskedPixels": 0}
            continue
        per_v[vt] = {
            "maskedPixels": cnt,
            "inMaskedMeanRgb": [round(si[c] / cnt / 255.0, 4) for c in range(3)],
            "outMaskedMeanRgb": [round(so[c] / cnt / 255.0, 4) for c in range(3)],
        }

    _agent_ndjson(
        {
            "hypothesisId": "H_MASKED_IN_VS_OUT_RGB",
            "location": "multi_view_router.handle_multi_view_refine",
            "message": "masked_region_before_after_comfy",
            "data": {"perView": per_v},
        }
    )
    # #endregion


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
    mask_composite = _composite_views(req.views, positions, "maskBase64", (per_view_w, per_view_h), fill=(0, 0, 0, 255))

    # #region agent log
    _mv_input_stats(rgb_composite, mask_composite, positions, per_view_w, per_view_h)
    # #endregion

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

    # #region agent log
    _agent_ndjson(
        {
            "hypothesisId": "H_PIPELINE_MV",
            "location": "multi_view_router.handle_multi_view_refine",
            "message": "strategy_selected",
            "data": {
                "strategy": "per_tile_inpaint_then_tripo_rgb_subgraph",
                "hypothesisBrief": (
                    "H1_fused_latent_split_attention; "
                    "H2_check_H_TILE_INPAINT_MASKED_RGB_outMaskedMean_vs_gray"
                ),
                "comfyInpaintPasses": len(_CANONICAL_VIEWS),
                "comfyTripoPasses": 1,
                "tripoSrLiftThreshold": tripo_sr_lift,
                "tripoInputPolicy": "natural_rgb_solid_white_ref_mask",
            },
        }
    )
    # #endregion

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
        # #region agent log
        _mv_graph_snapshot(inpaint_graph, tile_req, extra={"inpaintTile": vt})
        # #endregion
        tile_result = send_to_comfy(inpaint_graph)
        out_tile = tile_result.image_base64 or ""
        refined_tiles_b64[vt] = out_tile
        # #region agent log
        _log_tile_inpaint_hypothesis(
            view_type=vt,
            tile_seed=tile_seed,
            rgb_in_b64=vp.rgbBase64,
            mask_b64=vp.maskBase64,
            rgb_out_b64=out_tile if out_tile else None,
        )
        # #endregion

    rgb_refined_composite = _composite_rgb_tiles_from_map(
        refined_tiles_b64, positions, per_view_w, per_view_h
    )

    # #region agent log
    _agent_ndjson(
        {
            "hypothesisId": "H_MV_RECOMPOSE",
            "location": "multi_view_router.handle_multi_view_refine",
            "message": "recomposed_inpainted_grid",
            "data": {
                "hasTileOutputs": [bool(refined_tiles_b64.get(v)) for v in _CANONICAL_VIEWS],
                "views": list(_CANONICAL_VIEWS),
            },
        }
    )
    # #endregion

    # #region agent log
    _agent_ndjson(
        {
            "hypothesisId": "H_TRIPO_INPUT_POLICY",
            "location": "multi_view_router.handle_multi_view_refine",
            "message": "tripo_natural_rgb_solid_white_ref_mask",
            "data": {
                "tripoSrLiftThreshold": tripo_sr_lift,
                "rgbSource": "rgb_refined_composite",
                "tripoReferenceMaskStaged": "solid_white_via_graph_injector",
                "fixNote": "removed_dilated_trip_mask_H_TRIPO_MASK_DILATE_degenerate_geometry",
                "tradeOffBgHull": True,
            },
        }
    )
    # #endregion

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

    # #region agent log
    _mv_output_stats(
        rgb_refined_composite,
        crop_x,
        crop_y,
        per_view_w,
        per_view_h,
        positions,
        per_view_w,
        per_view_h,
    )
    _mv_masked_io_rgb(rgb_composite, mask_composite, rgb_refined_composite, positions, per_view_w, per_view_h)
    # #endregion

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
