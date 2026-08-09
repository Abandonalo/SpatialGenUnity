"""FastAPI router in front of ComfyUI.

Unity talks only to this service. It owns graph selection, prompt policy and the choice
of 3D lifter, so the editor stays a thin client that submits conditioning images and
collects files. The same app runs locally and inside the Colab notebook.
"""

import os
import random

from fastapi import FastAPI, HTTPException, Query
from fastapi.responses import Response

from .comfy_client import (
    build_run_result,
    get_view_bytes,
    guess_media_type,
    probe_comfy,
    submit_prompt,
)
from .jobs import RefinementJobs
from .models import (
    GENERATION_DEFAULT_NEGATIVE,
    GENERATION_DEFAULT_STYLE,
    GENERATION_ISOLATION_CUES,
    GenerateRequest,
    HealthStatus,
    MultiViewRefinementRequest,
    MultiViewRefinementResponse,
    RunRequest,
    RunResult,
    SubmitResponse,
)
from .multi_view_router import handle_refine
from .router import route_request


app = FastAPI(title="SpatialGen Comfy Router", version="1.0.0")

_refinements = RefinementJobs()


@app.get("/health", response_model=HealthStatus)
def health() -> HealthStatus:
    """Reports the router *and* the ComfyUI behind it.

    The router answering says nothing about whether ComfyUI is up, and a run that only
    fails at submit time is far harder to diagnose than one refused up front.
    """
    comfy_url = os.getenv("COMFY_BASE_URL", "http://127.0.0.1:8188")
    reachable, detail = probe_comfy()
    return HealthStatus(
        ok=True,
        comfy_url=comfy_url,
        comfy_reachable=reachable,
        detail=detail,
    )


@app.post("/generate", response_model=SubmitResponse)
def generate(req: GenerateRequest) -> SubmitResponse:
    """Queues one asset generation and returns the ComfyUI prompt id to poll."""
    try:
        graph = route_request(_to_run_request(req))
        return SubmitResponse(prompt_id=submit_prompt(graph))
    except Exception as exc:  # noqa: BLE001 - surfaced to Unity as a 400 with the reason
        raise HTTPException(status_code=400, detail=str(exc)) from exc


@app.get("/result/{prompt_id}", response_model=RunResult)
def result(prompt_id: str) -> RunResult:
    try:
        return build_run_result(prompt_id)
    except Exception as exc:  # noqa: BLE001 - a failed lookup is a failed run to the client
        return RunResult(
            prompt_id=prompt_id, status="error", completed=True, message=str(exc)
        )


@app.post("/refine", response_model=MultiViewRefinementResponse)
def refine(req: MultiViewRefinementRequest) -> MultiViewRefinementResponse:
    """Starts a region refinement. Poll ``GET /refine/{requestId}`` for the result."""
    if not req.requestId:
        raise HTTPException(status_code=400, detail="requestId is required")

    return _refinements.start(
        req.requestId,
        lambda: handle_refine(
            req,
            tripo_model=_tripo_model(),
            geometry_resolution=_geometry_resolution(),
            tripo_threshold=_tripo_threshold(),
        ),
    )


@app.get("/refine/{request_id}", response_model=MultiViewRefinementResponse)
def refine_status(request_id: str) -> MultiViewRefinementResponse:
    return _refinements.get(request_id)


@app.get("/view")
def view(
    filename: str = Query(...),
    subfolder: str = Query(""),
    type: str = Query("output"),
) -> Response:
    """Proxies a ComfyUI output file so Unity only needs one reachable host."""
    return Response(content=get_view_bytes(filename, subfolder, type), media_type=guess_media_type(filename))


def _to_run_request(req: GenerateRequest) -> RunRequest:
    """Applies prompt policy and picks the graph mode for the requested lifter."""
    prompt = req.prompt.strip()
    if not prompt:
        raise ValueError("prompt is required")

    model = (req.generation_model or "").strip().lower()
    uses_hunyuan = model not in ("tripo_sr", "triposr", "tripo")

    # Hunyuan reconstructs from a reference image directly; the TripoSR path first
    # diffuses one under ControlNet, so it needs the isolation cues that keep the
    # generated image a single object on a plain background.
    # Style leads so it modifies the subject; the isolation cues trail behind it.
    style = GENERATION_DEFAULT_STYLE if req.style is None else req.style.strip()
    subject = f"{style} {prompt}".strip() if style else prompt
    positive = subject if uses_hunyuan else f"{subject}, {GENERATION_ISOLATION_CUES}"
    negative = ", ".join(part for part in (req.negative_prompt.strip(), GENERATION_DEFAULT_NEGATIVE) if part)

    return RunRequest(
        mode="generate_hunyuan" if uses_hunyuan else "generate",
        positive_prompt=positive,
        negative_prompt=negative,
        rgb_image=req.rgb_image or None,
        depth_image=req.depth_image or None,
        canny_image=req.edges_image or None,
        mask_image=req.mask_image or None,
        seed=req.generation.seed if req.generation.seed >= 0 else random.randint(0, 2**31 - 1),
        steps=req.generation.steps,
        cfg=req.generation.cfg,
        sampler=req.generation.sampler,
        width=req.generation.width,
        height=req.generation.height,
        tripo_model=_tripo_model(),
        geometry_resolution=req.geometry_resolution or _geometry_resolution(),
        tripo_threshold=req.tripo_threshold if req.tripo_threshold is not None else _tripo_threshold(),
    )


def _tripo_model() -> str:
    return os.getenv("SPATIALGEN_TRIPO_MODEL", "TripoSRmodel.ckpt")


def _geometry_resolution() -> int:
    return int(os.getenv("SPATIALGEN_GEOMETRY_RESOLUTION", "512"))


def _tripo_threshold() -> float:
    return float(os.getenv("SPATIALGEN_TRIPO_THRESHOLD", "25"))


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(
        app,
        host="0.0.0.0",
        port=int(os.getenv("SPATIALGEN_BACKEND_PORT", "8001")),
        reload=False,
    )
