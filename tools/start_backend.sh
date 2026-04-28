#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

if [ ! -d ".venv" ]; then
  echo "Missing .venv. Create it first with: python3 -m venv .venv"
  exit 1
fi

source ".venv/bin/activate"

export COMFY_BASE_URL="${COMFY_BASE_URL:-http://127.0.0.1:8188}"
export COMFY_CHECKPOINT="${COMFY_CHECKPOINT:-sd-v1-5-inpainting.ckpt}"
DEFAULT_COMFY_INPUT_DIR="/Users/alo/ComfyUI/input"
if [ -z "${COMFY_INPUT_DIR:-}" ] || [ "${COMFY_INPUT_DIR:-}" = "/your/comfy/input" ] || [ "${COMFY_INPUT_DIR:-}" = "/Applications/ComfyUI.app/Contents/Resources/ComfyUI/input" ]; then
  export COMFY_INPUT_DIR="$DEFAULT_COMFY_INPUT_DIR"
else
  export COMFY_INPUT_DIR
fi
export SPATIALGEN_BACKEND_PORT="${SPATIALGEN_BACKEND_PORT:-8001}"
export SPATIALGEN_TRIPO_MODEL="${SPATIALGEN_TRIPO_MODEL:-TripoSRmodel.ckpt}"
export SPATIALGEN_GEOMETRY_RESOLUTION="${SPATIALGEN_GEOMETRY_RESOLUTION:-512}"
export SPATIALGEN_TRIPO_THRESHOLD="${SPATIALGEN_TRIPO_THRESHOLD:-25}"

exec uvicorn tools.comfy_router_backend.app:app --host 0.0.0.0 --port "$SPATIALGEN_BACKEND_PORT"
