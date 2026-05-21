import json
import os
from pathlib import Path
from typing import Any, Dict


_REPO_ROOT = Path(__file__).resolve().parents[2]
_DEFAULT_GRAPH_DIR = _REPO_ROOT / "tools" / "graphs"


def get_graph_dir() -> Path:
    configured = os.getenv("SPATIALGEN_GRAPH_DIR")
    return Path(configured).expanduser().resolve() if configured else _DEFAULT_GRAPH_DIR


def load_graph(graph_name: str) -> Dict[str, Any]:
    graph_path = get_graph_dir() / graph_name
    if not graph_path.is_file():
        raise FileNotFoundError(f"Graph template not found: {graph_path}")

    with graph_path.open("r", encoding="utf-8") as handle:
        payload = json.load(handle)

    if not isinstance(payload, dict):
        raise ValueError(f"Graph template must deserialize to an object: {graph_path}")

    return payload
