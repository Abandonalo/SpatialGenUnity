#!/usr/bin/env bash
# Starts the SpatialGen router in front of a locally running ComfyUI.
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

if [ ! -d ".venv" ]; then
  echo "Missing .venv. Create it with: python3 -m venv .venv && .venv/bin/pip install -r requirements.txt"
  exit 1
fi

source ".venv/bin/activate"

# Find ComfyUI. The Comfy Desktop app serves on 8000; a standalone `python main.py`
# defaults to 8188. Probe both rather than making the user remember which is running.
if [ -z "${COMFY_BASE_URL:-}" ]; then
  for candidate in http://127.0.0.1:8000 http://127.0.0.1:8188; do
    if curl -sf -m 2 -o /dev/null "$candidate/system_stats" 2>/dev/null; then
      COMFY_BASE_URL="$candidate"
      echo "Found ComfyUI at $COMFY_BASE_URL"
      break
    fi
  done
  # Nothing answered: assume Comfy Desktop's port so a later launch lands somewhere sane.
  COMFY_BASE_URL="${COMFY_BASE_URL:-http://127.0.0.1:8000}"
fi
export COMFY_BASE_URL
export COMFY_CHECKPOINT="${COMFY_CHECKPOINT:-v1-5-pruned-emaonly.safetensors}"
export COMFY_INPUT_CHECKPOINT="${COMFY_INPAINT_CHECKPOINT:-sd-v1-5-inpainting.ckpt}"

# Only used if ComfyUI's /upload/image endpoint is unavailable; see graph_injector._upload.
export COMFY_INPUT_DIR="${COMFY_INPUT_DIR:-$ROOT_DIR/.comfy_inputs}"
mkdir -p "$COMFY_INPUT_DIR"

export SPATIALGEN_BACKEND_PORT="${SPATIALGEN_BACKEND_PORT:-8001}"
export SPATIALGEN_TRIPO_MODEL="${SPATIALGEN_TRIPO_MODEL:-TripoSRmodel.ckpt}"
export SPATIALGEN_GEOMETRY_RESOLUTION="${SPATIALGEN_GEOMETRY_RESOLUTION:-512}"
export SPATIALGEN_TRIPO_THRESHOLD="${SPATIALGEN_TRIPO_THRESHOLD:-25}"

# Some venvs ship a uvloop build that fails on import (AttributeError: module 'uvloop' has
# no attribute 'new_event_loop'); the stdlib loop avoids it.
export UVICORN_LOOP="${UVICORN_LOOP:-asyncio}"

exec uvicorn tools.comfy_router_backend.app:app \
  --host 0.0.0.0 --port "$SPATIALGEN_BACKEND_PORT" --loop "$UVICORN_LOOP"
