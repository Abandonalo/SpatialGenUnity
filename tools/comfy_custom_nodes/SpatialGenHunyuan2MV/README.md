# SpatialGen Hunyuan3D-2mv adapter

This deliberately small ComfyUI node accepts four isolated cardinal views and invokes
the multi-view pipeline supplied by `ComfyUI-Hunyuan3DWrapper`. It is installed by
`tools/setup_hunyuan2mv.sh`; do not enable ComfyUI-3D-Pack for this workflow.

The model is downloaded from `tencent/Hunyuan3D-2mv` at the revision pinned by the setup
script. The minimal pipeline dependency is likewise pinned from
`kijai/ComfyUI-Hunyuan3DWrapper` into `ComfyUI/spatialgen_vendor`, outside the custom-node
scanner, so setup neither rewrites nor double-loads another wrapper installed by the user.
Its Tencent Hunyuan Community licence and NOTICE remain authoritative:
https://huggingface.co/tencent/Hunyuan3D-2mv
