import base64
import json
import mimetypes
import os
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from dataclasses import dataclass
from typing import Any, Dict, Iterable, List, Optional


_IMAGE_EXTENSIONS = {".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff"}
_MESH_EXTENSIONS = {".glb", ".gltf", ".obj", ".fbx", ".ply", ".stl"}


@dataclass
class OutputRef:
    filename: str
    subfolder: str = ""
    type: str = "output"


@dataclass
class ComfyRunResult:
    prompt_id: str
    image_base64: Optional[str]
    mesh_base64: Optional[str]


def send_to_comfy(graph: Dict[str, Any]) -> ComfyRunResult:
    prompt_id = submit_prompt(graph)
    history_entry = wait_for_completion(prompt_id)
    output_refs = _extract_output_refs(history_entry)
    if not output_refs:
        raise RuntimeError(f"ComfyUI completed prompt {prompt_id} without downloadable outputs")

    image_ref = _first_matching_ref(output_refs, _IMAGE_EXTENSIONS)
    mesh_ref = _first_matching_ref(output_refs, _MESH_EXTENSIONS)
    if image_ref is None and mesh_ref is None:
        raise RuntimeError(f"ComfyUI prompt {prompt_id} produced unsupported outputs only")
    return ComfyRunResult(
        prompt_id=prompt_id,
        image_base64=_download_output_base64(image_ref) if image_ref else None,
        mesh_base64=_download_output_base64(mesh_ref) if mesh_ref else None,
    )


def submit_prompt(graph: Dict[str, Any]) -> str:
    response = _request_json(
        "POST",
        _url("/prompt"),
        payload={"prompt": graph},
    )
    prompt_id = response.get("prompt_id")
    if not prompt_id:
        raise RuntimeError(f"ComfyUI /prompt response missing prompt_id: {response}")
    return str(prompt_id)


def wait_for_completion(prompt_id: str) -> Dict[str, Any]:
    # Default raised from 180s -> 900s: the multi-view refinement composes a
    # 2x2 grid (1024x1024 latent) which is roughly 4x more expensive than the
    # original 512x512 single-view inpaint, and TripoSR runs on top of that.
    # 180s is too tight for local Mac/MPS setups; 15 minutes is generous and
    # users can still override via env if they want stricter SLAs.
    timeout_seconds = float(os.getenv("COMFY_TIMEOUT_SECONDS", "900"))
    poll_interval = float(os.getenv("COMFY_POLL_INTERVAL", "1.0"))
    started = time.monotonic()
    deadline = started + timeout_seconds

    while time.monotonic() < deadline:
        history = get_history(prompt_id)
        entry = history.get(prompt_id) if isinstance(history, dict) else None
        if entry:
            status = entry.get("status") or {}
            if (status.get("status_str") or "").lower() == "error":
                error_message = status.get("messages") or entry.get("messages") or "unknown ComfyUI error"
                raise RuntimeError(f"ComfyUI prompt {prompt_id} failed: {error_message}")

            if status.get("completed") or _extract_output_refs(entry):
                return entry

        time.sleep(poll_interval)

    raise TimeoutError(f"Timed out waiting for ComfyUI prompt {prompt_id}")


def get_history(prompt_id: str) -> Dict[str, Any]:
    return _request_json("GET", _url(f"/history/{urllib.parse.quote(prompt_id)}"))


def get_view_bytes(filename: str, subfolder: str = "", output_type: str = "output") -> bytes:
    params = {
        "filename": filename,
        "type": output_type,
    }
    if subfolder:
        params["subfolder"] = subfolder
    return _request_bytes("GET", f"{_url('/view')}?{urllib.parse.urlencode(params)}")


def guess_media_type(filename: str) -> str:
    guessed, _ = mimetypes.guess_type(filename)
    return guessed or "application/octet-stream"


def build_proxy_result(prompt_id: str) -> Dict[str, Any]:
    history = get_history(prompt_id)
    entry = history.get(prompt_id) if isinstance(history, dict) else None
    if not isinstance(entry, dict):
        return {
            "prompt_id": prompt_id,
            "status": "running",
            "completed": False,
            "files": [],
            "images": [],
            "meshes": [],
        }

    status = entry.get("status") or {}
    status_str = str(status.get("status_str") or "").lower()
    refs = _extract_output_refs(entry)
    images = [_serialize_output_ref(ref) for ref in refs if _is_image_ref(ref)]
    meshes = [_serialize_output_ref(ref) for ref in refs if _is_mesh_ref(ref)]
    files = [_serialize_output_ref(ref) for ref in refs]

    if status_str == "error":
        message = _stringify_message(status.get("messages") or entry.get("messages"))
        return {
            "prompt_id": prompt_id,
            "status": "error",
            "completed": True,
            "files": files,
            "images": images,
            "meshes": meshes,
            "message": message,
            "exception_message": message,
        }

    completed = bool(status.get("completed")) or bool(files)
    result_status = "success" if completed else "running"
    return {
        "prompt_id": prompt_id,
        "status": result_status,
        "completed": completed,
        "files": files,
        "images": images,
        "meshes": meshes,
    }


def _extract_output_refs(payload: Dict[str, Any]) -> List[OutputRef]:
    refs: list[OutputRef] = []
    seen: set[tuple[str, str, str]] = set()

    def visit(value: Any) -> None:
        if isinstance(value, dict):
            filename = value.get("filename")
            if isinstance(filename, str):
                ref = OutputRef(
                    filename=filename,
                    subfolder=str(value.get("subfolder") or ""),
                    type=str(value.get("type") or "output"),
                )
                key = (ref.filename, ref.subfolder, ref.type)
                if key not in seen:
                    refs.append(ref)
                    seen.add(key)

            for inner in value.values():
                visit(inner)
            return

        if isinstance(value, list):
            for inner in value:
                visit(inner)

    visit(payload)
    return refs


def _first_matching_ref(refs: Iterable[OutputRef], extensions: set[str]) -> Optional[OutputRef]:
    for ref in refs:
        suffix = os.path.splitext(ref.filename)[1].lower()
        if suffix in extensions:
            return ref
    return None


def _download_output_base64(ref: OutputRef) -> str:
    data = get_view_bytes(ref.filename, ref.subfolder, ref.type)
    return base64.b64encode(data).decode("ascii")


def _serialize_output_ref(ref: OutputRef) -> Dict[str, str]:
    return {
        "filename": ref.filename,
        "subfolder": ref.subfolder,
        "type": ref.type,
    }


def _is_image_ref(ref: OutputRef) -> bool:
    return os.path.splitext(ref.filename)[1].lower() in _IMAGE_EXTENSIONS


def _is_mesh_ref(ref: OutputRef) -> bool:
    return os.path.splitext(ref.filename)[1].lower() in _MESH_EXTENSIONS


def _stringify_message(value: Any) -> str:
    if isinstance(value, str):
        return value
    if value is None:
        return "unknown ComfyUI error"
    return json.dumps(value)


def _url(path: str) -> str:
    base_url = os.getenv("COMFY_BASE_URL", "http://127.0.0.1:8188").rstrip("/")
    return f"{base_url}{path}"


def upload_input_image(filename: str, data: bytes, *, content_type: str = "image/png") -> str:
    """POST PNG/JPEG bytes to ComfyUI's /upload/image so LoadImage resolves them.

    Prefer this over writing to COMFY_INPUT_DIR on disk: the Comfy server
    (especially ComfyUI.app) may use a different input root than the router
    process, which previously caused FileNotFoundError on node LoadImage.
    """
    body, headers = _multipart_upload_body(filename, data, content_type)
    raw: bytes = b""
    response_url = ""
    for upload_path in ("/upload/image", "/api/upload/image"):
        response_url = _url(upload_path)
        request = urllib.request.Request(response_url, data=body, headers=headers, method="POST")
        timeout = float(os.getenv("COMFY_HTTP_TIMEOUT_SECONDS", "120"))
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                raw = response.read()
            break
        except urllib.error.HTTPError as exc:
            detail = exc.read().decode("utf-8", errors="replace")
            if exc.code == 404 and upload_path == "/upload/image":
                continue
            raise RuntimeError(f"HTTP {exc.code} from {response_url}: {detail}") from exc
        except urllib.error.URLError as exc:
            raise RuntimeError(f"Failed to reach {response_url}: {exc.reason}") from exc
    else:
        raise RuntimeError("ComfyUI upload failed: no working upload endpoint (/upload/image, /api/upload/image)")

    try:
        decoded = json.loads(raw.decode("utf-8"))
    except json.JSONDecodeError as exc:
        raise RuntimeError(f"Expected JSON from upload, got: {raw[:200]!r}") from exc

    if not isinstance(decoded, dict):
        raise RuntimeError(f"upload/image returned non-object: {decoded!r}")
    name = decoded.get("name")
    if not isinstance(name, str) or not name.strip():
        raise RuntimeError(f"upload/image missing 'name' in response: {decoded!r}")
    return name


def _multipart_upload_body(filename: str, data: bytes, content_type: str) -> tuple[bytes, Dict[str, str]]:
    boundary = f"spatialgen{uuid.uuid4().hex}"

    def part_field(name: str, value: str) -> bytes:
        return (
            f"--{boundary}\r\n"
            f'Content-Disposition: form-data; name="{name}"\r\n'
            f"\r\n"
            f"{value}\r\n"
        ).encode("utf-8")

    def part_file(fname: str, raw: bytes, ctype: str) -> bytes:
        return (
            f"--{boundary}\r\n"
            f'Content-Disposition: form-data; name="image"; filename="{fname}"\r\n'
            f"Content-Type: {ctype}\r\n"
            f"\r\n"
        ).encode("utf-8") + raw + b"\r\n"

    body = (
        part_field("type", "input")
        + part_field("overwrite", "true")
        + part_file(filename, data, content_type)
        + f"--{boundary}--\r\n".encode("utf-8")
    )
    headers = {"Content-Type": f"multipart/form-data; boundary={boundary}"}
    return body, headers


def _request_json(method: str, url: str, payload: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
    data = _request_bytes(method, url, payload)
    try:
        decoded = json.loads(data.decode("utf-8"))
    except json.JSONDecodeError as exc:
        raise RuntimeError(f"Expected JSON from {url}, got: {data[:200]!r}") from exc

    if not isinstance(decoded, dict):
        raise RuntimeError(f"Expected JSON object from {url}, got: {type(decoded).__name__}")
    return decoded


def _request_bytes(method: str, url: str, payload: Optional[Dict[str, Any]] = None) -> bytes:
    body = None
    headers: Dict[str, str] = {}
    if payload is not None:
        body = json.dumps(payload).encode("utf-8")
        headers["Content-Type"] = "application/json"
        headers["Accept"] = "application/json"

    request = urllib.request.Request(url, data=body, headers=headers, method=method)
    timeout = float(os.getenv("COMFY_HTTP_TIMEOUT_SECONDS", "30"))

    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.read()
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"HTTP {exc.code} from {url}: {detail}") from exc
    except urllib.error.URLError as exc:
        raise RuntimeError(f"Failed to reach {url}: {exc.reason}") from exc
