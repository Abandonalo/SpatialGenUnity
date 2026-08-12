"""Pinned Hunyuan shape-space to canonical glTF Y-up conversion."""

from __future__ import annotations

import numpy as np


# Hunyuan3D-2mv emits +X right, +Y up and +Z front. Trimesh's glTF exporter uses the
# same right-handed Y-up convention, so the correct conversion is deliberately identity.
HUNYUAN_TO_GLTF_Y_UP = np.eye(4, dtype=np.float64)


def validate_asymmetric_calibration() -> None:
    """Detect an accidental axis swap/reflection using three unequal calibration arms."""
    calibration = np.array(
        [
            [2.0, 0.0, 0.0, 1.0],  # +X/right arm
            [0.0, 3.0, 0.0, 1.0],  # +Y/up arm
            [0.0, 0.0, 4.0, 1.0],  # +Z/front arm
        ]
    )
    transformed = (HUNYUAN_TO_GLTF_Y_UP @ calibration.T).T[:, :3]
    expected = calibration[:, :3]
    if not np.allclose(transformed, expected, atol=1e-7):
        raise RuntimeError("Hunyuan-to-glTF conversion failed asymmetric axis calibration")
    if np.linalg.det(HUNYUAN_TO_GLTF_Y_UP[:3, :3]) <= 0:
        raise RuntimeError("Hunyuan-to-glTF conversion introduces a reflection")


def apply_canonical_y_up(mesh):
    validate_asymmetric_calibration()
    mesh.apply_transform(HUNYUAN_TO_GLTF_Y_UP)
    return mesh


__all__ = ["HUNYUAN_TO_GLTF_Y_UP", "apply_canonical_y_up", "validate_asymmetric_calibration"]
