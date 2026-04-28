import base64
import copy
import json
import os
import re
import time
import uuid
from pathlib import Path
from typing import Any, Dict, Optional

from .models import RunRequest

_DEBUG_LOG_PATH = "/Users/alo/SpatialGenUnity/.cursor/debug-b26376.log"


def _agent_debug_log(payload: Dict[str, Any]) -> None:
    with open(_DEBUG_LOG_PATH, "a", encoding="utf-8") as handle:
        handle.write(json.dumps(payload, default=str) + "\n")


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

    # region agent log
    try:
        n4 = injected.get("4") if isinstance(injected.get("4"), dict) else {}
        n13 = injected.get("13") if isinstance(injected.get("13"), dict) else {}
        n130 = injected.get("130") if isinstance(injected.get("130"), dict) else {}
        ckpt = (n4.get("inputs") or {}).get("ckpt_name")
        cn_depth = (n13.get("inputs") or {}).get("control_net_name")
        cn_canny = (n130.get("inputs") or {}).get("control_net_name")
        _agent_debug_log(
            {
                "sessionId": "b26376",
                "runId": "pre-fix",
                "hypothesisId": "H1",
                "location": "graph_injector.py:inject_params",
                "message": "resolved ckpt and controlnet for Comfy graph",
                "data": {
                    "mode": getattr(req, "mode", None),
                    "ckpt_name": ckpt,
                    "control_net_depth_name": cn_depth,
                    "control_net_canny_name": cn_canny,
                    "comfy_checkpoint_env_set": bool(os.getenv("COMFY_CHECKPOINT")),
                    "comfy_controlnet_depth_env_set": bool(os.getenv("COMFY_CONTROLNET_DEPTH")),
                    "comfy_controlnet_canny_env_set": bool(os.getenv("COMFY_CONTROLNET_CANNY")),
                    "ckpt_filename_suggests_sdxl": bool(
                        ckpt and isinstance(ckpt, str) and ("xl" in ckpt.lower() or "sd_xl" in ckpt.lower())
                    ),
                    "controlnet_depth_suggests_sdxl": bool(
                        cn_depth and isinstance(cn_depth, str) and ("xl" in cn_depth.lower() or "sdxl" in cn_depth.lower() or "xinsir" in cn_depth.lower())
                    ),
                },
                "timestamp": int(time.time() * 1000),
            }
        )
    except Exception:
        pass
    # endregion agent log

    return injected


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
        "__CROP_WIDTH__": req.crop_width or 512,
        "__CROP_HEIGHT__": req.crop_height or 512,
        "__CROP_X__": req.crop_x if req.crop_x is not None else 0,
        "__CROP_Y__": req.crop_y if req.crop_y is not None else 0,
        # Names must appear in ControlNetLoader’s list under models/controlnet/ (subdir layout matches Comfy’s UI).
        "__CONTROLNET_DEPTH__": os.getenv(
            "COMFY_CONTROLNET_DEPTH",
            "controlnet-depth/control_v11p_sd15_depth.pth",
        ),
        "__CONTROLNET_CANNY__": os.getenv(
            "COMFY_CONTROLNET_CANNY",
            "controlnet-canny/control_v11p_sd15_canny.pth",
        ),
    }

    checkpoint = os.getenv("COMFY_CHECKPOINT")
    if checkpoint:
        replacements["__CHECKPOINT__"] = checkpoint

    if req.mode == "refine":
        replacements.update(_stage_refine_images(req))

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


def _stage_refine_images(req: RunRequest) -> Dict[str, str]:
    input_root = _get_comfy_input_dir()
    run_token = uuid.uuid4().hex

    rgb_path = _write_base64_image(req.rgb_image, input_root / f"{run_token}_rgb{_guess_extension(req.rgb_image)}")
    mask_path = _write_base64_image(req.mask_image, input_root / f"{run_token}_mask{_guess_extension(req.mask_image)}")
    depth_path = _write_base64_image(req.depth_image, input_root / f"{run_token}_depth{_guess_extension(req.depth_image)}")
    canny_src = req.canny_image if req.canny_image else req.rgb_image
    canny_path = _write_base64_image(canny_src, input_root / f"{run_token}_canny{_guess_extension(canny_src)}")

    return {
        "__RGB_IMAGE__": rgb_path.name,
        "__MASK_IMAGE__": mask_path.name,
        "__DEPTH_IMAGE__": depth_path.name,
        "__CANNY_IMAGE__": canny_path.name,
    }


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
    if not encoded:
        raise ValueError(f"Missing base64 image for {target_path.name}")

    raw = encoded.strip()
    match = _DATA_URI_PATTERN.match(raw)
    if match:
        raw = match.group("data")

    try:
        payload = base64.b64decode(raw, validate=True)
    except Exception as exc:
        raise ValueError(f"Invalid base64 image payload for {target_path.name}") from exc

    target_path.write_bytes(payload)
    return target_path
