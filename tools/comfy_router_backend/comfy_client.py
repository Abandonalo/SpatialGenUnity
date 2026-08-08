"""Thin HTTP client for the ComfyUI server this router drives."""

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

from .models import OutputFile, RunResult


_IMAGE_EXTENSIONS = {".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff"}
_MESH_EXTENSIONS = {".glb", ".gltf", ".obj", ".fbx", ".ply", ".stl"}


@dataclass
class ComfyRunResult:
    prompt_id: str
    image_base64: Optional[str]
    mesh_base64: Optional[str]


def send_to_comfy(graph: Dict[str, Any]) -> ComfyRunResult:
    """Submits a graph, waits for it, and returns its first image and mesh output."""
    prompt_id = submit_prompt(graph)
    history_entry = wait_for_completion(prompt_id)

    outputs = _extract_outputs(history_entry)
    if not outputs:
        raise RuntimeError(f"ComfyUI finished prompt {prompt_id} without downloadable outputs")

    image = _first_with_extension(outputs, _IMAGE_EXTENSIONS)
    mesh = _first_with_extension(outputs, _MESH_EXTENSIONS)
    if image is None and mesh is None:
        raise RuntimeError(f"ComfyUI prompt {prompt_id} produced only unsupported output types")

    return ComfyRunResult(
        prompt_id=prompt_id,
        image_base64=_download_base64(image) if image else None,
        mesh_base64=_download_base64(mesh) if mesh else None,
    )


def submit_prompt(graph: Dict[str, Any]) -> str:
    response = _request_json("POST", _url("/prompt"), payload={"prompt": graph})
    prompt_id = response.get("prompt_id")
    if not prompt_id:
        raise RuntimeError(f"ComfyUI /prompt response has no prompt_id: {response}")
    return str(prompt_id)


def wait_for_completion(prompt_id: str) -> Dict[str, Any]:
    # Generous by default: a 512-latent inpaint followed by a reconstruction pass runs for
    # minutes on a shared Colab GPU, and much longer on CPU.
    timeout_seconds = float(os.getenv("COMFY_TIMEOUT_SECONDS", "900"))
    poll_interval = float(os.getenv("COMFY_POLL_INTERVAL", "1.0"))
    deadline = time.monotonic() + timeout_seconds

    while time.monotonic() < deadline:
        entry = _history_entry(prompt_id)
        if entry:
            status = entry.get("status") or {}
            if str(status.get("status_str") or "").lower() == "error":
                raise RuntimeError(f"ComfyUI prompt {prompt_id} failed: {_message(entry)}")
            if status.get("completed") or _extract_outputs(entry):
                return entry

        time.sleep(poll_interval)

    raise TimeoutError(f"Timed out waiting for ComfyUI prompt {prompt_id}")


def build_run_result(prompt_id: str) -> RunResult:
    """Flattens ComfyUI's history payload into the shape Unity polls for."""
    entry = _history_entry(prompt_id)
    if entry is None:
        return RunResult(prompt_id=prompt_id, status="running", completed=False)

    status = entry.get("status") or {}
    files = _extract_outputs(entry)

    if str(status.get("status_str") or "").lower() == "error":
        return RunResult(
            prompt_id=prompt_id,
            status="error",
            completed=True,
            files=files,
            message=_message(entry),
        )

    completed = bool(status.get("completed")) or bool(files)
    return RunResult(
        prompt_id=prompt_id,
        status="success" if completed else "running",
        completed=completed,
        files=files,
    )


def probe_comfy() -> tuple[bool, str]:
    """Whether ComfyUI answers, and why not when it does not.

    Short timeout on purpose: this backs a health check the user is waiting on, so a
    down ComfyUI should report quickly rather than hang the editor for 30 seconds.
    """
    try:
        _request_bytes("GET", _url("/system_stats"), timeout_seconds=3.0)
        return True, ""
    except Exception as exc:  # noqa: BLE001 - the reason is the whole point here
        return False, str(exc)


def get_history(prompt_id: str) -> Dict[str, Any]:
    return _request_json("GET", _url(f"/history/{urllib.parse.quote(prompt_id)}"))


def get_view_bytes(filename: str, subfolder: str = "", output_type: str = "output") -> bytes:
    params = {"filename": filename, "type": output_type}
    if subfolder:
        params["subfolder"] = subfolder
    return _request_bytes("GET", f"{_url('/view')}?{urllib.parse.urlencode(params)}")


def guess_media_type(filename: str) -> str:
    guessed, _ = mimetypes.guess_type(filename)
    return guessed or "application/octet-stream"


def upload_input_image(filename: str, data: bytes, *, content_type: str = "image/png") -> str:
    """POSTs image bytes to ComfyUI so LoadImage can resolve them by name.

    Preferred over writing into COMFY_INPUT_DIR: the Comfy server may use a different
    input root than this process, which surfaces as LoadImage raising FileNotFoundError.
    """
    body, headers = _multipart_body(filename, data, content_type)
    timeout = float(os.getenv("COMFY_HTTP_TIMEOUT_SECONDS", "120"))
    raw = b""

    for path in ("/upload/image", "/api/upload/image"):
        url = _url(path)
        request = urllib.request.Request(url, data=body, headers=headers, method="POST")
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                raw = response.read()
            break
        except urllib.error.HTTPError as exc:
            if exc.code == 404 and path == "/upload/image":
                continue  # Older builds only expose the /api prefix.
            raise RuntimeError(f"HTTP {exc.code} from {url}: {exc.read().decode('utf-8', 'replace')}") from exc
        except urllib.error.URLError as exc:
            raise RuntimeError(f"Failed to reach {url}: {exc.reason}") from exc
    else:
        raise RuntimeError("ComfyUI upload failed: no working upload endpoint")

    decoded = json.loads(raw.decode("utf-8"))
    name = decoded.get("name") if isinstance(decoded, dict) else None
    if not isinstance(name, str) or not name.strip():
        raise RuntimeError(f"upload/image response has no usable 'name': {decoded!r}")
    return name


def _history_entry(prompt_id: str) -> Optional[Dict[str, Any]]:
    history = get_history(prompt_id)
    entry = history.get(prompt_id) if isinstance(history, dict) else None
    return entry if isinstance(entry, dict) else None


def _extract_outputs(payload: Dict[str, Any]) -> List[OutputFile]:
    """Collects every ``{filename, subfolder, type}`` triple anywhere in the payload.

    ComfyUI nests outputs differently per node type, so this walks the whole tree rather
    than assuming a shape.
    """
    found: list[OutputFile] = []
    seen: set[tuple[str, str, str]] = set()

    def visit(value: Any) -> None:
        if isinstance(value, dict):
            filename = value.get("filename")
            if isinstance(filename, str):
                ref = OutputFile(
                    filename=filename,
                    subfolder=str(value.get("subfolder") or ""),
                    type=str(value.get("type") or "output"),
                )
                key = (ref.filename, ref.subfolder, ref.type)
                if key not in seen:
                    seen.add(key)
                    found.append(ref)

            for inner in value.values():
                visit(inner)
        elif isinstance(value, list):
            for inner in value:
                visit(inner)

    visit(payload)
    return found


def _first_with_extension(refs: Iterable[OutputFile], extensions: set[str]) -> Optional[OutputFile]:
    for ref in refs:
        if os.path.splitext(ref.filename)[1].lower() in extensions:
            return ref
    return None


def _download_base64(ref: OutputFile) -> str:
    return base64.b64encode(get_view_bytes(ref.filename, ref.subfolder, ref.type)).decode("ascii")


def _message(entry: Dict[str, Any]) -> str:
    status = entry.get("status") or {}
    value = status.get("messages") or entry.get("messages")
    if isinstance(value, str):
        return value
    return json.dumps(value) if value is not None else "unknown ComfyUI error"


def _multipart_body(filename: str, data: bytes, content_type: str) -> tuple[bytes, Dict[str, str]]:
    boundary = f"spatialgen{uuid.uuid4().hex}"

    def field(name: str, value: str) -> bytes:
        return (
            f"--{boundary}\r\n"
            f'Content-Disposition: form-data; name="{name}"\r\n\r\n'
            f"{value}\r\n"
        ).encode("utf-8")

    body = (
        field("type", "input")
        + field("overwrite", "true")
        + (
            f"--{boundary}\r\n"
            f'Content-Disposition: form-data; name="image"; filename="{filename}"\r\n'
            f"Content-Type: {content_type}\r\n\r\n"
        ).encode("utf-8")
        + data
        + b"\r\n"
        + f"--{boundary}--\r\n".encode("utf-8")
    )
    return body, {"Content-Type": f"multipart/form-data; boundary={boundary}"}


def _url(path: str) -> str:
    return f"{os.getenv('COMFY_BASE_URL', 'http://127.0.0.1:8188').rstrip('/')}{path}"


def _request_json(method: str, url: str, payload: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
    data = _request_bytes(method, url, payload)
    try:
        decoded = json.loads(data.decode("utf-8"))
    except json.JSONDecodeError as exc:
        raise RuntimeError(f"Expected JSON from {url}, got: {data[:200]!r}") from exc

    if not isinstance(decoded, dict):
        raise RuntimeError(f"Expected a JSON object from {url}, got {type(decoded).__name__}")
    return decoded


def _request_bytes(
    method: str,
    url: str,
    payload: Optional[Dict[str, Any]] = None,
    timeout_seconds: Optional[float] = None,
) -> bytes:
    body = None
    headers: Dict[str, str] = {}
    if payload is not None:
        body = json.dumps(payload).encode("utf-8")
        headers = {"Content-Type": "application/json", "Accept": "application/json"}

    request = urllib.request.Request(url, data=body, headers=headers, method=method)
    timeout = timeout_seconds or float(os.getenv("COMFY_HTTP_TIMEOUT_SECONDS", "30"))

    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.read()
    except urllib.error.HTTPError as exc:
        raise RuntimeError(f"HTTP {exc.code} from {url}: {exc.read().decode('utf-8', 'replace')}") from exc
    except urllib.error.URLError as exc:
        raise RuntimeError(f"Failed to reach {url}: {exc.reason}") from exc
