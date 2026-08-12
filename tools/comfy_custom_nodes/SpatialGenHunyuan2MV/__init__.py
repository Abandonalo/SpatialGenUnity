"""Minimal ComfyUI adapter for Hunyuan3D-2mv region reconstruction.

The large ComfyUI-3D-Pack is intentionally not a dependency.  This node reuses the
Hunyuan pipeline already shipped by ComfyUI-Hunyuan3DWrapper and exposes only the four
cardinal images and the controls SpatialGen needs.
"""

from __future__ import annotations

import gc
import os
import sys
from pathlib import Path
from typing import Any

import numpy as np
import torch
from PIL import Image

import folder_paths
import comfy.model_management as model_management

from .coordinates import apply_canonical_y_up


EXPECTED_MODEL = "hunyuan3d_2mv/hunyuan3d-dit-v2-mv-fast/model.fp16.safetensors"
MISSING_MODEL = f"<missing: {EXPECTED_MODEL}>"
MISSING_WRAPPER = "<unavailable: pinned Hunyuan wrapper>"


def _wrapper_root() -> Path | None:
    custom_nodes = Path(folder_paths.base_path) / "custom_nodes"
    preferred = Path(folder_paths.base_path) / "spatialgen_vendor" / "ComfyUI-Hunyuan3DWrapper"
    candidates = [preferred, *sorted(custom_nodes.glob("ComfyUI-Hunyuan3*"))]
    return next(
        (candidate for candidate in candidates
         if (candidate / "hy3dshape" / "hy3dshape" / "pipelines.py").is_file()),
        None,
    )


def _models() -> list[str]:
    if _wrapper_root() is None:
        return [MISSING_WRAPPER]
    root = Path(folder_paths.models_dir) / "hunyuan3d_2mv"
    if not root.is_dir():
        return [MISSING_MODEL]
    found = [
        str(path.relative_to(folder_paths.models_dir))
        for path in root.rglob("*.safetensors")
        if (path.parent / "config.yaml").is_file()
    ]
    return sorted(found) or [MISSING_MODEL]


def _load_hunyuan_symbols() -> tuple[Any, Any, Any, Any, Any]:
    candidate = _wrapper_root()
    if candidate is None:
        raise RuntimeError(
            "ComfyUI-Hunyuan3DWrapper is required. Install it, then restart ComfyUI."
        )
    path = str(candidate)
    if path not in sys.path:
        sys.path.insert(0, path)

    from hy3dshape.hy3dshape.pipelines import Hunyuan3DDiTFlowMatchingPipeline
    from hy3dshape.hy3dshape.postprocessors import (
        DegenerateFaceRemover,
        FaceReducer,
        FloaterRemover,
    )
    from hy3dshape.hy3dshape.rembg import BackgroundRemover

    return Hunyuan3DDiTFlowMatchingPipeline, BackgroundRemover, FloaterRemover, DegenerateFaceRemover, FaceReducer


def _tensor_to_subject(image: torch.Tensor, remover: Any) -> Image.Image:
    array = image[0].detach().float().cpu().numpy()
    array = np.clip(array[..., :3] * 255.0, 0, 255).astype(np.uint8)
    subject = remover(Image.fromarray(array, mode="RGB")).convert("RGBA")

    # Rembg occasionally retains disconnected pieces of the old scene.  Keep the central
    # component when OpenCV is available; the alpha image remains usable without it.
    alpha = np.asarray(subject.getchannel("A"))
    try:
        import cv2

        count, labels, stats, centroids = cv2.connectedComponentsWithStats(
            (alpha >= 16).astype(np.uint8), connectivity=8
        )
        if count > 1:
            h, w = alpha.shape
            centre = np.array([w * 0.5, h * 0.5])
            choices = range(1, count)
            chosen = min(
                choices,
                key=lambda i: np.linalg.norm(centroids[i] - centre) /
                max(1.0, float(stats[i, cv2.CC_STAT_AREA]) ** 0.5),
            )
            cleaned = np.where(labels == chosen, alpha, 0).astype(np.uint8)
            subject.putalpha(Image.fromarray(cleaned, mode="L"))
            alpha = cleaned
    except Exception:
        pass

    coverage = float(np.count_nonzero(alpha >= 16)) / float(alpha.size)
    if coverage < 0.005:
        raise RuntimeError("Background removal found no usable subject in a refinement view")
    return subject


class SpatialGenHunyuan2MV:
    _pipeline: Any = None
    _pipeline_key: tuple[str, str] | None = None

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "model": (_models(),),
                "front": ("IMAGE",),
                "back": ("IMAGE",),
                "left": ("IMAGE",),
                "right": ("IMAGE",),
                "seed": ("INT", {"default": 1234567, "min": 0, "max": 0xFFFFFFFF}),
                "steps": ("INT", {"default": 20, "min": 1, "max": 100}),
                "guidance_scale": ("FLOAT", {"default": 5.0, "min": 0.0, "max": 30.0}),
                "octree_resolution": ("INT", {"default": 256, "min": 64, "max": 512, "step": 64}),
                "max_faces": ("INT", {"default": 40000, "min": 1000, "max": 500000}),
                "filename_prefix": ("STRING", {"default": "spatialgen_refined_mv"}),
            }
        }

    RETURN_TYPES = ("STRING",)
    RETURN_NAMES = ("mesh_path",)
    FUNCTION = "generate"
    CATEGORY = "SpatialGen/3D"
    OUTPUT_NODE = True

    @classmethod
    def _load_pipeline(cls, model: str):
        if model.startswith("<"):
            raise RuntimeError(
                f"Hunyuan3D-2mv model is missing. Run tools/setup_hunyuan2mv.sh. Expected {EXPECTED_MODEL}"
            )

        model_path = (Path(folder_paths.models_dir) / model).resolve()
        config_path = model_path.parent / "config.yaml"
        if not model_path.is_file() or not config_path.is_file():
            raise RuntimeError(f"Incomplete Hunyuan3D-2mv model at {model_path.parent}")

        device = model_management.get_torch_device()
        key = (str(model_path), str(device))
        if cls._pipeline is not None and cls._pipeline_key == key:
            return cls._pipeline

        pipeline_type, _, _, _, _ = _load_hunyuan_symbols()
        if cls._pipeline is not None:
            del cls._pipeline
            cls._pipeline = None
            model_management.soft_empty_cache()

        dtype = torch.float16 if device.type in ("cuda", "mps") else torch.float32
        cls._pipeline = pipeline_type.from_single_file(
            ckpt_path=str(model_path),
            config_path=str(config_path),
            device=device,
            dtype=dtype,
            use_safetensors=True,
        )
        cls._pipeline_key = key
        return cls._pipeline

    def generate(
        self,
        model,
        front,
        back,
        left,
        right,
        seed,
        steps,
        guidance_scale,
        octree_resolution,
        max_faces,
        filename_prefix,
    ):
        pipeline = self._load_pipeline(model)
        _, remover_type, floater_type, degenerate_type, reducer_type = _load_hunyuan_symbols()
        remover = remover_type()
        images = {
            "front": _tensor_to_subject(front, remover),
            "back": _tensor_to_subject(back, remover),
            "left": _tensor_to_subject(left, remover),
            "right": _tensor_to_subject(right, remover),
        }

        try:
            outputs = pipeline(
                image=images,
                num_inference_steps=int(steps),
                guidance_scale=float(guidance_scale),
                generator=torch.manual_seed(int(seed) % (2**32)),
                octree_resolution=int(octree_resolution),
                output_type="trimesh",
            )
            mesh = outputs
            while isinstance(mesh, (list, tuple)):
                if not mesh:
                    raise RuntimeError("Hunyuan3D-2mv returned no mesh")
                mesh = mesh[0]

            mesh = floater_type()(mesh)
            mesh = degenerate_type()(mesh)
            mesh = reducer_type()(mesh, max_facenum=int(max_faces))
            mesh = apply_canonical_y_up(mesh)

            # Hunyuan shape space and glTF are both right-handed Y-up. Keep this identity
            # conversion explicit: Unity's glTF importer performs the handedness conversion,
            # while applying TripoSR's Z/Y swap here would rotate the result onto its side.
            mesh.metadata["extras"] = {
                **(mesh.metadata.get("extras") or {}),
                "spatialgen_coordinate_system": "right-handed Y-up",
                "spatialgen_calibration": "+X right, +Y up, +Z front",
            }

            output_dir, filename, counter, subfolder, _ = folder_paths.get_save_image_path(
                filename_prefix, folder_paths.get_output_directory()
            )
            output_path = Path(output_dir) / f"{filename}_{counter:05}_.glb"
            output_path.parent.mkdir(parents=True, exist_ok=True)
            mesh.export(output_path, file_type="glb")
            relative = str(Path(subfolder) / output_path.name)
            reference = {"filename": output_path.name, "subfolder": subfolder, "type": "output"}
            return {"ui": {"meshes": [reference]}, "result": (relative,)}
        except Exception:
            # Auto mode may immediately run TripoSR after this exception. Release failed
            # Hunyuan state first so the fallback is not starved of VRAM.
            type(self)._pipeline = None
            type(self)._pipeline_key = None
            del pipeline
            raise
        finally:
            del images
            gc.collect()
            model_management.soft_empty_cache()


NODE_CLASS_MAPPINGS = {"SpatialGenHunyuan2MV": SpatialGenHunyuan2MV}
NODE_DISPLAY_NAME_MAPPINGS = {"SpatialGenHunyuan2MV": "SpatialGen Hunyuan3D-2mv"}

__all__ = ["NODE_CLASS_MAPPINGS", "NODE_DISPLAY_NAME_MAPPINGS"]
