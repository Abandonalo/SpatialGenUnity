from .graph_injector import inject_params
from .graph_loader import load_graph
from .models import RunRequest


_MODE_TO_GRAPH = {
    "generate": "generation.json",
    "refine": "refinement.json",
    "refine_inpaint_only": "refinement_inpaint_only.json",
    "tripo_from_rgb": "refinement_tripo_from_rgb.json",
}


def route_request(req: RunRequest) -> dict:
    try:
        graph_name = _MODE_TO_GRAPH[req.mode]
    except KeyError as exc:
        raise ValueError("Invalid mode") from exc

    graph = load_graph(graph_name)
    return inject_params(graph, req)
