# Multi-View Local Refinement

SpatialGen's near-term local refinement is an image-conditioned region replacement pipeline,
not masked latent editing of an existing 3D representation. Hunyuan3D-2mv reconstructs a new
surface for the selected part; Unity is responsible for making that change spatially local.

## Data flow

1. The refinement OBB is captured from Front, Back, Left and Right orthographic cameras.
2. Every view supplies RGB, depth, edges, a visible-surface mask, projected OBB crop bounds,
   and the capture-time camera matrices.
3. The router builds one four-branch ComfyUI graph. The branches share checkpoint, prompt,
   ControlNet loaders and seed, but inpaint their own masked view.
4. Each result is cropped to its projected OBB, centred and letterboxed to 512×512.
5. The project-owned `SpatialGenHunyuan2MV` node removes the background, keeps the central
   connected component and submits the tagged Front/Back/Left/Right images to the pinned
   Hunyuan3D-2mv Fast checkpoint.
6. If Hunyuan is unavailable in `Auto` mode, the refined Front view is lifted through the
   existing TripoSR graph and the response records the fallback and warning.
7. Unity measures the source fragments removed by the exact cut in OBB-local coordinates,
   fits the replacement to 102% of those source dimensions, and centres it on the removed
   geometry. The OBB remains an editing boundary and no longer determines replacement size.
   Clipping uses a 5% transition depth on only the faces that carry source seams, bounded by
   the measured source depth, before the four views are projected into vertex colours.
8. The source mesh is clipped exactly at the OBB. Source and replacement boundary loops are
   paired by OBB face and centroid; an arc-length zipper triangulation joins unequal loop
   tessellations. Tiny disconnected planar source islands are capped; an unmatched substantial
   contour still aborts the transaction rather than producing a non-manifold branch.
9. Preserved source submeshes, replacement geometry and transition strips are combined into
   one mesh. A positional edge-incidence check requires every new seam edge to have exactly
   two incident triangles.
10. Only after the full composite validates does one Undo transaction hide the source. Any
    failure destroys the temporary result and leaves the original hierarchy active.

The zipper is the topology-preserving equivalent of equal-count loop resampling. It advances
around both loops by normalised arc length while retaining every original boundary vertex;
resampling without also splitting the adjacent source triangles would create T-junctions.

## Models and installation

The default refinement lifter is `Auto`. It prefers `tencent/Hunyuan3D-2mv`, Fast variant,
with revision `3a761b539b29fe4ff64714813aa9560fd66f5de0`, 20 steps, guidance 5, octree resolution
256 and a 40,000-face limit. The minimal pipeline wrapper is pinned separately. Run:

```bash
./tools/setup_hunyuan2mv.sh [optional-ComfyUI-root]
```

The command is explicit by design: generation never rewrites a ComfyUI installation. The
same pinned node, wrapper and model setup is present in `notebooks/Colab_ComfyUI.ipynb`.
`GET /health` reports Hunyuan and TripoSR capability before refinement is submitted.

The Tencent model licence and attribution apply to research use and generated results.

## Coordinate and appearance reconstruction

Hunyuan shape space is exported as canonical right-handed Y-up glTF. An asymmetric axis
fixture (`+X=2`, `+Y=3`, `+Z=4`) guards against an accidental swap or reflection. Unity does
not run heuristic mesh levelling on Hunyuan results; the legacy geometry estimator remains
only for TripoSR fallback.

Vertex colours are projected with immutable capture-time matrices rather than live camera
objects, so moving or deleting the rig while inference runs cannot corrupt the result. A view
is accepted only inside its stored OBB crop; normal visibility is weighted to the fourth
power, and the two strongest valid samples are blended.

## Validation and remaining boundary

Automated coverage includes router ordering, crop conversion, letterboxing, shared graph
state, capability preflight, four-output collection and fallback; Unity coverage includes
camera migration, exact clipping and attribute interpolation, selective OBB planes, multiple
loops, winding repair, transition geometry, stored-matrix colour projection and transaction
rollback.

This implementation still cannot condition Hunyuan on unchanged exterior 3D samples. It
therefore provides hard geometric locality and a welded transition in Unity, but not Rodin-
style masked 3D latent inpainting. A future provider can sit behind the same lifter interface.
