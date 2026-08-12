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
   one mesh. Positional edge-incidence validation checks that every transition boundary meets
   source or replacement geometry. Exact two-triangle incidence is preferred; geometrically
   coincident subdivisions within 0.8 mm are accepted and inherited non-manifold edges are
   reported as warnings.
10. Only after the full composite validates does one Undo transaction hide the source. Any
    failure destroys the temporary result and leaves the original hierarchy active.

The zipper is the topology-preserving equivalent of equal-count loop resampling. It advances
around both loops by normalised arc length while retaining every original boundary vertex;
resampling without also splitting the adjacent source triangles would create T-junctions.

## TripoSR refinement: observed problems and solutions

TripoSR remains useful as a lower-cost fallback when Hunyuan3D-2mv is unavailable, especially
on hardware that cannot hold the multi-view model. However, it reconstructs one isolated RGB
view rather than editing an existing 3D representation. Integrating that output as a local
replacement exposed several failures at the interfaces between image preparation, lifting and
mesh splicing.

**Incorrect mask binding and reconstructed backgrounds.** The first TripoSR graph used a
solid-white mask, so the sampler interpreted the entire image—including its background—as the
object. A second graph-injection error replaced every `LoadImage` input with the RGB image,
including the node intended to load the mask. This produced the tensor-size failure
`Expected size 125 but got size 512` when TripoSR concatenated an OBB crop with a full-frame
mask. The router now crops RGB and mask with the same projected OBB bounds, expands the mask
slightly to retain edge pixels, keeps its central connected component, composites all excluded
pixels to white, and passes the resulting mask through an explicit `__MASK_IMAGE__`
placeholder. Node-class inference is disabled whenever a graph already contains explicit
placeholders, preventing it from rewriting the mask input. The lifted mesh therefore represents
the selected foreground instead of the image background.

**Tilt inherited from the source image.** The asset-oriented diffusion style that produced the
most liftable images often rendered an object from a slightly elevated three-quarter view.
Because TripoSR reconstructs in the source camera frame, this viewpoint became a tilted and
yaw-rotated mesh in Unity. Prompting only for a strict orthographic front view reduced image
quality and sometimes returned blueprint-like images, so the correction was moved to geometry.
For TripoSR output, Unity estimates the vertical axis from area-weighted wall normals, falls
back to near-horizontal faces when no stable wall set exists, and performs a bounded footprint
yaw search. The correction is applied below the imported root before non-uniform fitting; this
ordering prevents later axis scaling from reintroducing tilt. Hunyuan output bypasses this
heuristic because it is exported in a tested canonical Y-up frame.

**Replacement size followed the editing box.** Initially, Unity scaled every reconstruction to
104% of the refinement OBB. The OBB is intentionally easy to draw with padding, so this made a
replacement larger than the source part even when TripoSR reconstructed the correct object.
The exact inside fragments produced by source clipping are now measured in OBB-local
coordinates. Unity fits the replacement to 102% of those measured dimensions and centres it on
the removed geometry. The small overscan exists only to create a weldable transition; changing
the amount of empty space in the OBB no longer changes the replacement's final size.

**Closed source meshes reported open cuts.** Dense TripoSR meshes contain very small triangles,
duplicated coplanar segments and small numerical cracks. The original sequential segment walk
could enter a duplicate branch or lose a sub-millimetre triangle, after which an otherwise valid
OBB cut was reported as an open boundary. Boundary extraction now constructs a welded edge
graph, parity-cancels duplicated interior segments, repairs only nearby degree-one endpoints on
the same OBB face, preserves small triangles with an area-squared tolerance appropriate to
fitted TripoSR geometry, and snaps cut vertices to the canonical loop coordinates. A genuinely
open contour still aborts the operation.

**Different source and replacement topology.** In one failure, the source cut contained a main
12.54 m contour and a 0.40 m disconnected corner island, while the replacement contained only
the expected main contour. Requiring equal loop counts rejected the valid replacement; attaching
both source loops to one replacement loop would instead create a non-manifold branch. The
welder now pairs principal contours first by OBB face, centroid and perimeter. An unmatched
single-face island is capped only when both its perimeter and planar span are at most 10% of the
main contour and selection dimensions. Any unmatched substantial contour remains a hard error,
and the original hierarchy is left unchanged.

**Safe integration into the original model.** Earlier overlap-based replacement could hide a
gap without producing a continuous surface. The current path clips source and replacement
triangles exactly, interpolates normals, tangents, colours and UVs at intersections, and joins
paired loops with an arc-length zipper strip. The complete composite is validated before the
source object is hidden. If clipping, pairing, triangulation or incidence validation fails, the
temporary result is destroyed and one transaction restores the original model. This makes a
failed TripoSR refinement recoverable rather than leaving a partially modified scene.

These measures make single-view TripoSR practical as a fallback, but they do not solve its
fundamental ambiguity: unseen surfaces and consistency across Front, Back, Left and Right are
not conditioned during lifting. Hunyuan3D-2mv is therefore preferred for refinement, while
Unity's exact clipping, source-derived sizing and transactional welding enforce locality for
either lifter.

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
