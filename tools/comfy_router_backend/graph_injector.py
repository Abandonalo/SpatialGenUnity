"""Fills a graph template's ``__PLACEHOLDER__`` slots from a :class:`RunRequest`.

Images cannot be inlined into a ComfyUI graph, so every image a mode needs is uploaded to
ComfyUI first and the placeholder is replaced with the filename the server returned.
"""

import base64
import copy
import io
import os
import re
import sys
import uuid
from pathlib import Path
from typing import Any, Dict, Optional, Tuple

from PIL import Image

from .models import RunRequest


_DATA_URI_PATTERN = re.compile(r"^data:(?P<mime>[\w.+-]+/[\w.+-]+);base64,(?P<data>.+)$", re.IGNORECASE)
_PLACEHOLDER_PATTERN = re.compile(r"__[A-Z0-9_]+__")
_EXTENSION_BY_MIME = {
    "image/png": ".png",
    "image/jpeg": ".jpg",
    "image/jpg": ".jpg",
    "image/webp": ".webp",
}

# Images each mode stages, as {placeholder: (RunRequest attribute, required)}.
_IMAGES_BY_MODE: Dict[str, Dict[str, Tuple[str, bool]]] = {
    "generate": {
        "__DEPTH_IMAGE__": ("depth_image", True),
        "__CANNY_IMAGE__": ("canny_image", True),
        "__MASK_IMAGE__": ("mask_image", False),
    },
    "generate_hunyuan": {
        # Optional: Hunyuan runs text-to-3D without a reference photo.
        "__RGB_IMAGE__": ("rgb_image", False),
    },
    "refine_inpaint_only": {
        "__RGB_IMAGE__": ("rgb_image", True),
        "__MASK_IMAGE__": ("mask_image", True),
        "__DEPTH_IMAGE__": ("depth_image", True),
        "__CANNY_IMAGE__": ("canny_image", True),
    },
    "tripo_from_rgb": {
        "__RGB_IMAGE__": ("rgb_image", True),
    },
}

# Compatibility with workflows authored in the ComfyUI editor, which have no placeholders
# and identify their prompt nodes only by id. Override when your graph numbers differ.
_POSITIVE_PROMPT_NODE_IDS = set(filter(None, os.getenv("COMFY_POSITIVE_PROMPT_NODE_IDS", "55").split(",")))
_NEGATIVE_PROMPT_NODE_IDS = set(filter(None, os.getenv("COMFY_NEGATIVE_PROMPT_NODE_IDS", "56").split(",")))

_IMAGE_LOADER_CLASSES = {"LoadImage", "Hy3D21LoadImageWithTransparency"}


def inject_params(graph: Dict[str, Any], req: RunRequest) -> Dict[str, Any]:
    """Binds a graph template to a request.

    Templates in ``tools/graphs`` use ``__PLACEHOLDER__`` slots. Workflows exported from
    the ComfyUI editor (the Hunyuan3D graph, for instance) have none, so those are bound
    by node class instead — same values, discovered rather than declared.
    """
    replacements = _build_replacements(req)
    injected = _replace_placeholders(copy.deepcopy(graph), replacements)

    if "__POSITIVE_PROMPT__" not in _collect_placeholders(graph):
        _bind_by_node_class(injected, req, replacements.get("__RGB_IMAGE__"))

    unresolved = sorted(_collect_placeholders(injected))
    if unresolved:
        raise ValueError(f"Unresolved graph placeholders: {', '.join(unresolved)}")

    return injected


def _bind_by_node_class(graph: Dict[str, Any], req: RunRequest, staged_image: Optional[str]) -> None:
    """Writes request values into a placeholder-free workflow, in place."""
    for node_id, node in graph.items():
        if not isinstance(node, dict):
            continue

        class_type = node.get("class_type")
        inputs = node.setdefault("inputs", {})

        if class_type == "CLIPTextEncode":
            title = str((node.get("_meta") or {}).get("title", "")).lower()
            is_negative = node_id in _NEGATIVE_PROMPT_NODE_IDS or (
                node_id not in _POSITIVE_PROMPT_NODE_IDS and "negative" in title
            )
            inputs["text"] = req.negative_prompt if is_negative else req.positive_prompt

        elif class_type in ("KSampler", "KSamplerAdvanced"):
            inputs["noise_seed" if "noise_seed" in inputs else "seed"] = req.seed
            inputs["steps"] = req.steps
            inputs["cfg"] = req.cfg
            if req.sampler:
                inputs["sampler_name"] = req.sampler

        elif class_type == "EmptyLatentImage":
            inputs["width"] = req.width
            inputs["height"] = req.height

        elif class_type in _IMAGE_LOADER_CLASSES and staged_image:
            inputs["image"] = staged_image
            inputs["upload"] = "image"


def _build_replacements(req: RunRequest) -> Dict[str, Any]:
    replacements: Dict[str, Any] = {
        "__POSITIVE_PROMPT__": req.positive_prompt,
        "__NEGATIVE_PROMPT__": req.negative_prompt,
        # Some templates name the prompt slots after the mesh stage they feed.
        "__MESH_PROMPT__": req.positive_prompt,
        "__MESH_NEG_PROMPT__": req.negative_prompt,
        "__SEED__": req.seed,
        "__STEPS__": req.steps,
        "__CFG__": req.cfg,
        "__DENOISE__": req.denoise,
        "__CN_DEPTH_STRENGTH__": req.controlnet_depth_strength,
        "__CN_CANNY_STRENGTH__": req.controlnet_canny_strength,
        "__TRIPOSR_MODEL__": req.tripo_model,
        "__GEOMETRY_RESOLUTION__": req.geometry_resolution,
        "__TRIPOSR_THRESHOLD__": req.tripo_threshold,
        "__CHECKPOINT__": os.getenv("COMFY_CHECKPOINT", "v1-5-pruned-emaonly.safetensors"),
        # Must match an entry in ControlNetLoader's dropdown, i.e. the path relative to
        # models/controlnet/ including any subfolder.
        "__CONTROLNET_DEPTH__": os.getenv(
            "COMFY_CONTROLNET_DEPTH", "controlnet-depth/control_v11p_sd15_depth.pth"
        ),
        "__CONTROLNET_CANNY__": os.getenv(
            "COMFY_CONTROLNET_CANNY", "controlnet-canny/control_v11p_sd15_canny.pth"
        ),
        "__INPAINT_CHECKPOINT__": os.getenv("COMFY_INPAINT_CHECKPOINT", "sd-v1-5-inpainting.ckpt"),
        **_crop_replacements(req),
    }

    replacements.update(_stage_images(req))
    return replacements


def _crop_replacements(req: RunRequest) -> Dict[str, int]:
    """Crop rect applied before reconstruction.

    Refinement passes an explicit rect. Everything else falls back to the full staged
    image, which makes the crop node a no-op.
    """
    if req.crop_width and req.crop_height:
        crop_w, crop_h = int(req.crop_width), int(req.crop_height)
    else:
        crop_w, crop_h = _image_size(req.rgb_image) or (512, 512)

    return {
        "__CROP_WIDTH__": crop_w,
        "__CROP_HEIGHT__": crop_h,
        "__CROP_X__": int(req.crop_x or 0),
        "__CROP_Y__": int(req.crop_y or 0),
    }


def _stage_images(req: RunRequest) -> Dict[str, str]:
    """Uploads every image this mode needs and returns {placeholder: comfy filename}."""
    wanted = _IMAGES_BY_MODE.get(req.mode, {})
    if not wanted:
        return {}

    run_token = uuid.uuid4().hex
    staged: Dict[str, str] = {}

    for placeholder, (attribute, required) in wanted.items():
        encoded = getattr(req, attribute, None)

        # Canny falls back to the other conditioning images so a graph wired for edges
        # still runs when the client did not send an edge map.
        if not encoded and attribute == "canny_image":
            encoded = req.rgb_image or req.depth_image

        if not encoded:
            if required:
                raise ValueError(f"{req.mode} mode requires an image for {attribute}")
            continue

        data = _decode_base64(encoded, attribute)
        extension = _guess_extension(encoded)
        staged[placeholder] = _upload(f"{run_token}_{attribute}{extension}", data, extension)

    # TripoSR's reference_mask input wants a solid-white image over the crop region: the
    # background was already removed upstream, so any tighter mask only carves holes in
    # the reconstruction.
    if "__TRIPO_SOLID_MASK__" not in staged:
        width, height = _tripo_mask_size(req)
        staged["__TRIPO_SOLID_MASK__"] = _upload(
            f"{run_token}_tripo_solid_mask.png", _solid_white_png(width, height), ".png"
        )

    return staged


def _upload(filename: str, data: bytes, extension: str) -> str:
    """Uploads to ComfyUI, falling back to its input directory if the endpoint is absent.

    Uploading is preferred over writing to disk because the Comfy server may resolve a
    different input root than this process, which used to surface as LoadImage failing
    with FileNotFoundError.
    """
    if os.getenv("COMFY_REFINE_USE_UPLOAD", "1").strip().lower() not in ("0", "false", "no"):
        try:
            from .comfy_client import upload_input_image

            return upload_input_image(filename, data, content_type=_mime_for_extension(extension))
        except Exception as exc:  # noqa: BLE001 - any upload failure should fall back
            print(
                f"[comfy_router_backend] Upload of {filename} failed ({exc!r}); "
                "falling back to COMFY_INPUT_DIR.",
                file=sys.stderr,
            )

    target = _comfy_input_dir() / filename
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(data)
    if not target.is_file() or target.stat().st_size == 0:
        raise RuntimeError(f"Failed to write image: {target}")
    return target.name


def _tripo_mask_size(req: RunRequest) -> Tuple[int, int]:
    if req.crop_width and req.crop_height:
        return int(req.crop_width), int(req.crop_height)
    return _image_size(req.rgb_image) or (512, 512)


def _solid_white_png(width: int, height: int) -> bytes:
    buffer = io.BytesIO()
    Image.new("RGB", (max(1, width), max(1, height)), (255, 255, 255)).save(buffer, format="PNG")
    return buffer.getvalue()


def _replace_placeholders(value: Any, replacements: Dict[str, Any]) -> Any:
    if isinstance(value, dict):
        return {key: _replace_placeholders(inner, replacements) for key, inner in value.items()}
    if isinstance(value, list):
        return [_replace_placeholders(item, replacements) for item in value]
    if not isinstance(value, str):
        return value

    # An exact match keeps the replacement's type, so numeric slots stay numeric.
    if value in replacements:
        return replacements[value]

    result = value
    for placeholder, replacement in replacements.items():
        if placeholder in result:
            result = result.replace(placeholder, str(replacement))
    return result


def _collect_placeholders(value: Any) -> set[str]:
    if isinstance(value, dict):
        return set().union(*(_collect_placeholders(inner) for inner in value.values())) if value else set()
    if isinstance(value, list):
        return set().union(*(_collect_placeholders(item) for item in value)) if value else set()
    if isinstance(value, str):
        return set(_PLACEHOLDER_PATTERN.findall(value))
    return set()


def _decode_base64(encoded: str, label: str) -> bytes:
    raw = encoded.strip()
    match = _DATA_URI_PATTERN.match(raw)
    if match:
        raw = match.group("data")

    try:
        return base64.b64decode(raw, validate=True)
    except Exception as exc:  # noqa: BLE001 - surfaced to the caller with context
        raise ValueError(f"Invalid base64 image payload for {label}") from exc


def _image_size(encoded: Optional[str]) -> Optional[Tuple[int, int]]:
    if not encoded:
        return None
    try:
        with Image.open(io.BytesIO(_decode_base64(encoded, "size probe"))) as image:
            return image.size
    except Exception:  # noqa: BLE001 - probing is best-effort
        return None


def _guess_extension(encoded: Optional[str]) -> str:
    if not encoded:
        return ".png"
    match = _DATA_URI_PATTERN.match(encoded.strip())
    return _EXTENSION_BY_MIME.get(match.group("mime").lower(), ".png") if match else ".png"


def _mime_for_extension(extension: str) -> str:
    return {
        ".jpg": "image/jpeg",
        ".jpeg": "image/jpeg",
        ".webp": "image/webp",
        ".png": "image/png",
    }.get((extension or "").lower(), "application/octet-stream")


def _comfy_input_dir() -> Path:
    configured = os.getenv("COMFY_INPUT_DIR")
    if not configured:
        raise ValueError("COMFY_INPUT_DIR must point at ComfyUI's input directory")

    path = Path(configured).expanduser().resolve()
    path.mkdir(parents=True, exist_ok=True)
    return path
