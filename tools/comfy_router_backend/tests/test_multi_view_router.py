from __future__ import annotations

import base64
import io
import unittest
from types import SimpleNamespace
from unittest.mock import patch

from PIL import Image

from tools.comfy_router_backend.comfy_client import ComfyOutputs
from tools.comfy_router_backend.models import MultiViewRefinementRequest, ViewPayload
from tools.comfy_router_backend.multi_view_router import (
    _CARDINAL_VIEWS,
    _build_multi_inpaint_graph,
    _pixel_crop,
    _prepare_lifter_image,
    _prepare_lifter_view,
    _lift_tripo,
    _views_by_name,
    handle_refine,
)


def png(width: int = 32, height: int = 16, colour=(20, 80, 160)) -> str:
    image = Image.new("RGB", (width, height), colour)
    output = io.BytesIO()
    image.save(output, "PNG")
    return base64.b64encode(output.getvalue()).decode("ascii")


def mask_png(width: int = 32, height: int = 16) -> str:
    return png(width, height, (255, 255, 255))


def request(lifter: str = "auto", allow_fallback: bool = True) -> MultiViewRefinementRequest:
    encoded = png()
    return MultiViewRefinementRequest(
        requestId="test",
        positivePrompt="replace the roof",
        seed=42,
        lifter=lifter,
        allowFallback=allow_fallback,
        views=[
            ViewPayload(
                viewType=name,
                width=32,
                height=16,
                rgbBase64=encoded,
                depthBase64=encoded,
                edgesBase64=encoded,
                maskBase64=mask_png(),
                cropMinX=0.25,
                cropMinY=0.0,
                cropMaxX=0.75,
                cropMaxY=1.0,
            )
            for name in _CARDINAL_VIEWS
        ],
    )


class MultiViewRouterTests(unittest.TestCase):
    def test_canonical_order_is_front_back_left_right(self):
        self.assertEqual(("Front", "Back", "Left", "Right"), _CARDINAL_VIEWS)
        self.assertEqual(list(_CARDINAL_VIEWS), list(_views_by_name(request())))

    def test_crop_conversion_uses_normalized_bounds(self):
        self.assertEqual((25, 10, 50, 20), _pixel_crop((100, 40), (0.25, 0.25, 0.75, 0.75)))
        self.assertEqual((0, 0, 100, 40), _pixel_crop((100, 40), (0.8, 0.2, 0.2, 0.9)))

    def test_lifter_input_is_letterboxed_to_512(self):
        req = request()
        encoded = _prepare_lifter_image(req, png(), req.views[0])
        with Image.open(io.BytesIO(base64.b64decode(encoded))) as result:
            self.assertEqual((512, 512), result.size)
            self.assertEqual((255, 255, 255), result.getpixel((0, 0)))
            # The cropped 16x16 subject becomes a centred 432x432 square.
            self.assertEqual((20, 80, 160), result.getpixel((256, 256)))
            self.assertEqual((255, 255, 255), result.getpixel((39, 256)))
            self.assertEqual((20, 80, 160), result.getpixel((40, 256)))

    def test_tripo_input_whitens_pixels_outside_the_selection_mask(self):
        req = request()
        view = req.views[0]
        view.width = 128
        view.height = 128
        view.cropMinX = view.cropMinY = 0.0
        view.cropMaxX = view.cropMaxY = 1.0
        source = Image.new("RGB", (128, 128), (20, 80, 160))
        mask = Image.new("L", (128, 128), 0)
        for y in range(48, 80):
            for x in range(48, 80):
                mask.putpixel((x, y), 255)
        source_buffer = io.BytesIO()
        mask_buffer = io.BytesIO()
        source.save(source_buffer, "PNG")
        mask.save(mask_buffer, "PNG")
        view.maskBase64 = base64.b64encode(mask_buffer.getvalue()).decode("ascii")

        image_encoded, mask_encoded = _prepare_lifter_view(
            req, base64.b64encode(source_buffer.getvalue()).decode("ascii"), view
        )

        with Image.open(io.BytesIO(base64.b64decode(image_encoded))) as image_result, \
             Image.open(io.BytesIO(base64.b64decode(mask_encoded))) as mask_result:
            self.assertEqual((20, 80, 160), image_result.getpixel((256, 256)))
            self.assertEqual((255, 255, 255), image_result.getpixel((80, 80)))
            self.assertEqual(255, mask_result.getpixel((256, 256)))
            self.assertEqual(0, mask_result.getpixel((80, 80)))

    @patch("tools.comfy_router_backend.multi_view_router.send_to_comfy")
    @patch("tools.comfy_router_backend.multi_view_router.inject_params", return_value={})
    @patch("tools.comfy_router_backend.multi_view_router.load_graph", return_value={})
    def test_tripo_lifter_receives_the_isolated_mask(self, _load, inject, send):
        send.return_value = SimpleNamespace(mesh_base64="mesh")

        result = _lift_tripo(
            "isolated-image", "foreground-mask", seed=7, positive="roof",
            negative="background", tripo_model="TripoSRmodel.ckpt",
            geometry_resolution=512, tripo_threshold=25.0,
        )

        self.assertEqual("mesh", result)
        request_sent = inject.call_args.args[1]
        self.assertEqual("isolated-image", request_sent.rgb_image)
        self.assertEqual("foreground-mask", request_sent.mask_image)

    @patch("tools.comfy_router_backend.multi_view_router.load_graph", return_value={})
    @patch("tools.comfy_router_backend.multi_view_router.inject_params")
    def test_four_branch_graph_shares_loaders_and_seed(self, inject, _load):
        def branch(_graph, req):
            return {
                "4": {"class_type": "CheckpointLoaderSimple", "inputs": {}},
                "13": {"class_type": "KSampler", "inputs": {"seed": req.seed, "model": ["4", 0]}},
                "20": {"class_type": "SaveImage", "inputs": {"images": ["13", 0]}},
            }

        inject.side_effect = branch
        graph = _build_multi_inpaint_graph(
            _views_by_name(request()),
            positive="roof",
            negative="duplicate",
            seed=99,
            steps=20,
            cfg=5.0,
            denoise=0.95,
            tripo_model="TripoSRmodel.ckpt",
            geometry_resolution=512,
            tripo_threshold=25.0,
        )

        self.assertEqual(1, sum(node["class_type"] == "CheckpointLoaderSimple" for node in graph.values()))
        samplers = [node for node in graph.values() if node["class_type"] == "KSampler"]
        saves = [node for node in graph.values() if node["class_type"] == "SaveImage"]
        self.assertEqual(4, len(samplers))
        self.assertTrue(all(node["inputs"]["seed"] == 99 for node in samplers))
        self.assertEqual(
            {f"spatialgen_refined_{name.lower()}" for name in _CARDINAL_VIEWS},
            {node["inputs"]["filename_prefix"] for node in saves},
        )

    @patch("tools.comfy_router_backend.multi_view_router._build_multi_inpaint_graph")
    @patch(
        "tools.comfy_router_backend.multi_view_router.lifter_available",
        return_value=(False, "model missing"),
    )
    def test_explicit_hunyuan_fails_before_inpainting_when_unavailable(self, _available, graph):
        with self.assertRaisesRegex(RuntimeError, "model missing"):
            handle_refine(
                request(lifter="hunyuan3d_2mv", allow_fallback=False),
                tripo_model="TripoSRmodel.ckpt",
                geometry_resolution=512,
                tripo_threshold=25.0,
            )
        graph.assert_not_called()

    @patch("tools.comfy_router_backend.multi_view_router._lift_tripo", return_value="mesh")
    @patch("tools.comfy_router_backend.multi_view_router.lifter_available")
    @patch("tools.comfy_router_backend.multi_view_router._collect_refined_views")
    @patch("tools.comfy_router_backend.multi_view_router.send_to_comfy_all")
    @patch("tools.comfy_router_backend.multi_view_router._build_multi_inpaint_graph", return_value={})
    def test_auto_reports_hunyuan_to_tripo_fallback(
        self, _graph, send, collect, available, lift
    ):
        send.return_value = ComfyOutputs("p", [], [])
        collect.return_value = {name: png() for name in _CARDINAL_VIEWS}
        available.side_effect = [(False, "checkpoint missing"), (True, "")]

        response = handle_refine(
            request(), tripo_model="TripoSRmodel.ckpt", geometry_resolution=512,
            tripo_threshold=25.0,
        )

        self.assertTrue(response.success)
        self.assertTrue(response.fallbackUsed)
        self.assertEqual("tripo_sr", response.lifterUsed)
        self.assertIn("checkpoint missing", response.warnings[0])
        lift.assert_called_once()

    @patch("tools.comfy_router_backend.multi_view_router._lift_hunyuan", return_value="mesh")
    @patch("tools.comfy_router_backend.multi_view_router.lifter_available", return_value=(True, ""))
    @patch("tools.comfy_router_backend.multi_view_router._collect_refined_views")
    @patch("tools.comfy_router_backend.multi_view_router.send_to_comfy_all")
    @patch("tools.comfy_router_backend.multi_view_router._build_multi_inpaint_graph", return_value={})
    def test_hunyuan_collects_all_four_refined_outputs(
        self, _graph, send, collect, _available, _lift
    ):
        send.return_value = ComfyOutputs("p", [], [])
        collect.return_value = {name: png() for name in _CARDINAL_VIEWS}

        response = handle_refine(
            request(), tripo_model="TripoSRmodel.ckpt", geometry_resolution=512,
            tripo_threshold=25.0,
        )

        self.assertFalse(response.fallbackUsed)
        self.assertEqual("hunyuan3d_2mv", response.lifterUsed)
        self.assertEqual(list(_CARDINAL_VIEWS), [view.viewType for view in response.refinedViews])


if __name__ == "__main__":
    unittest.main()
