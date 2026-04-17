import os
import random
from typing import Optional

from fastapi import FastAPI, HTTPException, Query
from fastapi.responses import Response

from .comfy_client import (
    build_proxy_result,
    get_history,
    get_view_bytes,
    guess_media_type,
    send_to_comfy,
    submit_prompt,
)
from .models import (
    ProxyGenerateRequest,
    RefinementRequestModel,
    RefinementResponseModel,
    RunRequest,
    RunResponse,
)
from .router import route_request


app = FastAPI(title="SpatialGen Comfy Router", version="0.1.0")


@app.get("/health")
def health() -> dict:
    return {"ok": True}


@app.post("/run", response_model=RunResponse)
def run(req: RunRequest) -> RunResponse:
    try:
        graph = route_request(req)
        result = send_to_comfy(graph)
        return RunResponse(
            success=True,
            image_base64=result.image_base64,
            mesh_base64=result.mesh_base64,
        )
    except Exception as exc:
        return RunResponse(
            success=False,
            error=str(exc),
        )


@app.post("/generate")
def generate(req: ProxyGenerateRequest) -> dict:
    try:
        run_request = _proxy_request_to_run_request(req)
        graph = route_request(run_request)
        prompt_id = submit_prompt(graph)
        return {
            "prompt_id": prompt_id,
            "id": prompt_id,
        }
    except Exception as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc


@app.post("/refine", response_model=RefinementResponseModel)
def refine(req: RefinementRequestModel) -> RefinementResponseModel:
    try:
        run_request = RunRequest(
            mode="refine",
            positive_prompt=_build_refinement_prompt(req.globalPrompt, req.localPrompt),
            negative_prompt="",
            rgb_image=req.rgbImageBase64,
            depth_image=req.depthImageBase64,
            mask_image=req.maskImageBase64,
            seed=_normalize_seed(-1),
            steps=max(1, req.steps),
            cfg=max(0.0, req.cfgScale),
            tripo_model=_default_tripo_model(),
            geometry_resolution=_default_geometry_resolution(),
            tripo_threshold=_default_tripo_threshold(),
        )
        graph = route_request(run_request)
        result = send_to_comfy(graph)
        return RefinementResponseModel(
            requestId=req.requestId,
            refinedImageBase64=result.image_base64 or "",
            meshBase64=result.mesh_base64 or "",
            success=True,
            errorMessage="",
        )
    except Exception as exc:
        return RefinementResponseModel(
            requestId=req.requestId,
            refinedImageBase64="",
            meshBase64="",
            success=False,
            errorMessage=str(exc),
        )


@app.get("/result/{prompt_id}")
def result(prompt_id: str) -> dict:
    try:
        return build_proxy_result(prompt_id)
    except Exception as exc:
        return {
            "prompt_id": prompt_id,
            "status": "error",
            "completed": True,
            "files": [],
            "images": [],
            "meshes": [],
            "message": str(exc),
            "exception_message": str(exc),
        }


@app.get("/history/{prompt_id}")
def history(prompt_id: str) -> dict:
    return get_history(prompt_id)


@app.get("/view")
def view(
    filename: str = Query(...),
    subfolder: str = Query(""),
    type: str = Query("output"),
) -> Response:
    data = get_view_bytes(filename, subfolder, type)
    return Response(content=data, media_type=guess_media_type(filename))


def _proxy_request_to_run_request(req: ProxyGenerateRequest) -> RunRequest:
    mode = req.mode or _infer_mode(req)
    positive_prompt = (req.positive_prompt or req.prompt or "").strip()
    if not positive_prompt:
        raise ValueError("positive_prompt or prompt is required")

    return RunRequest(
        mode=mode,
        positive_prompt=positive_prompt,
        negative_prompt=req.negative_prompt or "",
        rgb_image=_first_non_empty(req.rgb_image, _asset_image_base64(req)),
        depth_image=req.depth_image,
        mask_image=req.mask_image,
        seed=_normalize_seed(req.generation.seed),
        steps=req.generation.steps,
        cfg=req.generation.cfg,
        tripo_model=req.tripo_model or _default_tripo_model(),
        geometry_resolution=req.geometry_resolution or _default_geometry_resolution(),
        tripo_threshold=req.tripo_threshold if req.tripo_threshold is not None else _default_tripo_threshold(),
    )


def _infer_mode(req: ProxyGenerateRequest) -> str:
    if any((_asset_image_base64(req), req.rgb_image, req.depth_image, req.mask_image)):
        return "refine"
    return "generate"


def _build_refinement_prompt(global_prompt: str, local_prompt: str) -> str:
    global_prompt = (global_prompt or "").strip()
    local_prompt = (local_prompt or "").strip()
    if global_prompt and local_prompt:
        return f"{global_prompt}, {local_prompt}"
    return local_prompt or global_prompt


def _asset_image_base64(req: ProxyGenerateRequest) -> Optional[str]:
    if req.asset_image is None:
        return None
    return req.asset_image.image_base64


def _first_non_empty(*values: Optional[str]) -> Optional[str]:
    for value in values:
        if value:
            return value
    return None


def _normalize_seed(seed: int) -> int:
    return seed if seed >= 0 else random.randint(0, 2**31 - 1)


def _default_tripo_model() -> str:
    return os.getenv("SPATIALGEN_TRIPO_MODEL", "TripoSRmodel.ckpt")


def _default_geometry_resolution() -> int:
    return int(os.getenv("SPATIALGEN_GEOMETRY_RESOLUTION", "512"))


def _default_tripo_threshold() -> float:
    return float(os.getenv("SPATIALGEN_TRIPO_THRESHOLD", "25"))


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(
        "tools.comfy_router_backend.app:app",
        host="0.0.0.0",
        port=int(os.getenv("SPATIALGEN_BACKEND_PORT", "8001")),
        reload=False,
    )
