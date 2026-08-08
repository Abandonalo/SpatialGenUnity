"""Loads ComfyUI API-format graph templates."""

import json
import os
from pathlib import Path
from typing import Any, Dict, List


_REPO_ROOT = Path(__file__).resolve().parents[2]
_DEFAULT_GRAPH_DIR = _REPO_ROOT / "tools" / "graphs"


def graph_search_paths() -> List[Path]:
    """Directories searched for a graph, in priority order.

    Graphs that belong to the project live in ``tools/graphs``. Graphs that ship with a
    custom-node pack (the Hunyuan3D workflow, for instance) are installed into ComfyUI's
    own workflow folder instead, so that is searched as a fallback rather than copied
    into the repo where it would drift.
    """
    paths: List[Path] = []

    configured = os.getenv("SPATIALGEN_GRAPH_DIR")
    if configured:
        paths.append(Path(configured).expanduser().resolve())

    paths.append(_DEFAULT_GRAPH_DIR)

    comfy_root = os.getenv("COMFY_ROOT")
    if comfy_root:
        paths.append(Path(comfy_root).expanduser().resolve() / "user" / "default" / "workflows")

    return paths


def load_graph(graph_name: str) -> Dict[str, Any]:
    searched = graph_search_paths()

    for directory in searched:
        graph_path = directory / graph_name
        if not graph_path.is_file():
            continue

        with graph_path.open("r", encoding="utf-8") as handle:
            payload = json.load(handle)

        if not isinstance(payload, dict):
            raise ValueError(f"Graph template must be a JSON object: {graph_path}")
        if "nodes" in payload and "links" in payload:
            raise ValueError(
                f"{graph_path} is a ComfyUI UI-format workflow. "
                "Re-export it with 'Save (API format)' before using it here."
            )

        # Underscore-prefixed top-level keys are documentation for whoever reads the
        # template. ComfyUI validates every top-level key as a node, so they are dropped.
        return {key: value for key, value in payload.items() if not key.startswith("_")}

    locations = ", ".join(str(path) for path in searched)
    raise FileNotFoundError(f"Graph template '{graph_name}' not found in: {locations}")
