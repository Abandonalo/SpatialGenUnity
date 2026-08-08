"""Request/response models shared with the Unity client.

Field names here are the wire contract. Their C# counterparts live in
``Assets/SpatialGeneration/RunTime/Generation/Backends/RouterProtocol.cs`` and
``.../Refinement/MultiView/MultiViewRefinementRequest.cs``; change both together.
"""

from typing import Literal, Optional

from pydantic import BaseModel, ConfigDict, Field, model_validator

# Denoise applied inside the refinement mask. High because masked pixels are being
# replaced rather than nudged; at low denoise the inpaint returns a blurred copy of the
# original. Mirrors RefinementDefaults.Denoise in Unity.
REFINEMENT_DENOISE: float = 0.95

# Artifact and geometry negatives only. Deliberately excludes "dull", "desaturated" and
# "washed out": those steer inpainted pixels toward neutral gray and fight prompts that
# ask for a specific bright colour.
REFINEMENT_DEFAULT_NEGATIVE: str = "blur, low quality, noise, jpeg artifacts, distorted, deformed"

# Appended to every generation prompt. Deliberately short.
#
# A single-view lifter can only reconstruct what the image shows, so the source image wants
# to be one complete object seen straight on. The temptation is to spell that out at
# length, and it backfires: the user's subject is often a single token, so a long cue block
# outvotes it and the model renders the cues instead of the subject. Measured on SD 1.5
# with the prompt "house":
#
#   "symmetrical front elevation, orthographic product render"  -> architectural blueprints
#                                                                  on graph paper
#   "studio photograph, seamless backdrop, sharp focus, ..."    -> abstract framed boxes
#   the three clauses below                                    -> recognisable houses
#
# Everything else that used to live here now lives in the negative prompt, where it steers
# the result without competing for the subject's share of the attention budget.
GENERATION_ISOLATION_CUES: str = (
    "front view, whole object centered in frame, isolated on a plain white background"
)

GENERATION_DEFAULT_NEGATIVE: str = (
    # Drawing styles. Architectural subjects collapse into these without being warned off.
    "blueprint, technical drawing, architectural drawing, elevation drawing, floor plan, "
    "line art, sketch, diagram, graph paper, grid, monochrome, isometric, "
    # Degenerate "isolated object" readings: an empty frame instead of the subject.
    "abstract sculpture, empty frame, shelf, display case, "
    # Wrong viewpoint: the largest cause of a malformed reconstruction.
    "side view, profile view, three-quarter view, angled view, rear view, back view, "
    "tilted, rotated object, perspective distortion, foreshortening, "
    # Anything that survives background removal becomes geometry.
    "ground, floor, pedestal, base, platform, stand, table, "
    "cast shadow, contact shadow, shadow on ground, reflection, "
    "background scene, environment, sky, clouds, landscape, grass, road, room, wall, "
    # Foreground planting is the worst of these: it sits in front of the subject, so it
    # survives background removal and gets welded onto the mesh.
    "flowers, blossoms, bushes, shrubs, hedge, foliage, plants, trees, garden, vegetation, "
    # Extra subjects get fused into one mesh.
    "multiple objects, duplicate, clutter, occlusion, cropped, cut off, partial object, "
    # Optical effects the lifter reads as shape.
    "close-up, zoomed in, depth of field, bokeh, vignette, motion blur, "
    "blurry, noisy, jpeg artifacts, messy geometry, "
    "text, watermark, signature"
)

GenerationModel = Literal["hunyuan_2_1", "tripo_sr"]

RunMode = Literal["generate", "generate_hunyuan", "refine_inpaint_only", "tripo_from_rgb"]


class RunRequest(BaseModel):
    """A single ComfyUI graph execution, after mode and defaults have been resolved."""

    mode: RunMode

    positive_prompt: str = Field(min_length=1)
    negative_prompt: str = ""

    rgb_image: Optional[str] = None
    depth_image: Optional[str] = None
    mask_image: Optional[str] = None

    # Edge conditioning for ControlNet Canny. When absent, the injector falls back to
    # rgb_image so the graph's LoadImage still resolves.
    canny_image: Optional[str] = None

    seed: int
    steps: int = Field(gt=0)
    cfg: float = Field(gt=0)
    denoise: float = REFINEMENT_DENOISE
    sampler: str = "euler"

    # Latent size for graphs that expose an EmptyLatentImage.
    width: int = 512
    height: int = 512

    # ControlNet weights. Depth carries the spatial constraint, so it leads; Canny only
    # sharpens the silhouette and is easy to overdrive into hard outlines.
    #
    # Tuned down from 0.8/0.4. A proxy's depth map is a featureless primitive, so at high
    # strength the model reproduces the primitive rather than the subject inside it. At
    # these values the silhouette still lands on the proxy's footprint within a few pixels
    # while the subject stays recognisable.
    controlnet_depth_strength: float = 0.45
    controlnet_canny_strength: float = 0.2

    tripo_model: str = Field(min_length=1)
    geometry_resolution: int = Field(gt=0)
    tripo_threshold: float

    # Region of the staged RGB to crop before reconstruction, in pixels. Refinement sets
    # this to the selection's footprint so the lifted mesh covers the edited region and
    # not the surrounding context the cameras captured for blending.
    crop_width: Optional[int] = None
    crop_height: Optional[int] = None
    crop_x: Optional[int] = None
    crop_y: Optional[int] = None

    @model_validator(mode="after")
    def _check_required_images(self) -> "RunRequest":
        required: dict[str, tuple[str, ...]] = {
            "generate": ("depth_image",),
            "generate_hunyuan": (),
            "refine_inpaint_only": ("rgb_image", "depth_image", "mask_image"),
            "tripo_from_rgb": ("rgb_image",),
        }

        missing = [name for name in required[self.mode] if not getattr(self, name)]
        if missing:
            raise ValueError(f"{self.mode} mode requires: {', '.join(missing)}")
        return self


class GenerationParams(BaseModel):
    seed: int = -1
    steps: int = 30
    cfg: float = 7.0
    sampler: str = "euler"
    width: int = 512
    height: int = 512


class ProxyVolume(BaseModel):
    """The authored primitive a generation run is producing an asset for."""

    id: str = ""
    role: str = "occupy"
    shape: str = "box"
    label: Optional[str] = None
    position: dict = Field(default_factory=dict)
    rotation: dict = Field(default_factory=dict)
    size: dict = Field(default_factory=dict)


class GenerateRequest(BaseModel):
    """``POST /generate``: one asset for one occupy proxy."""

    model_config = ConfigDict(extra="allow")

    request_id: str = ""
    mode: Literal["generate"] = "generate"

    prompt: str = ""
    negative_prompt: str = ""

    # Conditioning captured in Unity. depth_image and edges_image drive ControlNet;
    # rgb_image switches the pipeline to image-to-3D when the proxy has a reference photo.
    rgb_image: Optional[str] = None
    depth_image: Optional[str] = None
    edges_image: Optional[str] = None
    mask_image: Optional[str] = None

    generation_model: Optional[str] = None
    geometry_resolution: Optional[int] = None
    tripo_threshold: Optional[float] = None

    proxy: Optional[ProxyVolume] = None
    generation: GenerationParams = Field(default_factory=GenerationParams)


class HealthStatus(BaseModel):
    """``GET /health``: the router's own state plus the ComfyUI it depends on."""

    ok: bool
    comfy_url: str
    comfy_reachable: bool
    detail: str = ""


class SubmitResponse(BaseModel):
    prompt_id: str


class OutputFile(BaseModel):
    filename: str
    subfolder: str = ""
    type: str = "output"


class RunResult(BaseModel):
    """``GET /result/{prompt_id}``."""

    prompt_id: str
    status: Literal["running", "success", "error"]
    completed: bool
    files: list[OutputFile] = Field(default_factory=list)
    message: str = ""


class ViewPayload(BaseModel):
    """One canonical view. ``viewType`` mirrors the Unity enum: Front/Left/Right/Top."""

    viewType: str = ""
    width: int = 0
    height: int = 0
    rgbBase64: str = ""
    depthBase64: str = ""
    edgesBase64: str = ""
    maskBase64: str = ""


class MultiViewRefinementRequest(BaseModel):
    """``POST /refine``: a region-scoped edit captured from four canonical cameras."""

    requestId: str = ""
    sessionId: str = ""
    mode: Literal["refine"] = "refine"

    positivePrompt: str = ""
    negativePrompt: str = ""

    # One seed for every view. Different seeds per view would produce four mutually
    # inconsistent inpaints of the same surface.
    seed: int = -1

    steps: int = Field(default=30, gt=0)
    cfg: float = Field(default=8.0, gt=0)
    denoise: float = REFINEMENT_DENOISE

    reconstructionView: str = "Front"

    # Selection footprint in the reconstruction view, in viewport UV with origin
    # bottom-left (Unity's convention).
    cropMinX: float = 0.0
    cropMinY: float = 0.0
    cropMaxX: float = 1.0
    cropMaxY: float = 1.0

    views: list[ViewPayload] = Field(default_factory=list)


class RefinedViewResult(BaseModel):
    viewType: str = ""
    refinedImageBase64: str = ""


class MultiViewRefinementResponse(BaseModel):
    requestId: str = ""
    refinedViews: list[RefinedViewResult] = Field(default_factory=list)
    meshBase64: str = ""

    success: bool
    # "queued" / "running" while the job is in flight, "success" or "error" once settled.
    status: str = ""
    errorMessage: str = ""
