#!/usr/bin/env bash
# Installs the project-owned Hunyuan3D-2mv ComfyUI adapter and its pinned Fast checkpoint.
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
NODE_SOURCE="$ROOT_DIR/tools/comfy_custom_nodes/SpatialGenHunyuan2MV"
MODEL_REVISION="3a761b539b29fe4ff64714813aa9560fd66f5de0"
WRAPPER_REVISION="2609efa38f6a98292476f714839b7c1e5f9b699a"
WRAPPER_URL="https://github.com/kijai/ComfyUI-Hunyuan3DWrapper.git"
MODEL_PATTERN="hunyuan3d-dit-v2-mv-fast/config.yaml"
WEIGHT_PATTERN="hunyuan3d-dit-v2-mv-fast/model.fp16.safetensors"

COMFY_DIR="${1:-${COMFY_ROOT:-}}"
if [ -z "$COMFY_DIR" ]; then
  for candidate in \
    "$HOME/ComfyUI" \
    "$HOME/ComfyUI-Installs/ComfyUI/ComfyUI"; do
    if [ -d "$candidate/custom_nodes" ]; then
      COMFY_DIR="$candidate"
      break
    fi
  done
fi

if [ -z "$COMFY_DIR" ] || [ ! -d "$COMFY_DIR/custom_nodes" ]; then
  echo "Could not find ComfyUI. Pass its root directory as the first argument or set COMFY_ROOT."
  exit 1
fi

NODE_TARGET="$COMFY_DIR/custom_nodes/SpatialGenHunyuan2MV"
if [ -e "$NODE_TARGET" ] && [ ! -L "$NODE_TARGET" ]; then
  echo "Refusing to replace existing non-symlink: $NODE_TARGET"
  exit 1
fi
ln -sfn "$NODE_SOURCE" "$NODE_TARGET"

PYTHON_BIN="${COMFY_PYTHON:-}"
if [ -z "$PYTHON_BIN" ] && [ -x "$COMFY_DIR/.venv/bin/python" ]; then
  PYTHON_BIN="$COMFY_DIR/.venv/bin/python"
fi
PYTHON_BIN="${PYTHON_BIN:-python3}"

WRAPPER_PARENT="$COMFY_DIR/spatialgen_vendor"
WRAPPER_TARGET="$WRAPPER_PARENT/ComfyUI-Hunyuan3DWrapper"
mkdir -p "$WRAPPER_PARENT"
if [ -e "$WRAPPER_TARGET" ] && [ ! -d "$WRAPPER_TARGET/.git" ]; then
  echo "Refusing to replace existing non-git wrapper: $WRAPPER_TARGET"
  exit 1
fi
if [ ! -d "$WRAPPER_TARGET/.git" ]; then
  git clone --filter=blob:none "$WRAPPER_URL" "$WRAPPER_TARGET"
fi
git -C "$WRAPPER_TARGET" fetch --depth 1 origin "$WRAPPER_REVISION"
git -C "$WRAPPER_TARGET" checkout --detach "$WRAPPER_REVISION"
"$PYTHON_BIN" -m pip install -r "$WRAPPER_TARGET/requirements.txt"

MODEL_DIR="$COMFY_DIR/models/hunyuan3d_2mv"
mkdir -p "$MODEL_DIR"
"$PYTHON_BIN" - "$MODEL_DIR" "$MODEL_REVISION" "$MODEL_PATTERN" "$WEIGHT_PATTERN" <<'PY'
import subprocess
import sys

target, revision, config_pattern, weight_pattern = sys.argv[1:]
try:
    from huggingface_hub import snapshot_download
except ImportError:
    subprocess.run([sys.executable, "-m", "pip", "install", "huggingface_hub>=0.27,<1"], check=True)
    from huggingface_hub import snapshot_download

snapshot_download(
    repo_id="tencent/Hunyuan3D-2mv",
    revision=revision,
    local_dir=target,
    allow_patterns=[config_pattern, weight_pattern, "LICENSE*", "NOTICE*"],
)
PY

echo "Installed SpatialGenHunyuan2MV into $COMFY_DIR"
echo "Restart ComfyUI, then use Check Backend Health in Unity."
