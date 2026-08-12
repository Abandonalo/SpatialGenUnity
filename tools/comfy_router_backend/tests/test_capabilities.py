from __future__ import annotations

import unittest
from unittest.mock import patch

from tools.comfy_router_backend.capabilities import refinement_capabilities


class CapabilityTests(unittest.TestCase):
    @patch("tools.comfy_router_backend.capabilities.get_object_info")
    def test_reports_model_and_node_availability(self, object_info):
        object_info.return_value = {
            "SpatialGenHunyuan2MV": {
                "input": {"required": {"model": [["hunyuan3d_2mv/model.safetensors"]]}}
            },
            "TripoSRSampler": {},
            "TripoSRModelLoader": {},
        }
        values = {item.id: item for item in refinement_capabilities()}
        self.assertTrue(values["hunyuan3d_2mv"].available)
        self.assertTrue(values["tripo_sr"].available)

    @patch("tools.comfy_router_backend.capabilities.get_object_info")
    def test_missing_checkpoint_is_not_advertised(self, object_info):
        object_info.return_value = {
            "SpatialGenHunyuan2MV": {
                "input": {"required": {"model": [["<missing: model>"]]}}
            }
        }
        values = {item.id: item for item in refinement_capabilities()}
        self.assertFalse(values["hunyuan3d_2mv"].available)
        self.assertIn("setup_hunyuan2mv.sh", values["hunyuan3d_2mv"].detail)


if __name__ == "__main__":
    unittest.main()
