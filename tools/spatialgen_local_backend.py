#!/usr/bin/env python3
import argparse
import json
import os
import sys
import time
from dataclasses import dataclass
from typing import Any, Dict, List, Optional

# -----------------------------
# Utilities
# -----------------------------

def atomic_write_text(path: str, text: str) -> None:
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(text)
    os.replace(tmp, path)  # atomic on macOS/Linux/Windows

def safe_read_json(path: str, max_retries: int = 10, retry_delay: float = 0.05) -> Optional[Dict[str, Any]]:
    """
    Unity may still be writing request.json when we see it.
    Retry a bit if JSON is incomplete.
    """
    for _ in range(max_retries):
        try:
            with open(path, "r", encoding="utf-8") as f:
                return json.load(f)
        except json.JSONDecodeError:
            time.sleep(retry_delay)
        except FileNotFoundError:
            return None
    return None

def now_ts() -> str:
    return time.strftime("%Y-%m-%d %H:%M:%S")

# -----------------------------
# Core "generation" logic
# Replace this later with ComfyUI / real pipeline
# -----------------------------

def map_constraint_to_object(constraint: Dict[str, Any]) -> Dict[str, Any]:
    shape = (constraint.get("shape") or "").lower()
    if shape == "sphere":
        primitive = "sphere"
    elif shape == "cylinder":
        primitive = "cylinder"
    else:
        primitive = "cube"

    pos = constraint.get("position") or {"x": 0, "y": 0, "z": 0}
    size = constraint.get("size") or {"x": 1, "y": 1, "z": 1}

    return {
        "primitive": primitive,
        "position": pos,
        "size": size,
    }

def generate_response(request_json: Dict[str, Any]) -> Dict[str, Any]:
    """
    Minimal backend: echoes constraints into primitive objects.
    Later: call ComfyUI / TripoSR / mesh pipeline, then return objects/meshes.
    """
    constraints = request_json.get("constraints") or []
    objects = [map_constraint_to_object(c) for c in constraints]
    return {"objects": objects}

# -----------------------------
# Watcher loop
# -----------------------------

@dataclass
class Config:
    handoff_dir: str
    request_file: str
    response_file: str
    poll_interval: float
    archive_requests: bool

def ensure_dir(path: str) -> None:
    os.makedirs(path, exist_ok=True)

def archive_request(handoff_dir: str, request_path: str, request_id: str) -> None:
    archive_dir = os.path.join(handoff_dir, "archive")
    ensure_dir(archive_dir)
    base = f"request_{request_id}.json"
    dest = os.path.join(archive_dir, base)
    try:
        os.replace(request_path, dest)
    except Exception:
        # Fallback: keep it if move fails
        pass

def main() -> int:
    parser = argparse.ArgumentParser(description="SpatialGen Local File Backend (Unity handoff)")
    parser.add_argument("--project-root", default=".", help="Unity project root (folder containing Assets/)")
    parser.add_argument("--handoff-dir", default="SpatialGenHandoff", help="Handoff folder relative to project root")
    parser.add_argument("--request", default="request.json", help="Request filename")
    parser.add_argument("--response", default="response.json", help="Response filename")
    parser.add_argument("--poll", type=float, default=0.25, help="Poll interval seconds")
    parser.add_argument("--archive", action="store_true", help="Move processed requests to handoff/archive/")
    parser.add_argument("--oneshot", action="store_true", help="Process once and exit (useful for debugging)")
    args = parser.parse_args()

    project_root = os.path.abspath(args.project_root)
    handoff_dir = os.path.join(project_root, args.handoff_dir)
    ensure_dir(handoff_dir)

    cfg = Config(
        handoff_dir=handoff_dir,
        request_file=args.request,
        response_file=args.response,
        poll_interval=args.poll,
        archive_requests=args.archive,
    )

    request_path = os.path.join(cfg.handoff_dir, cfg.request_file)
    response_path = os.path.join(cfg.handoff_dir, cfg.response_file)
    state_path = os.path.join(cfg.handoff_dir, ".last_request_id")

    last_request_id = None
    if os.path.exists(state_path):
        try:
            with open(state_path, "r", encoding="utf-8") as f:
                last_request_id = f.read().strip() or None
        except Exception:
            last_request_id = None

    print(f"[{now_ts()}] SpatialGen Local Backend started")
    print(f"  Project root : {project_root}")
    print(f"  Handoff dir  : {cfg.handoff_dir}")
    print(f"  Watching     : {request_path}")
    print(f"  Writing      : {response_path}")
    print(f"  Poll         : {cfg.poll_interval}s")
    print("  Ctrl+C to stop\n")

    try:
        while True:
            if os.path.exists(request_path):
                req = safe_read_json(request_path)
                if req is None:
                    time.sleep(cfg.poll_interval)
                    continue

                request_id = (req.get("request_id") or "").strip()
                if not request_id:
                    # No request_id: still process, but stamp one
                    request_id = f"noid_{int(time.time())}"

                # Avoid re-processing the same request forever
                if last_request_id == request_id and os.path.exists(response_path):
                    if args.oneshot:
                        return 0
                    time.sleep(cfg.poll_interval)
                    continue

                print(f"[{now_ts()}] Processing request_id={request_id}")

                resp = generate_response(req)
                print(f"[LocalFileBackend] Writing response to: {response_path}")
                atomic_write_text(response_path, json.dumps(resp, indent=2))

                last_request_id = request_id
                atomic_write_text(state_path, request_id)

                if cfg.archive_requests:
                    archive_request(cfg.handoff_dir, request_path, request_id)

                print(f"[{now_ts()}] Wrote response ({len(resp.get('objects', []))} objects)\n")

                if args.oneshot:
                    return 0

            time.sleep(cfg.poll_interval)

    except KeyboardInterrupt:
        print(f"\n[{now_ts()}] Stopped")
        return 0

if __name__ == "__main__":
    raise SystemExit(main())
