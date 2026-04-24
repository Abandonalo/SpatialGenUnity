from typing import Any, Dict, Literal, Optional

from pydantic import BaseModel, ConfigDict, Field, model_validator


class RunRequest(BaseModel):
    mode: Literal["generate", "refine"]

    positive_prompt: str = Field(min_length=1)
    negative_prompt: str = ""

    rgb_image: Optional[str] = None
    depth_image: Optional[str] = None
    mask_image: Optional[str] = None

    seed: int
    steps: int = Field(gt=0)
    cfg: float = Field(gt=0)
    denoise: float = 0.55

    tripo_model: str = Field(min_length=1)
    geometry_resolution: int = Field(gt=0)
    tripo_threshold: float

    @model_validator(mode="after")
    def validate_refine_inputs(self) -> "RunRequest":
        if self.mode != "refine":
            return self

        missing = [
            name
            for name, value in (
                ("rgb_image", self.rgb_image),
                ("depth_image", self.depth_image),
                ("mask_image", self.mask_image),
            )
            if not value
        ]
        if missing:
            joined = ", ".join(missing)
            raise ValueError(f"refine mode requires: {joined}")
        return self


class RunResponse(BaseModel):
    success: bool
    image_base64: Optional[str] = None
    mesh_base64: Optional[str] = None
    error: Optional[str] = None


class ProxyGenerationParams(BaseModel):
    seed: int = -1
    steps: int = 30
    cfg: float = 7.0
    sampler: str = "euler"
    width: int = 1024
    height: int = 1024


class ProxyAssetImage(BaseModel):
    file_name: Optional[str] = None
    image_base64: Optional[str] = None


class ProxyGenerateRequest(BaseModel):
    model_config = ConfigDict(extra="allow")

    request_id: str = ""
    mode: Optional[Literal["generate", "refine"]] = None

    prompt: str = ""
    positive_prompt: Optional[str] = None
    negative_prompt: str = ""

    input_mode: Optional[str] = None
    workflow: Optional[Dict[str, Any]] = None
    constraint_set_json: Optional[str] = None
    proxy: Optional[Dict[str, Any]] = None
    asset_image: Optional[ProxyAssetImage] = None
    generation: ProxyGenerationParams = Field(default_factory=ProxyGenerationParams)

    rgb_image: Optional[str] = None
    depth_image: Optional[str] = None
    mask_image: Optional[str] = None

    tripo_model: Optional[str] = None
    geometry_resolution: Optional[int] = None
    tripo_threshold: Optional[float] = None


class ProxyResultResponse(BaseModel):
    prompt_id: str
    status: str
    completed: bool
    files: list[Dict[str, str]] = Field(default_factory=list)
    images: list[Dict[str, str]] = Field(default_factory=list)
    meshes: list[Dict[str, str]] = Field(default_factory=list)
    message: Optional[str] = None
    exception_message: Optional[str] = None


class RefinementRequestModel(BaseModel):
    requestId: str = ""
    globalPrompt: str = ""
    localPrompt: str = ""
    rgbImageBase64: str = ""
    depthImageBase64: str = ""
    maskImageBase64: str = ""
    denoiseStrength: float = 0.6
    steps: int = 20
    cfgScale: float = 8.0
    sessionId: str = ""


class RefinementResponseModel(BaseModel):
    requestId: str = ""
    refinedImageBase64: str = ""
    meshBase64: str = ""
    success: bool
    errorMessage: str = ""


class ViewPayload(BaseModel):
    # viewType mirrors the Unity ViewType enum: Front / Left / Right / Top.
    viewType: str = ""
    width: int = 0
    height: int = 0
    rgbBase64: str = ""
    depthBase64: str = ""
    maskBase64: str = ""


class MultiViewRefinementRequestModel(BaseModel):
    requestId: str = ""
    sessionId: str = ""
    mode: Literal["refine"] = "refine"

    positivePrompt: str = ""
    negativePrompt: str = ""

    # Deterministic seed shared by every view. Negative values are
    # resolved to a random seed in the router once so all views still
    # share the same value for the duration of a request.
    seed: int = -1

    steps: int = Field(default=20, gt=0)
    cfg: float = Field(default=8.0, gt=0)
    denoise: float = 1.0

    reconstructionView: str = "Front"
    views: list[ViewPayload] = Field(default_factory=list)


class RefinedViewResultModel(BaseModel):
    viewType: str = ""
    refinedImageBase64: str = ""


class MultiViewRefinementResponseModel(BaseModel):
    requestId: str = ""
    refinedViews: list[RefinedViewResultModel] = Field(default_factory=list)
    meshBase64: str = ""
    success: bool
    errorMessage: str = ""
