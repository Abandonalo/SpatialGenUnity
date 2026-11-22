using System.Collections.Generic;
using UnityEngine;

namespace SpatialGeneration.Generation
{
    /// <summary>
    /// Levels a reconstructed mesh so it sits flat and square in the scene.
    ///
    /// A single-view lifter reconstructs in the camera's frame, so the mesh inherits whatever
    /// angle the source image was drawn from. Asset renders are usually three-quarter views
    /// from slightly above, which arrives in Unity as a model that is both tilted and turned.
    /// Prompting for a dead-on front view fights the style that makes the image liftable in
    /// the first place, so the orientation is corrected here instead, from the geometry.
    ///
    /// Up is taken from the walls rather than from the base. Measured on two lifted houses,
    /// the walls carry 35-40% of the surface area and agree on an axis to within a fraction of
    /// a degree, while the base is a lumpy plinth the lifter invents and the roof is
    /// asymmetric because its far side was never visible. Averaging flat faces is pulled
    /// several degrees off true by that roof; the walls are not.
    /// </summary>
    public static class MeshAlignment
    {
        /// <summary>
        /// A face is a wall when its normal is at least this far from the current up axis.
        ///
        /// Bounds the correctable tilt: walls sit at (90 - tilt) from up, so at 65 this
        /// resolves tilts up to about 25 degrees, which is well past the 3-4 degrees the
        /// pipeline actually produces. Raising it further would start admitting steep roofs.
        /// </summary>
        private const float WallBandDegrees = 65f;

        /// <summary>
        /// How much flatter the wall normals must lie in their plane than across it.
        ///
        /// Guards the degenerate case: two parallel walls fix no up axis at all, and without
        /// this the solver would return an arbitrary perpendicular and tilt a sound mesh.
        /// </summary>
        private const float MinAxisSeparation = 4f;

        private const int MinWallTriangles = 24;

        /// <summary>
        /// Fallback tolerance, used only when no wall axis can be found.
        ///
        /// Deliberately much tighter than the wall band: this averages flat faces, so it has
        /// to exclude pitched roofs to be worth anything.
        /// </summary>
        private const float FlatToleranceDegrees = 25f;

        /// <summary>
        /// Yaw is searched over +/- this, not over a full quadrant.
        ///
        /// The lifter's output already faces the source camera, so only the three-quarter
        /// offset needs removing. Searching a full quadrant lets the smallest footprint land
        /// a quarter turn away and stand the asset side-on to the proxy, which is what
        /// happened to the first mesh this was tried on: 63 degrees chosen where the intended
        /// answer was -27.
        /// </summary>
        private const float MaxYawDegrees = 45f;

        /// <summary>
        /// Re-estimating after the first correction picks up walls that the band missed while
        /// the mesh was still tilted. A second pass is worth about a third of a degree.
        /// </summary>
        private const int LevelPasses = 2;

        /// <summary>
        /// Rotates <paramref name="target"/> so its walls stand vertical and its footprint
        /// squares to the object's own axes.
        /// </summary>
        /// <returns>The correction applied, for logging. Callers need not compose with it.</returns>
        public static Quaternion Level(GameObject target)
        {
            if (target == null)
                return Quaternion.identity;

            // Work in the target's own frame so the result is a local correction, independent
            // of whatever rotation the importer happened to leave on the object.
            if (!CollectGeometry(target, out List<Vector3> vertices, out List<Vector3> normals, out List<float> areas))
                return Quaternion.identity;

            Quaternion correction = ComputeLevelling(normals, areas);
            correction = Quaternion.Euler(0f, FindSquaringYaw(vertices, correction), 0f) * correction;

            if (Quaternion.Angle(correction, Quaternion.identity) < 0.01f)
                return Quaternion.identity;

            return ApplyBeneathRoot(target.transform, correction) ? correction : Quaternion.identity;
        }

        /// <summary>
        /// Rotates the target's children rather than the target itself.
        ///
        /// This has to sit below the root, because <see cref="MeshFitting.FitToVolume"/> then
        /// writes a non-uniform <c>localScale</c> on the root to fill the proxy, and a
        /// transform applies its scale in local space -- underneath its own rotation. Levelling
        /// via the root's rotation therefore leaves the scale acting on geometry that is still
        /// tilted, and stretching a tilted object anisotropically moves its up direction
        /// somewhere the rotation above can no longer correct. Measured on a lifted house that
        /// arrived 6.9 degrees off, the asset came out between 0.4 and 6.4 degrees from
        /// vertical depending only on the proxy's aspect ratio. With the correction below the
        /// scale, a vertical direction maps to (0, sy, 0) and stays exactly vertical.
        /// </summary>
        /// <returns>False when there is nowhere below the root to put the correction.</returns>
        private static bool ApplyBeneathRoot(Transform root, Quaternion correction)
        {
            // Geometry hanging directly off the root has no node of its own to carry this.
            // Only the fallback primitives are built that way, and a primitive is already
            // square, so declining costs nothing and is safer than levelling into a shear.
            if (root.childCount == 0)
                return false;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                child.localPosition = correction * child.localPosition;
                child.localRotation = correction * child.localRotation;
            }

            return true;
        }

        /// <summary>
        /// Iterates the up-axis estimate, returning the correction that levels the mesh.
        ///
        /// The normals stay in the mesh's own frame throughout and the search direction moves
        /// instead, so each pass yields an absolute answer rather than a delta to compose:
        /// <paramref name="normals"/> never rotates, so an axis found against a refined
        /// reference is already expressed in the frame the correction applies to.
        /// </summary>
        private static Quaternion ComputeLevelling(List<Vector3> normals, List<float> areas)
        {
            Quaternion correction = Quaternion.identity;
            Vector3 reference = Vector3.up;

            for (int pass = 0; pass < LevelPasses; pass++)
            {
                if (!TryFindUpAxis(normals, areas, reference, out Vector3 up) &&
                    !TryFindFlatFaceAxis(normals, areas, reference, out up))
                    break;

                correction = Quaternion.FromToRotation(up, Vector3.up);

                // Settled: re-selecting the wall band around this axis would return it again.
                if (Vector3.Angle(up, reference) < 0.05f)
                    break;

                reference = up;
            }

            return correction;
        }

        /// <summary>
        /// The axis the walls agree on.
        ///
        /// Every wall normal is perpendicular to up, so up is the direction least represented
        /// among them: the smallest eigenvector of their area-weighted scatter matrix. Faces
        /// are weighted by area and folded onto one hemisphere first, so opposite walls of the
        /// same building reinforce each other instead of cancelling.
        /// </summary>
        private static bool TryFindUpAxis(
            List<Vector3> normals, List<float> areas, Vector3 reference, out Vector3 up)
        {
            up = reference;

            float cosBand = Mathf.Cos(WallBandDegrees * Mathf.Deg2Rad);
            var scatter = new SymmetricMatrix3();
            float total = 0f;
            int count = 0;

            for (int i = 0; i < normals.Count; i++)
            {
                Vector3 normal = normals[i];
                if (Mathf.Abs(Vector3.Dot(normal, reference)) > cosBand)
                    continue;

                scatter.AddOuterProduct(normal, areas[i]);
                total += areas[i];
                count++;
            }

            if (count < MinWallTriangles || total <= 0f)
                return false;

            scatter.Scale(1f / total);
            scatter.SolveEigen(out Vector3 smallest, out float smallestValue, out float middleValue);

            // Rank-deficient wall set (all parallel): no axis is determined.
            if (smallestValue <= 0f || middleValue < smallestValue * MinAxisSeparation)
                return false;

            up = Vector3.Dot(smallest, reference) < 0f ? -smallest : smallest;
            return true;
        }

        /// <summary>
        /// Area-weighted average of the near-horizontal faces, for meshes with no usable walls.
        ///
        /// Weaker than the wall fit and known to be biased by asymmetric roofs, so it is only
        /// consulted when the wall fit declines.
        /// </summary>
        private static bool TryFindFlatFaceAxis(
            List<Vector3> normals, List<float> areas, Vector3 reference, out Vector3 up)
        {
            up = reference;

            float cosTolerance = Mathf.Cos(FlatToleranceDegrees * Mathf.Deg2Rad);
            Vector3 accumulated = Vector3.zero;

            for (int i = 0; i < normals.Count; i++)
            {
                Vector3 normal = normals[i];
                float alignment = Vector3.Dot(normal, reference);
                if (Mathf.Abs(alignment) < cosTolerance)
                    continue;

                accumulated += (alignment < 0f ? -normal : normal) * areas[i];
            }

            if (accumulated.sqrMagnitude < 1e-8f)
                return false;

            up = accumulated.normalized;
            return true;
        }

        /// <summary>
        /// The yaw within <see cref="MaxYawDegrees"/> whose footprint is smallest.
        ///
        /// The minimum-area footprint is the natural square-on orientation for the box-like
        /// assets this pipeline produces, and needs no knowledge of which side is the front.
        /// </summary>
        private static float FindSquaringYaw(List<Vector3> vertices, Quaternion levelling)
        {
            const float coarseStep = 2f;
            const float fineStep = 0.25f;

            float bestAngle = 0f;
            float bestArea = float.PositiveInfinity;

            for (float angle = -MaxYawDegrees; angle <= MaxYawDegrees; angle += coarseStep)
                Consider(angle, ref bestAngle, ref bestArea);

            // Refine around the coarse winner rather than sweeping finely throughout.
            float coarseWinner = bestAngle;
            for (float angle = coarseWinner - coarseStep; angle <= coarseWinner + coarseStep; angle += fineStep)
                Consider(angle, ref bestAngle, ref bestArea);

            return bestAngle;

            void Consider(float angle, ref float chosenAngle, ref float chosenArea)
            {
                float area = FootprintArea(vertices, Quaternion.Euler(0f, angle, 0f) * levelling);
                if (area >= chosenArea)
                    return;

                chosenArea = area;
                chosenAngle = angle;
            }
        }

        private static float FootprintArea(List<Vector3> vertices, Quaternion orientation)
        {
            float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
            float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;

            foreach (Vector3 vertex in vertices)
            {
                Vector3 rotated = orientation * vertex;
                if (rotated.x < minX) minX = rotated.x;
                if (rotated.x > maxX) maxX = rotated.x;
                if (rotated.z < minZ) minZ = rotated.z;
                if (rotated.z > maxZ) maxZ = rotated.z;
            }

            return (maxX - minX) * (maxZ - minZ);
        }

        /// <summary>
        /// Vertices in <paramref name="target"/>'s local frame, plus per-triangle normals and
        /// areas derived from those vertices.
        ///
        /// Normals come from cross products of the transformed positions rather than from the
        /// mesh's stored normals, so they stay correct under whatever non-uniform or mirrored
        /// scale the glTF importer left on the hierarchy.
        /// </summary>
        private static bool CollectGeometry(
            GameObject target, out List<Vector3> vertices, out List<Vector3> normals, out List<float> areas)
        {
            vertices = new List<Vector3>();
            normals = new List<Vector3>();
            areas = new List<float>();

            Matrix4x4 toLocal = target.transform.worldToLocalMatrix;

            foreach (MeshFilter filter in target.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null)
                    continue;

                Matrix4x4 toTarget = toLocal * filter.transform.localToWorldMatrix;
                Vector3[] source = mesh.vertices;
                var local = new Vector3[source.Length];
                for (int i = 0; i < source.Length; i++)
                {
                    local[i] = toTarget.MultiplyPoint3x4(source[i]);
                    vertices.Add(local[i]);
                }

                for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                {
                    int[] triangles = mesh.GetTriangles(subMesh);
                    for (int t = 0; t + 2 < triangles.Length; t += 3)
                    {
                        Vector3 cross = Vector3.Cross(
                            local[triangles[t + 1]] - local[triangles[t]],
                            local[triangles[t + 2]] - local[triangles[t]]);

                        // Vector3.normalized returns zero below its own epsilon, which would
                        // otherwise enter the scatter matrix as a spurious sample.
                        Vector3 normal = cross.normalized;
                        if (normal == Vector3.zero)
                            continue;

                        normals.Add(normal);
                        areas.Add(cross.magnitude * 0.5f);
                    }
                }
            }

            return vertices.Count > 0 && normals.Count > 0;
        }

        /// <summary>
        /// A symmetric 3x3 accumulator with just enough eigen-solving for the wall fit.
        /// Unity ships no such type, and the alternative is pulling in a linear algebra
        /// package for one 3x3 decomposition per generated asset.
        /// </summary>
        private struct SymmetricMatrix3
        {
            private float _xx, _yy, _zz, _xy, _xz, _yz;

            public void AddOuterProduct(Vector3 v, float weight)
            {
                _xx += v.x * v.x * weight;
                _yy += v.y * v.y * weight;
                _zz += v.z * v.z * weight;
                _xy += v.x * v.y * weight;
                _xz += v.x * v.z * weight;
                _yz += v.y * v.z * weight;
            }

            public void Scale(float factor)
            {
                _xx *= factor; _yy *= factor; _zz *= factor;
                _xy *= factor; _xz *= factor; _yz *= factor;
            }

            /// <summary>
            /// Cyclic Jacobi rotation. Converges in a handful of sweeps for a 3x3 and, unlike
            /// the closed form via the characteristic polynomial, stays stable when two
            /// eigenvalues are near-equal, which is exactly the degenerate wall case.
            /// </summary>
            public void SolveEigen(out Vector3 smallestVector, out float smallestValue, out float middleValue)
            {
                const int maxSweeps = 24;
                const float tolerance = 1e-12f;

                var a = new[,] { { _xx, _xy, _xz }, { _xy, _yy, _yz }, { _xz, _yz, _zz } };
                var v = new[,] { { 1f, 0f, 0f }, { 0f, 1f, 0f }, { 0f, 0f, 1f } };

                for (int sweep = 0; sweep < maxSweeps; sweep++)
                {
                    float offDiagonal = Mathf.Abs(a[0, 1]) + Mathf.Abs(a[0, 2]) + Mathf.Abs(a[1, 2]);
                    if (offDiagonal < tolerance)
                        break;

                    for (int p = 0; p < 2; p++)
                    {
                        for (int q = p + 1; q < 3; q++)
                        {
                            if (Mathf.Abs(a[p, q]) < tolerance)
                                continue;

                            float theta = (a[q, q] - a[p, p]) / (2f * a[p, q]);
                            float sign = theta >= 0f ? 1f : -1f;
                            float t = sign / (Mathf.Abs(theta) + Mathf.Sqrt(theta * theta + 1f));
                            float cos = 1f / Mathf.Sqrt(t * t + 1f);
                            float sin = t * cos;

                            for (int k = 0; k < 3; k++)
                            {
                                float akp = a[k, p];
                                float akq = a[k, q];
                                a[k, p] = cos * akp - sin * akq;
                                a[k, q] = sin * akp + cos * akq;
                            }

                            for (int k = 0; k < 3; k++)
                            {
                                float apk = a[p, k];
                                float aqk = a[q, k];
                                a[p, k] = cos * apk - sin * aqk;
                                a[q, k] = sin * apk + cos * aqk;

                                float vkp = v[k, p];
                                float vkq = v[k, q];
                                v[k, p] = cos * vkp - sin * vkq;
                                v[k, q] = sin * vkp + cos * vkq;
                            }
                        }
                    }
                }

                int smallest = 0;
                for (int i = 1; i < 3; i++)
                {
                    if (a[i, i] < a[smallest, smallest])
                        smallest = i;
                }

                smallestValue = a[smallest, smallest];
                smallestVector = new Vector3(v[0, smallest], v[1, smallest], v[2, smallest]).normalized;

                middleValue = float.PositiveInfinity;
                for (int i = 0; i < 3; i++)
                {
                    if (i != smallest)
                        middleValue = Mathf.Min(middleValue, a[i, i]);
                }
            }
        }
    }
}
