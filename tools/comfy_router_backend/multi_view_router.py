"""Four-view, region-scoped refinement with Hunyuan3D-2mv and TripoSR fallback."""

from __future__ import annotations

import base64
import copy
import io
import random
from collections import deque
from typing import Any, Tuple

from PIL import Image, ImageFilter

from .capabilities import lifter_available
from .comfy_client import send_to_comfy, send_to_comfy_all
from .graph_injector import inject_params
from .graph_loader import load_graph
from .models import (
    REFINEMENT_ATTACHMENT_CUES,
    REFINEMENT_DEFAULT_NEGATIVE,
    MultiViewRefinementRequest,
    MultiViewRefinementResponse,
    RefinedViewResult,
    RunRequest,
    ViewPayload,
)

_INPAINT_GRAPH = "refinement_inpaint_only.json"
_TRIPO_GRAPH = "refinement_tripo_from_rgb.json"
_HY3D_GRAPH = "refinement_hunyuan_mv.json"
_CARDINAL_VIEWS = ("Front", "Back", "Left", "Right")
_SHARED_INPAINT_NODES = {"4", "6", "7", "16", "17"}
_LETTERBOX_SIZE = 512
_SUBJECT_SIZE = 432
_REFINEMENT_MASK_GROW = 25


def handle_refine(
    req: MultiViewRefinementRequest,
    *,
    tripo_model: str,
    geometry_resolution: int,
    tripo_threshold: float,
) -> MultiViewRefinementResponse:
    _validate(req)
    views = _views_by_name(req)
    requested = req.lifter
    use_hunyuan = requested in ("auto", "hunyuan3d_2mv")
    hy3d_available, hy3d_detail = (False, "Hunyuan3D-2mv was not requested")
    if use_hunyuan:
        hy3d_available, hy3d_detail = lifter_available("hunyuan3d_2mv")
        missing_inputs = [name for name in _CARDINAL_VIEWS if name not in views]
        if missing_inputs:
            hy3d_available = False
            hy3d_detail = (
                "Hunyuan3D-2mv requires Front/Back/Left/Right; missing "
                + ", ".join(missing_inputs)
            )
        if requested == "hunyuan3d_2mv" and not req.allowFallback and not hy3d_available:
            # Fail before the expensive four-view diffusion pass.
            raise RuntimeError(hy3d_detail)

    seed = req.seed if req.seed >= 0 else random.randint(0, 2**31 - 1)
    positive = f"{req.positivePrompt.strip()}, {REFINEMENT_ATTACHMENT_CUES}"
    negative = ", ".join(
        part for part in ((req.negativePrompt or "").strip(), REFINEMENT_DEFAULT_NEGATIVE) if part
    )

    graph = _build_multi_inpaint_graph(
        views,
        positive=positive,
        negative=negative,
        seed=seed,
        steps=req.steps,
        cfg=req.cfg,
        denoise=req.denoise,
        tripo_model=tripo_model,
        geometry_resolution=geometry_resolution,
        tripo_threshold=tripo_threshold,
    )
    outputs = send_to_comfy_all(graph)
    refined = _collect_refined_views(outputs.images, views)
    prepared: dict[str, str] = {}
    prepared_masks: dict[str, str] = {}
    for name, view in views.items():
        if name not in refined:
            continue
        prepared[name], prepared_masks[name] = _prepare_lifter_view(
            req, refined[name], view
        )

    warnings: list[str] = []
    if use_hunyuan:
        missing = [name for name in _CARDINAL_VIEWS if name not in prepared]
        if missing:
            hy3d_available = False
            hy3d_detail = f"Hunyuan3D-2mv inpaint outputs are missing {', '.join(missing)}"

        if hy3d_available:
            try:
                mesh = _lift_hunyuan(
                    prepared,
                    positive=positive,
                    negative=negative,
                    seed=seed,
                    tripo_model=tripo_model,
                    tripo_threshold=tripo_threshold,
                )
                return _response(req, refined, mesh, "hunyuan3d_2mv", False, warnings)
            except Exception as exc:  # noqa: BLE001 - Auto is explicitly a resilient mode
                hy3d_detail = f"Hunyuan3D-2mv failed: {exc}"

        if requested == "hunyuan3d_2mv" and not req.allowFallback:
            raise RuntimeError(hy3d_detail)
        warnings.append(f"{hy3d_detail}; used TripoSR instead.")

    available, detail = lifter_available("tripo_sr")
    if not available:
        raise RuntimeError(detail)
    if "Front" not in prepared:
        raise RuntimeError("TripoSR fallback requires a Front view")

    mesh = _lift_tripo(
        prepared["Front"],
        prepared_masks["Front"],
        seed=seed,
        positive=positive,
        negative=negative,
        tripo_model=tripo_model,
        geometry_resolution=geometry_resolution,
        tripo_threshold=tripo_threshold,
    )
    return _response(req, refined, mesh, "tripo_sr", use_hunyuan, warnings)


def _build_multi_inpaint_graph(
    views: dict[str, ViewPayload],
    *,
    positive: str,
    negative: str,
    seed: int,
    steps: int,
    cfg: float,
    denoise: float,
    tripo_model: str,
    geometry_resolution: int,
    tripo_threshold: float,
) -> dict[str, Any]:
    merged: dict[str, Any] = {}
    for branch_index, name in enumerate(_CARDINAL_VIEWS):
        view = views.get(name)
        if view is None:
            continue
        request = RunRequest(
            mode="refine_inpaint_only",
            positive_prompt=positive,
            negative_prompt=negative,
            rgb_image=view.rgbBase64,
            depth_image=view.depthBase64,
            mask_image=view.maskBase64,
            canny_image=view.edgesBase64 or view.rgbBase64,
            seed=seed,
            steps=steps,
            cfg=cfg,
            denoise=min(1.0, max(0.0, denoise)),
            tripo_model=tripo_model,
            geometry_resolution=geometry_resolution,
            tripo_threshold=tripo_threshold,
        )
        branch = inject_params(load_graph(_INPAINT_GRAPH), request)
        mapping = {
            node_id: node_id if node_id in _SHARED_INPAINT_NODES
            else str((branch_index + 1) * 1000 + int(node_id))
            for node_id in branch
        }

        for node_id, node in branch.items():
            if node_id in _SHARED_INPAINT_NODES and node_id in merged:
                continue
            remapped = _remap_links(copy.deepcopy(node), mapping)
            if node.get("class_type") == "SaveImage":
                remapped["inputs"]["filename_prefix"] = f"spatialgen_refined_{name.lower()}"
            merged[mapping[node_id]] = remapped

    if not merged:
        raise ValueError("No canonical refinement views were available")
    return merged


def _remap_links(value: Any, mapping: dict[str, str]) -> Any:
    if isinstance(value, dict):
        return {key: _remap_links(inner, mapping) for key, inner in value.items()}
    if isinstance(value, list):
        if len(value) == 2 and str(value[0]) in mapping and isinstance(value[1], int):
            return [mapping[str(value[0])], value[1]]
        return [_remap_links(inner, mapping) for inner in value]
    return value


def _collect_refined_views(outputs, views: dict[str, ViewPayload]) -> dict[str, str]:
    refined: dict[str, str] = {}
    for name in views:
        marker = f"spatialgen_refined_{name.lower()}"
        match = next((output for output in outputs if marker in output.filename.lower()), None)
        if match is None:
            raise RuntimeError(f"The multi-view inpaint produced no {name} image")
        refined[name] = match.data_base64
    return refined


def _lift_hunyuan(
    prepared: dict[str, str],
    *,
    positive: str,
    negative: str,
    seed: int,
    tripo_model: str,
    tripo_threshold: float,
) -> str:
    request = RunRequest(
        mode="refine_hunyuan_mv",
        positive_prompt=positive,
        negative_prompt=negative,
        rgb_image=prepared["Front"],
        back_image=prepared["Back"],
        left_image=prepared["Left"],
        right_image=prepared["Right"],
        seed=seed,
        steps=20,
        cfg=5.0,
        denoise=1.0,
        tripo_model=tripo_model,
        geometry_resolution=256,
        tripo_threshold=tripo_threshold,
    )
    result = send_to_comfy(inject_params(load_graph(_HY3D_GRAPH), request))
    if not result.mesh_base64:
        raise RuntimeError("Hunyuan3D-2mv produced no mesh")
    return result.mesh_base64


def _lift_tripo(
    image: str,
    mask: str,
    *,
    seed: int,
    positive: str,
    negative: str,
    tripo_model: str,
    geometry_resolution: int,
    tripo_threshold: float,
) -> str:
    request = RunRequest(
        mode="tripo_from_rgb",
        positive_prompt=positive,
        negative_prompt=negative,
        rgb_image=image,
        mask_image=mask,
        seed=seed,
        steps=1,
        cfg=1.0,
        denoise=1.0,
        tripo_model=tripo_model,
        geometry_resolution=geometry_resolution,
        tripo_threshold=tripo_threshold,
        crop_width=_LETTERBOX_SIZE,
        crop_height=_LETTERBOX_SIZE,
        crop_x=0,
        crop_y=0,
    )
    result = send_to_comfy(inject_params(load_graph(_TRIPO_GRAPH), request))
    if not result.mesh_base64:
        raise RuntimeError("TripoSR produced no mesh")
    return result.mesh_base64


def _response(
    req: MultiViewRefinementRequest,
    refined: dict[str, str],
    mesh: str,
    lifter: str,
    fallback: bool,
    warnings: list[str],
) -> MultiViewRefinementResponse:
    return MultiViewRefinementResponse(
        requestId=req.requestId,
        refinedViews=[
            RefinedViewResult(viewType=name, refinedImageBase64=refined[name])
            for name in _CARDINAL_VIEWS if name in refined
        ],
        meshBase64=mesh,
        success=True,
        status="success",
        lifterUsed=lifter,
        fallbackUsed=fallback,
        warnings=warnings,
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
            name for name, value in (
                ("rgbBase64", view.rgbBase64),
                ("depthBase64", view.depthBase64),
                ("maskBase64", view.maskBase64),
            ) if not value
        ]
        if missing:
            raise ValueError(f"View '{view.viewType}' is missing: {', '.join(missing)}")


def _views_by_name(req: MultiViewRefinementRequest) -> dict[str, ViewPayload]:
    result: dict[str, ViewPayload] = {}
    for view in req.views:
        canonical = _canonicalize(view.viewType)
        if canonical in _CARDINAL_VIEWS:
            result[canonical] = view
    if "Front" not in result:
        raise ValueError("Refinement requires a Front view")
    return result


def _canonicalize(view: str) -> str:
    key = (view or "").strip().lower()
    for canonical in _CARDINAL_VIEWS:
        if canonical.lower() == key:
            return canonical
    return ""


def _prepare_lifter_image(
    req: MultiViewRefinementRequest, encoded: str, view: ViewPayload
) -> str:
    image, _ = _prepare_lifter_view(req, encoded, view)
    return image


def _prepare_lifter_view(
    req: MultiViewRefinementRequest, encoded: str, view: ViewPayload
) -> tuple[str, str]:
    """Crops one refined view and supplies TripoSR with a real foreground mask.

    The Unity mask is the projection of the selected surface, so it is a stronger locality
    signal than estimating foreground from image colour. It is grown to retain geometry
    introduced by the inpaint, reduced to the component nearest the crop centre, and used
    both to whiten the RGB background and as TripoSR's reference mask.
    """
    raw = base64.b64decode(encoded.split(",", 1)[-1])
    mask_raw = base64.b64decode(view.maskBase64.split(",", 1)[-1])
    with Image.open(io.BytesIO(raw)) as source, Image.open(io.BytesIO(mask_raw)) as mask_source:
        source = source.convert("RGB")
        mask_source = mask_source.convert("L")
        if mask_source.size != source.size:
            mask_source = mask_source.resize(source.size, Image.Resampling.NEAREST)
        crop = _view_crop(req, view)
        x, y, width, height = _pixel_crop(source.size, crop)
        subject = source.crop((x, y, x + width, y + height))
        mask_subject = mask_source.crop((x, y, x + width, y + height))
        mask_subject = mask_subject.point(lambda value: 255 if value >= 128 else 0)
        mask_subject = mask_subject.filter(ImageFilter.MaxFilter(_REFINEMENT_MASK_GROW))
        mask_subject = _central_component(mask_subject)

        scale = min(_SUBJECT_SIZE / subject.width, _SUBJECT_SIZE / subject.height)
        resized_size = (
            max(1, round(subject.width * scale)),
            max(1, round(subject.height * scale)),
        )
        resized = subject.resize(resized_size, Image.Resampling.LANCZOS)
        resized_mask = mask_subject.resize(resized_size, Image.Resampling.NEAREST)
        offset = (
            (_LETTERBOX_SIZE - resized.width) // 2,
            (_LETTERBOX_SIZE - resized.height) // 2,
        )

        canvas = Image.new("RGB", (_LETTERBOX_SIZE, _LETTERBOX_SIZE), "white")
        canvas.paste(resized, offset, resized_mask)
        mask_canvas = Image.new("L", (_LETTERBOX_SIZE, _LETTERBOX_SIZE), 0)
        mask_canvas.paste(resized_mask, offset)

        return _encode_png(canvas), _encode_png(mask_canvas)


def _central_component(mask: Image.Image) -> Image.Image:
    """Keeps the connected mask component that best represents the centred subject."""
    width, height = mask.size
    pixels = bytearray(mask.tobytes())
    visited = bytearray(width * height)
    components: list[tuple[list[int], float]] = []
    centre_x = (width - 1) * 0.5
    centre_y = (height - 1) * 0.5

    for seed, value in enumerate(pixels):
        if value < 128 or visited[seed]:
            continue
        queue = deque([seed])
        visited[seed] = 1
        component: list[int] = []
        sum_x = 0.0
        sum_y = 0.0
        while queue:
            index = queue.popleft()
            component.append(index)
            px = index % width
            py = index // width
            sum_x += px
            sum_y += py
            for neighbour in (index - 1, index + 1, index - width, index + width):
                if neighbour < 0 or neighbour >= len(pixels) or visited[neighbour]:
                    continue
                nx = neighbour % width
                ny = neighbour // width
                if abs(nx - px) + abs(ny - py) != 1 or pixels[neighbour] < 128:
                    continue
                visited[neighbour] = 1
                queue.append(neighbour)

        centroid_x = sum_x / len(component)
        centroid_y = sum_y / len(component)
        distance = ((centroid_x - centre_x) ** 2 + (centroid_y - centre_y) ** 2) ** 0.5
        # Area wins unless two components are comparable; proximity then selects the subject
        # rather than a retained fragment near the crop edge.
        score = len(component) / (1.0 + distance / max(1.0, min(width, height)))
        components.append((component, score))

    if not components:
        raise RuntimeError("The Front refinement mask contains no liftable foreground")

    chosen = max(components, key=lambda item: item[1])[0]
    cleaned = bytearray(width * height)
    for index in chosen:
        cleaned[index] = 255
    return Image.frombytes("L", (width, height), bytes(cleaned))


def _encode_png(image: Image.Image) -> str:
    buffer = io.BytesIO()
    image.save(buffer, format="PNG")
    return base64.b64encode(buffer.getvalue()).decode("ascii")


def _view_crop(req: MultiViewRefinementRequest, view: ViewPayload) -> tuple[float, float, float, float]:
    crop = (view.cropMinX, view.cropMinY, view.cropMaxX, view.cropMaxY)
    if _canonicalize(view.viewType) == "Front" and crop == (0.0, 0.0, 1.0, 1.0):
        legacy = (req.cropMinX, req.cropMinY, req.cropMaxX, req.cropMaxY)
        if legacy != (0.0, 0.0, 1.0, 1.0):
            return legacy
    return crop


def _pixel_crop(
    image_size: tuple[int, int], crop: tuple[float, float, float, float]
) -> Tuple[int, int, int, int]:
    width, height = image_size
    min_x, min_y, max_x, max_y = (_clamp01(value) for value in crop)
    if max_x <= min_x or max_y <= min_y:
        return 0, 0, width, height
    x = int(round(min_x * width))
    y = int(round((1.0 - max_y) * height))
    crop_width = max(8, int(round((max_x - min_x) * width)))
    crop_height = max(8, int(round((max_y - min_y) * height)))
    crop_width = min(width, crop_width)
    crop_height = min(height, crop_height)
    x = min(max(0, x), width - crop_width)
    y = min(max(0, y), height - crop_height)
    return x, y, crop_width, crop_height


def _clamp01(value: float) -> float:
    return min(1.0, max(0.0, float(value)))


__all__ = ["handle_refine"]
