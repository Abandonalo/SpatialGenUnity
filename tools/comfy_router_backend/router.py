"""Maps a resolved :class:`RunRequest` onto the ComfyUI graph that serves it."""

from .graph_injector import inject_params
from .graph_loader import load_graph
from .models import RunRequest


_MODE_TO_GRAPH: dict[str, str] = {
    # Text/depth-to-image, then TripoSR lifting. Ships in tools/graphs.
    "generate": "generation.json",
    # Hunyuan3D 2.1 lifting. Resolved from ComfyUI's own workflow folder when it is not
    # in tools/graphs, because the Hunyuan graph is installed alongside its custom nodes.
    "generate_hunyuan": "generation_hunyuan.json",
    "refine_inpaint_only": "refinement_inpaint_only.json",
    "tripo_from_rgb": "refinement_tripo_from_rgb.json",
}


def route_request(req: RunRequest) -> dict:
    try:
        graph_name = _MODE_TO_GRAPH[req.mode]
    except KeyError as exc:
        raise ValueError(f"Unknown run mode: {req.mode}") from exc

    return inject_params(load_graph(graph_name), req)
