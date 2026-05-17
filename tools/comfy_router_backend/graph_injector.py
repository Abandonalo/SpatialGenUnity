import base64
import copy
import io
import json
import os
import re
import uuid
from pathlib import Path
from typing import Any, Dict, Optional, Tuple

from PIL import Image

from .models import RunRequest


_DATA_URI_PATTERN = re.compile(r"^data:(?P<mime>[\w.+-]+/[\w.+-]+);base64,(?P<data>.+)$", re.IGNORECASE)
_PLACEHOLDER_PATTERN = re.compile(r"__[A-Z0-9_]+__")
_IMAGE_EXTENSIONS = {
    "image/png": ".png",
    "image/jpeg": ".jpg",
    "image/jpg": ".jpg",
    "image/webp": ".webp",
}


def inject_params(graph: Dict[str, Any], req: RunRequest) -> Dict[str, Any]:
    g = copy.deepcopy(graph)
    replacements = _build_replacements(req)
    injected = _replace_placeholders(g, replacements)

    unresolved = sorted(_collect_placeholders(injected))
    if unresolved:
        joined = ", ".join(unresolved)
        raise ValueError(f"Unresolved graph placeholders: {joined}")

    return injected


def _tripo_crop_replacements(req: RunRequest) -> Dict[str, Any]:
    """Crop applied after inpaint so TripoSR ingests a single coherent view.

    Multi-view requests set explicit crop_* for the reconstruction quadrant.
    Single-view /refine omits crop_* — use the full staged RGB dimensions.
    """
    if req.crop_width is not None and req.crop_height is not None:
        crop_w, crop_h = req.crop_width, req.crop_height
    else:
        dims = _rgb_dimensions_from_base64(req.rgb_image)
        crop_w, crop_h = dims if dims else (512, 512)
    crop_x = 0 if req.crop_x is None else req.crop_x
    crop_y = 0 if req.crop_y is None else req.crop_y
    return {
        "__CROP_WIDTH__": crop_w,
        "__CROP_HEIGHT__": crop_h,
        "__CROP_X__": crop_x,
        "__CROP_Y__": crop_y,
    }


def _rgb_dimensions_from_base64(encoded: Optional[str]) -> Optional[Tuple[int, int]]:
    if not encoded:
        return None
    raw = encoded.strip()
    match = _DATA_URI_PATTERN.match(raw)
    if match:
        raw = match.group("data")
    try:
        payload = base64.b64decode(raw, validate=True)
    except Exception:
        return None
    try:
        with Image.open(io.BytesIO(payload)) as im:
            return im.size
    except Exception:
        return None


def _build_replacements(req: RunRequest) -> Dict[str, Any]:
    replacements: Dict[str, Any] = {
        "__POSITIVE_PROMPT__": req.positive_prompt,
        "__NEGATIVE_PROMPT__": req.negative_prompt,
        "__MESH_PROMPT__": req.positive_prompt,
        "__MESH_NEG_PROMPT__": req.negative_prompt,
        "__SEED__": req.seed,
        "__STEPS__": req.steps,
        "__CFG__": req.cfg,
        "__DENOISE__": req.denoise,
        "__TRIPOSR_MODEL__": req.tripo_model,
        "__GEOMETRY_RESOLUTION__": req.geometry_resolution,
        "__TRIPOSR_THRESHOLD__": req.tripo_threshold,
        **_tripo_crop_replacements(req),
        # Names must appear in ControlNetLoader’s list under models/controlnet/ (subdir layout matches Comfy’s UI).
        "__CONTROLNET_DEPTH__": os.getenv(
            "COMFY_CONTROLNET_DEPTH",
            "controlnet-depth/control_v11p_sd15_depth.pth",
        ),
        "__CONTROLNET_CANNY__": os.getenv(
            "COMFY_CONTROLNET_CANNY",
            "controlnet-canny/control_v11p_sd15_canny.pth",
        ),
        # SD1.5 inpaint UNet for refinement.json; must match a ckpt_name listed by ComfyUI.
        "__INPAINT_CHECKPOINT__": os.getenv(
            "COMFY_INPAINT_CHECKPOINT",
            "sd-v1-5-inpainting.ckpt",
        ),
    }

    checkpoint = os.getenv("COMFY_CHECKPOINT")
    if checkpoint:
        replacements["__CHECKPOINT__"] = checkpoint

    if req.mode in ("refine", "refine_inpaint_only"):
        replacements.update(_stage_refine_images(req))
    elif req.mode == "tripo_from_rgb":
        replacements.update(_stage_tripo_from_rgb_images(req))

    return replacements


def _replace_placeholders(value: Any, replacements: Dict[str, Any]) -> Any:
    if isinstance(value, dict):
        return {key: _replace_placeholders(inner, replacements) for key, inner in value.items()}

    if isinstance(value, list):
        return [_replace_placeholders(item, replacements) for item in value]

    if not isinstance(value, str):
        return value

    if value in replacements:
        return replacements[value]

    result = value
    for placeholder, replacement in replacements.items():
        if placeholder in result:
            result = result.replace(placeholder, str(replacement))
    return result


def _collect_placeholders(value: Any) -> set[str]:
    found: set[str] = set()
    if isinstance(value, dict):
        for inner in value.values():
            found.update(_collect_placeholders(inner))
        return found

    if isinstance(value, list):
        for inner in value:
            found.update(_collect_placeholders(inner))
        return found

    if isinstance(value, str):
        found.update(_PLACEHOLDER_PATTERN.findall(value))

    return found


def _tripo_mask_dimensions_px(req: RunRequest) -> Tuple[int, int]:
    if req.crop_width and req.crop_height:
        return int(req.crop_width), int(req.crop_height)
    dims = _rgb_dimensions_from_base64(req.rgb_image)
    if dims:
        return int(dims[0]), int(dims[1])
    return 512, 512


def _solid_white_png_bytes(width: int, height: int) -> bytes:
    w, h = max(1, int(width)), max(1, int(height))
    buf = io.BytesIO()
    Image.new("RGB", (w, h), (255, 255, 255)).save(buf, format="PNG")
    return buf.getvalue()


def _stage_refine_images(req: RunRequest) -> Dict[str, str]:
    run_token = uuid.uuid4().hex
    rgb_bytes = _decode_base64_to_bytes(req.rgb_image, "rgb")
    mask_bytes = _decode_base64_to_bytes(req.mask_image, "mask")
    depth_bytes = _decode_base64_to_bytes(req.depth_image, "depth")
    canny_src = req.canny_image if req.canny_image else req.rgb_image
    canny_bytes = _decode_base64_to_bytes(canny_src, "canny")

    ext_rgb = _guess_extension(req.rgb_image)
    ext_mask = _guess_extension(req.mask_image)
    ext_depth = _guess_extension(req.depth_image)
    ext_canny = _guess_extension(canny_src)

    mw, mh = _tripo_mask_dimensions_px(req)
    tripo_mask_bytes = _solid_white_png_bytes(mw, mh)

    use_upload = os.getenv("COMFY_REFINE_USE_UPLOAD", "1").strip().lower() not in (
        "0",
        "false",
        "no",
    )
    if use_upload:
        try:
            from .comfy_client import upload_input_image

            return {
                "__RGB_IMAGE__": upload_input_image(
                    f"{run_token}_rgb{ext_rgb}",
                    rgb_bytes,
                    content_type=_mime_for_extension(ext_rgb),
                ),
                "__MASK_IMAGE__": upload_input_image(
                    f"{run_token}_mask{ext_mask}",
                    mask_bytes,
                    content_type=_mime_for_extension(ext_mask),
                ),
                "__DEPTH_IMAGE__": upload_input_image(
                    f"{run_token}_depth{ext_depth}",
                    depth_bytes,
                    content_type=_mime_for_extension(ext_depth),
                ),
                "__CANNY_IMAGE__": upload_input_image(
                    f"{run_token}_canny{ext_canny}",
                    canny_bytes,
                    content_type=_mime_for_extension(ext_canny),
                ),
                "__TRIPO_SOLID_MASK__": upload_input_image(
                    f"{run_token}_tripo_solid_mask.png",
                    tripo_mask_bytes,
                    content_type="image/png",
                ),
            }
        except Exception as exc:
            import sys

            print(
                "[comfy_router_backend] Refine upload to ComfyUI failed "
                f"({exc!r}); falling back to COMFY_INPUT_DIR.",
                file=sys.stderr,
            )

    input_root = _get_comfy_input_dir()
    rgb_path = _write_bytes_to_file(rgb_bytes, input_root / f"{run_token}_rgb{ext_rgb}")
    mask_path = _write_bytes_to_file(mask_bytes, input_root / f"{run_token}_mask{ext_mask}")
    depth_path = _write_bytes_to_file(depth_bytes, input_root / f"{run_token}_depth{ext_depth}")
    canny_path = _write_bytes_to_file(canny_bytes, input_root / f"{run_token}_canny{ext_canny}")
    tripo_mask_path = _write_bytes_to_file(
        tripo_mask_bytes, input_root / f"{run_token}_tripo_solid_mask.png"
    )

    return {
        "__RGB_IMAGE__": rgb_path.name,
        "__MASK_IMAGE__": mask_path.name,
        "__DEPTH_IMAGE__": depth_path.name,
        "__CANNY_IMAGE__": canny_path.name,
        "__TRIPO_SOLID_MASK__": tripo_mask_path.name,
    }


def _tripo_reference_mask_png_bytes(req: RunRequest, mw: int, mh: int) -> bytes:
    """Bytes for TripoSR ImageToMask input: semantic crop from Unity or solid white fallback."""
    raw_opt = (req.tripo_reference_mask_image or "").strip()
    if not raw_opt:
        return _solid_white_png_bytes(mw, mh)
    raw_bytes = _decode_base64_to_bytes(raw_opt, "tripo_reference_mask")
    try:
        with Image.open(io.BytesIO(raw_bytes)) as im:
            rgba = im.convert("RGBA")
            if rgba.size != (mw, mh):
                rgba = rgba.resize((max(1, mw), max(1, mh)), Image.NEAREST)
            buf = io.BytesIO()
            rgba.save(buf, format="PNG")
            return buf.getvalue()
    except Exception:
        return raw_bytes


def _stage_tripo_from_rgb_images(req: RunRequest) -> Dict[str, str]:
    """Stage reconstructed RGB composite and TripoSR reference_mask (semantic crop or solid white).

    Used by refinement_tripo_from_rgb.json (no Stable Diffusion / depth uploads).
    """
    run_token = uuid.uuid4().hex
    rgb_bytes = _decode_base64_to_bytes(req.rgb_image, "rgb")
    ext_rgb = _guess_extension(req.rgb_image)
    mw, mh = _tripo_mask_dimensions_px(req)
    tripo_mask_bytes = _tripo_reference_mask_png_bytes(req, mw, mh)

    use_upload = os.getenv("COMFY_REFINE_USE_UPLOAD", "1").strip().lower() not in (
        "0",
        "false",
        "no",
    )
    if use_upload:
        try:
            from .comfy_client import upload_input_image

            return {
                "__RGB_IMAGE__": upload_input_image(
                    f"{run_token}_mv_trip_rgb{ext_rgb}",
                    rgb_bytes,
                    content_type=_mime_for_extension(ext_rgb),
                ),
                "__TRIPO_SOLID_MASK__": upload_input_image(
                    f"{run_token}_tripo_solid_mask.png",
                    tripo_mask_bytes,
                    content_type="image/png",
                ),
            }
        except Exception as exc:
            import sys

            print(
                "[comfy_router_backend] Tripo RGB upload to ComfyUI failed "
                f"({exc!r}); falling back to COMFY_INPUT_DIR.",
                file=sys.stderr,
            )

    input_root = _get_comfy_input_dir()
    rgb_path = _write_bytes_to_file(rgb_bytes, input_root / f"{run_token}_mv_trip_rgb{ext_rgb}")
    tripo_mask_path = _write_bytes_to_file(
        tripo_mask_bytes, input_root / f"{run_token}_tripo_solid_mask.png"
    )

    return {
        "__RGB_IMAGE__": rgb_path.name,
        "__TRIPO_SOLID_MASK__": tripo_mask_path.name,
    }


def _mime_for_extension(ext: str) -> str:
    e = (ext or "").lower()
    if e in (".jpg", ".jpeg"):
        return "image/jpeg"
    if e == ".webp":
        return "image/webp"
    if e == ".png":
        return "image/png"
    return "application/octet-stream"


def _decode_base64_to_bytes(encoded: Optional[str], label: str) -> bytes:
    if not encoded:
        raise ValueError(f"Missing base64 image for {label}")
    raw = encoded.strip()
    match = _DATA_URI_PATTERN.match(raw)
    if match:
        raw = match.group("data")
    try:
        return base64.b64decode(raw, validate=True)
    except Exception as exc:
        raise ValueError(f"Invalid base64 image payload for {label}") from exc


def _write_bytes_to_file(data: bytes, target_path: Path) -> Path:
    target_path.parent.mkdir(parents=True, exist_ok=True)
    target_path.write_bytes(data)
    if not target_path.is_file() or target_path.stat().st_size == 0:
        raise RuntimeError(f"Failed to write non-empty image file: {target_path.resolve()}")
    return target_path


def _get_comfy_input_dir() -> Path:
    configured = os.getenv("COMFY_INPUT_DIR")
    if not configured:
        raise ValueError("COMFY_INPUT_DIR must point at ComfyUI's input directory for refine mode")

    path = Path(configured).expanduser().resolve()
    path.mkdir(parents=True, exist_ok=True)
    return path


def _guess_extension(encoded: Optional[str]) -> str:
    if not encoded:
        return ".png"

    match = _DATA_URI_PATTERN.match(encoded.strip())
    if not match:
        return ".png"

    return _IMAGE_EXTENSIONS.get(match.group("mime").lower(), ".png")


def _write_base64_image(encoded: Optional[str], target_path: Path) -> Path:
    data = _decode_base64_to_bytes(encoded, target_path.name)
    return _write_bytes_to_file(data, target_path)
