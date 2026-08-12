from __future__ import annotations

import importlib.util
from pathlib import Path
import unittest

import numpy as np


MODULE_PATH = (
    Path(__file__).resolve().parents[2]
    / "comfy_custom_nodes"
    / "SpatialGenHunyuan2MV"
    / "coordinates.py"
)
SPEC = importlib.util.spec_from_file_location("spatialgen_hunyuan_coordinates", MODULE_PATH)
coordinates = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(coordinates)


class HunyuanCoordinateTests(unittest.TestCase):
    def test_asymmetric_calibration_preserves_right_up_and_front(self):
        coordinates.validate_asymmetric_calibration()
        matrix = coordinates.HUNYUAN_TO_GLTF_Y_UP
        points = np.array([[2, 0, 0, 1], [0, 3, 0, 1], [0, 0, 4, 1]], dtype=float)
        np.testing.assert_allclose((matrix @ points.T).T[:, :3], points[:, :3])
        self.assertGreater(np.linalg.det(matrix[:3, :3]), 0)


if __name__ == "__main__":
    unittest.main()
