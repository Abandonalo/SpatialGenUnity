using System.Collections.Generic;
using UnityEngine;

namespace SpatialGeneration.Generation
{
    /// <summary>
    /// Levels a reconstructed mesh so it sits flat and square in the scene.
    ///
    /// A single-view lifter reconstructs in the camera's frame, so the mesh inherits
    /// whatever angle the source image was drawn from. Asset renders are usually
    /// three-quarter views from slightly above, which arrives in Unity as a model that is
    /// both tilted and turned. Prompting for a dead-on front view fights the style that
    /// makes the image liftable in the first place, so the orientation is corrected here
    /// instead, from the geometry itself.
    /// </summary>
    public static class MeshAlignment
    {
        /// <summary>Normals within this of vertical are candidates for the base plane.</summary>
        private const float VerticalToleranceDegrees = 55f;

        /// <summary>Ignore slivers, which have unstable normals.</summary>
        private const float MinTriangleArea = 1e-7f;

        /// <summary>
        /// Rotates <paramref name="target"/> so its dominant flat face points up and its
        /// footprint squares to the world axes.
        /// </summary>
        /// <returns>
        /// The correction applied, relative to the rotation the object had on entry. Callers
        /// that go on to orient the object must compose with this rather than overwrite
        /// <c>transform.rotation</c>, or the levelling is silently discarded.
        /// </returns>
        public static Quaternion Level(GameObject target)
        {
            if (target == null)
                return Quaternion.identity;

            Quaternion before = target.transform.rotation;

            List<Vector3> vertices = new();
            List<Vector3> normals = new();
            List<float> areas = new();
            if (!CollectTriangles(target, vertices, normals, areas))
                return Quaternion.identity;

            if (TryFindGroundNormal(normals, areas, out Vector3 groundNormal))
                target.transform.rotation = Quaternion.FromToRotation(groundNormal, Vector3.up) * target.transform.rotation;

            ApplyYaw(target, vertices);
            return target.transform.rotation * Quaternion.Inverse(before);
        }

        /// <summary>
        /// The mesh's dominant near-horizontal face, expressed as an upward normal.
        ///
        /// Generated assets almost always have a large flat base or roof; whichever has the
        /// most surface area is the best evidence of which way is up. Faces pointing down
        /// are flipped first so a base and a roof reinforce each other rather than cancel.
        /// </summary>
        private static bool TryFindGroundNormal(List<Vector3> normals, List<float> areas, out Vector3 groundNormal)
        {
            groundNormal = Vector3.up;
            float cosTolerance = Mathf.Cos(VerticalToleranceDegrees * Mathf.Deg2Rad);

            Vector3 accumulated = Vector3.zero;
            float total = 0f;

            for (int i = 0; i < normals.Count; i++)
            {
                Vector3 normal = normals[i];
                float alignment = Vector3.Dot(normal, Vector3.up);
                if (Mathf.Abs(alignment) < cosTolerance)
                    continue;

                // Treat up- and down-facing horizontals as the same plane orientation.
                accumulated += (alignment < 0f ? -normal : normal) * areas[i];
                total += areas[i];
            }

            if (total <= 0f || accumulated.sqrMagnitude < 1e-8f)
                return false;

            groundNormal = accumulated.normalized;
            return true;
        }

        /// <summary>
        /// Turns the object about Y to the angle whose footprint is smallest.
        ///
        /// The minimum-area bounding rectangle is the natural square-on orientation for the
        /// box-like assets this pipeline produces, and it needs no knowledge of which side
        /// is the front.
        /// </summary>
        private static void ApplyYaw(GameObject target, List<Vector3> worldVertices)
        {
            const float sweepDegrees = 90f;
            const float coarseStep = 2f;

            float bestAngle = 0f;
            float bestArea = float.PositiveInfinity;

            for (float angle = 0f; angle < sweepDegrees; angle += coarseStep)
            {
                float area = FootprintArea(worldVertices, angle);
                if (area < bestArea)
                {
                    bestArea = area;
                    bestAngle = angle;
                }
            }

            // Refine around the coarse winner rather than sweeping finely throughout.
            for (float angle = bestAngle - coarseStep; angle <= bestAngle + coarseStep; angle += 0.25f)
            {
                float area = FootprintArea(worldVertices, angle);
                if (area < bestArea)
                {
                    bestArea = area;
                    bestAngle = angle;
                }
            }

            if (!Mathf.Approximately(bestAngle, 0f))
                target.transform.rotation = Quaternion.Euler(0f, bestAngle, 0f) * target.transform.rotation;
        }

        private static float FootprintArea(List<Vector3> worldVertices, float yawDegrees)
        {
            float radians = yawDegrees * Mathf.Deg2Rad;
            float cos = Mathf.Cos(radians);
            float sin = Mathf.Sin(radians);

            float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
            float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;

            foreach (Vector3 vertex in worldVertices)
            {
                float x = vertex.x * cos - vertex.z * sin;
                float z = vertex.x * sin + vertex.z * cos;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (z < minZ) minZ = z;
                if (z > maxZ) maxZ = z;
            }

            return (maxX - minX) * (maxZ - minZ);
        }

        /// <summary>World-space vertices, plus per-triangle normals and areas.</summary>
        private static bool CollectTriangles(
            GameObject target, List<Vector3> vertices, List<Vector3> normals, List<float> areas)
        {
            foreach (MeshFilter filter in target.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null)
                    continue;

                Matrix4x4 toWorld = filter.transform.localToWorldMatrix;
                Vector3[] local = mesh.vertices;
                var world = new Vector3[local.Length];
                for (int i = 0; i < local.Length; i++)
                {
                    world[i] = toWorld.MultiplyPoint3x4(local[i]);
                    vertices.Add(world[i]);
                }

                for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                {
                    int[] triangles = mesh.GetTriangles(subMesh);
                    for (int t = 0; t + 2 < triangles.Length; t += 3)
                    {
                        Vector3 cross = Vector3.Cross(
                            world[triangles[t + 1]] - world[triangles[t]],
                            world[triangles[t + 2]] - world[triangles[t]]);

                        float area = cross.magnitude * 0.5f;
                        if (area < MinTriangleArea)
                            continue;

                        normals.Add(cross.normalized);
                        areas.Add(area);
                    }
                }
            }

            return vertices.Count > 0 && normals.Count > 0;
        }
    }
}
