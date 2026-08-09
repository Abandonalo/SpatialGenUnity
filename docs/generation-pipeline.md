# Text-to-Asset Generation: Image Style, Prompt Policy, and Orientation Recovery

*Draft thesis material, written from the implementation as it stands. Numbers cited are
measured on this system; adapt voice and citation style as needed.*

## 1. Pipeline overview

Asset generation is a two-stage process: a conditioned text-to-image pass produces a single
source image, and a single-view reconstruction model lifts that image to a mesh. The stages
run inside ComfyUI, orchestrated by a FastAPI router that owns all prompt policy, with Unity
acting as the authoring front end.

A generation run proceeds as follows. The user authors an *occupy proxy* — a primitive
volume standing in for the intended asset — and supplies a subject prompt. Unity renders two
conditioning images from the proxy: a linear depth map and an edge map derived from it. The
router composes the final positive and negative prompts, injects them along with the
conditioning images into a ComfyUI graph template, and submits it. The graph runs Stable
Diffusion 1.5 under two ControlNet constraints, removes the background, and passes the
result to TripoSR. The resulting mesh is downloaded, imported into Unity, levelled, and
scaled to fill the proxy.

The design constraint that shapes everything downstream is that the reconstruction stage is
*single-view*. TripoSR can only reconstruct what one image shows. The image is therefore not
an aesthetic artefact but a specification: its quality is judged solely by whether it depicts
one complete, unoccluded, isolated object. Most of the engineering described below exists to
make the diffusion model reliably produce that, and to repair the one systematic distortion
that the chosen solution introduced.

## 2. Choice of image style

The naive approach is to prompt for a photograph of the subject. This fails for architectural
subjects in a specific and instructive way: a photograph of a house is a photograph of a
*scene*. It brings a lawn, a driveway, foreground planting, neighbouring buildings, and sky.
Background removal strips what is behind the subject but cannot strip what is in front of it,
so foreground vegetation survives the cut and is welded onto the reconstructed mesh as
geometry. Cast shadows and ground contact behave the same way: the lifter reads them as
surface and extrudes them.

The style eventually adopted is the **stylised low-polygon 3D asset render**, applied as the
modifier `low poly 3d model of` prepended to the user's subject. The rationale is
conventional rather than aesthetic: a 3D asset render has no scene around it *by convention*.
Asking for the object in that idiom obtains isolation implicitly, without having to enumerate
everything that must be absent. The convention also supplies a neutral background and even
lighting, both of which improve the reconstruction.

This choice is not free. The same convention that isolates the object also renders it in a
three-quarter view from slightly above, because that is how asset renders are conventionally
presented. Section 5 addresses the consequence.

## 3. Model selection

**Diffusion model.** Stable Diffusion 1.5 base drifts strongly toward photography and brings
the scene with it, even when the prompt asks otherwise. The system therefore uses
**DreamShaper 8**, a fine-tune of SD 1.5, which renders isolated 3D-asset subjects far more
reliably. Remaining on the SD 1.5 architecture is deliberate: it keeps the ControlNet v1.1
weights applicable, which a newer base model would not.

**Spatial conditioning.** Two ControlNets are applied simultaneously:

| ControlNet | Weights | Strength | Role |
|---|---|---|---|
| Depth | `control_v11p_sd15_depth` | 0.45 | Carries the spatial constraint; leads |
| Canny | `control_v11p_sd15_canny` | 0.20 | Sharpens the silhouette only |

The strengths were tuned down from an initial 0.8 / 0.4. The conditioning depth map is
rendered from a proxy, which is a featureless primitive — a box or a capsule. At high
ControlNet strength the model reproduces the *primitive* rather than the subject that is
supposed to occupy it, yielding a box-shaped blob. At 0.45 / 0.20 the generated silhouette
still lands on the proxy's footprint to within a few pixels, while the subject remains
recognisable. Canny is kept deliberately low because it is easy to overdrive into hard
cartoon outlines.

**Reconstruction model.** TripoSR, at geometry resolution 512 and density threshold 25.
A Hunyuan3D 2.1 path exists as an alternative; because Hunyuan is natively image-to-3D and
handles framing itself, the isolation cues described below are applied only on the TripoSR
path.

## 4. Prompt policy for output stability

The central empirical finding is that **prompt length trades directly against subject
fidelity**. The user's subject is frequently a single token ("house"), and CLIP's attention
budget is finite. A long block of style and framing instructions outvotes a one-token subject,
and the model renders the instructions instead of the thing requested. Measured on the prompt
`house`:

| Cue formulation | Result |
|---|---|
| `symmetrical front elevation, orthographic product render` | Architectural blueprints on graph paper |
| `studio photograph, seamless backdrop, sharp focus, …` | Abstract framed boxes |
| `house, 3d render, isolated game asset, …` (appended) | A dark façade; on DreamShaper, a sofa |
| `low poly 3d model of a house` | Recognisable houses |

Three rules follow, and they are the operative guidance for writing these inner prompts.

**Rule 1 — the style must modify the head noun, not trail behind it.** `house, 3d render,
isolated game asset` places the style words in competition with the subject, and they win;
on DreamShaper this produced a sofa. `low poly 3d model of a house` keeps `house` as the head
noun and obtains the same visual idiom. Prepend, never append.

**Rule 2 — keep the positive prompt short.** Beyond the style modifier, only one clause of
framing survives, applied on the TripoSR path:

```
front view, whole object centered in frame, isolated on a plain white background
```

Every additional clause measurably erodes subject fidelity. Notably, the vocabulary that
seems most obviously correct is the most dangerous: architectural terms such as *elevation*
and *orthographic* collapse the output into technical drawings, because those words
co-occur with line art far more often than with rendered buildings in the training
distribution.

**Rule 3 — everything else belongs in the negative prompt.** A negative prompt steers the
result without competing for the subject's share of the attention budget, so constraints that
would be destructive as positive cues are safe as negative ones. The negative prompt is
organised by failure mode rather than written as an undifferentiated list, and each group
addresses a specific way the reconstruction breaks:

- **Photography** (`photograph, photorealistic, real estate photo`) — suppresses the scene.
- **Drawing styles** (`blueprint, technical drawing, elevation drawing, floor plan, line
  art, diagram, graph paper, monochrome, isometric`) — architectural subjects collapse into
  these unless explicitly warned off.
- **Degenerate isolation** (`abstract sculpture, empty frame, shelf, display case`) — the
  model's failure reading of "isolated object" is an empty frame rather than the subject.
- **Wrong viewpoint** (`side view, profile view, three-quarter view, rear view, tilted,
  foreshortening`) — the largest single cause of malformed reconstruction.
- **Ground contact** (`pedestal, base, platform, cast shadow, contact shadow, reflection`) —
  anything touching the subject survives background removal and becomes geometry.
- **Foreground planting** (`flowers, blossoms, bushes, hedge, foliage, trees, vegetation`) —
  the worst category, because it occludes the subject rather than sitting behind it, and is
  therefore welded onto the mesh.
- **Extra subjects** (`multiple objects, duplicate, clutter, cropped, partial object`) —
  additional objects are fused into a single mesh.
- **Optical effects** (`depth of field, bokeh, vignette, motion blur, close-up`) — the lifter
  interprets blur gradients as shape.

The asymmetry is worth stating explicitly in the thesis: **the positive prompt should name
the subject and its idiom; the negative prompt should enumerate the failure modes.** This
partition is what makes single-token subjects survive an otherwise heavily constrained
generation.

## 5. The orientation problem

Adopting the asset-render style solved image quality and introduced a systematic geometric
defect. Asset renders are conventionally three-quarter views from slightly above. TripoSR
reconstructs in the source camera's frame, so this viewpoint is baked directly into the
output vertices: the mesh arrives in Unity both tilted and turned, with the transform at
identity.

The tilt was measured across three consecutive runs at **3.26°, 3.61°, and 6.92°** from
vertical. Small in absolute terms, but plainly visible against an axis-aligned proxy box, and
unacceptable for a greyboxing workflow whose premise is that authored volumes are respected.

The obvious remedy — strengthening the front-view wording in the prompt — was rejected. The
negative prompt already carries `three-quarter view, angled view, tilted`, and by Rule 2 above,
pushing harder on the positive side risks reintroducing the blueprint failure that the style
change had just fixed. The two objectives are in direct tension. The orientation was therefore
recovered geometrically, downstream of generation, where it costs nothing in image quality.

## 6. Orientation recovery

Three distinct defects had to be resolved. They are separated here because only the third
turned out to dominate, and the diagnostic sequence is itself instructive.

### 6.1 Choosing an estimator: walls, not flat faces

The first implementation estimated the ground plane as the area-weighted mean of all
near-horizontal face normals, within a 55° tolerance. This is the intuitive approach and it is
wrong for buildings. A single-view reconstruction of a house has an invented, lumpy base — the
lifter has never observed the underside — and an asymmetric roof, because the far slope was
never visible either. At a 55° tolerance the pitched roof falls inside the selection and, at
19.8% of surface area against the base's 19.5%, outvotes it. On the failing mesh the true tilt
was 3.61° toward +X while the estimator chose 5.03° toward −Z: a correction about a nearly
perpendicular axis, leaving the asset more crooked than it arrived.

The replacement estimates up from the **walls**. Every wall normal is perpendicular to the up
axis, so up is the direction *least* represented among them — formally, the eigenvector
associated with the smallest eigenvalue of their area-weighted scatter matrix, obtained by
Jacobi rotation of a symmetric 3×3. Walls carry 35–40% of surface area on the test meshes and
determine the axis with an eigenvalue separation of 12–26×. Residual tilt after correction:

| Mesh | Before | After |
|---|---|---|
| A | 3.26° | **0.24°** |
| B | 3.61° | **0.33°** |

The estimator declines rather than guessing when the answer is not determined: fewer than 24
wall triangles, or two smallest eigenvalues within a factor of four, indicates walls that are
parallel or absent and that fix no axis at all. A 65° selection band bounds the correctable
tilt at roughly 25°, comfortably beyond the 3–7° the pipeline produces.

### 6.2 Bounding the yaw search

Squaring the asset to the proxy uses the minimum-area footprint: the yaw whose axis-aligned
XZ bounding rectangle is smallest. Searching a full 90° quadrant, however, allows the minimum
to land a quarter turn from the intended orientation — on one mesh the search chose +63° where
the correct answer was −27°, standing the asset side-on to its proxy. Because TripoSR's output
already faces the source camera by construction, only the three-quarter offset needs removing,
so the search is bounded to ±45°.

### 6.3 Transform order — the dominant defect

With the estimator corrected, rendering the levelled mesh offline confirmed an upright,
symmetric result, yet the asset still imported visibly tilted. The correction was being
computed correctly and destroyed downstream.

`MeshAlignment.Level` wrote the correction into the imported object's **root rotation**.
`MeshFitting.FitToVolume` subsequently wrote a **non-uniform local scale** onto that same
transform, to make the asset fill its proxy. A Unity transform applies its scale in local
space — beneath its own rotation — so the fill scale was stretching geometry that was still
tilted. Anisotropically stretching a tilted object moves its up direction to somewhere the
rotation above it can no longer recover.

The magnitude of the residual error is governed by how far the proxy's aspect ratio departs
from the mesh's own, which is why the defect varied between runs and never presented as a
constant offset. Tracing the mesh's up vector through the full transform chain for a house
arriving 6.9° off vertical:

| Proxy shape (x : y : z) | Correction on root | Correction beneath root |
|---|---|---|
| 1 : 1 : 1 | 0.55° | **0.000°** |
| 2 : 1 : 1 | 4.34° | **0.000°** |
| 1 : 1 : 2 | 4.89° | **0.000°** |
| 2 : 1 : 2 | 6.43° | **0.000°** |
| 2.5 : 1.2 : 1.5 | 4.92° | **0.000°** |

The fix applies the correction to the imported object's *children*, leaving the root's
rotation and scale free for the proxy. The fill scale then acts on already-upright geometry,
where a vertical direction maps to `(0, s_y, 0)` and remains exactly vertical for any diagonal
scale. This is exact rather than approximate, which is why the right-hand column is zero
rather than merely small.

The general lesson, worth stating in the thesis, is that **a rigid correction expressed as a
parent rotation is not invariant under a non-uniform child scale.** Any pipeline that both
reorients and anisotropically fits reconstructed geometry must order the two operations so
that the scale is applied in the corrected frame.

## 7. Validation approach

Two constraints shaped how this work was verified, and both are worth recording.

First, the Unity editor holds an exclusive project lock while open, so the EditMode test suite
could not be executed from the command line during development. Compilation was verified
instead by invoking Unity's bundled Roslyn compiler against the editor's own generated
response files, which is permitted while the lock is held.

Second, and more consequentially, the geometric algorithms were validated against **the actual
GLB files the pipeline had produced**, not against synthetic fixtures. This mattered: the
synthetic box fixtures passed against the original flat-face estimator, whereas the real
meshes immediately exposed both the wrong-axis estimate and the quarter-turn yaw. For the one
piece of non-trivial numerics — the hand-written Jacobi eigensolver — the C# implementation was
extracted and executed directly against a reference LAPACK implementation on the real scatter
matrices, agreeing to 0.000000°.

The methodological point is that reconstruction output is adversarial in ways synthetic
geometry is not: invented bases, unobserved far sides, and organic surface noise all break
estimators that are sound on clean primitives. Test fixtures for this class of problem should
be drawn from pipeline output.
