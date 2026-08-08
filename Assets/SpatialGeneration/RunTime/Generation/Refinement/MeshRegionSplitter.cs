using System.Collections.Generic;
using UnityEngine;

namespace SpatialGeneration.Generation.Refinement
{
    /// <summary>
    /// Splits a mesh against the user's selection box so a refinement can replace only the
    /// selected volume.
    ///
    /// This is what makes a local edit actually local: the triangles outside the box are
    /// carried over untouched, vertex for vertex, so the scene outside the mask is provably
    /// unchanged rather than re-derived from a fresh reconstruction.
    /// </summary>
    public static class MeshRegionSplitter
    {
        /// <summary>
        /// Builds a copy of <paramref name="source"/> containing only the triangles whose
        /// centroid falls outside <paramref name="region"/>.
        /// </summary>
        /// <param name="source">Mesh to split. Not modified.</param>
        /// <param name="localToWorld">Transform placing <paramref name="source"/> in world space.</param>
        /// <param name="region">Selection box, in world space.</param>
        /// <param name="removedTriangles">How many triangles fell inside the region.</param>
        /// <returns>
        /// The outside-only mesh, or null when every triangle is inside the region (in which
        /// case the caller should drop the object rather than keep an empty mesh).
        /// </returns>
        public static Mesh BuildOutsideMesh(
            Mesh source,
            Matrix4x4 localToWorld,
            RegionSelection region,
            out int removedTriangles)
        {
            removedTriangles = 0;
            if (source == null || region == null)
                return null;

            Vector3[] vertices = source.vertices;
            if (vertices.Length == 0)
                return null;

            // Classify once per vertex, then test triangles by centroid. Working in the
            // region's local frame turns the oriented box into a plain min/max test.
            Matrix4x4 worldToRegion = region.WorldToLocal();
            Vector3 halfExtents = RegionSelection.ClampSize(region.size) * 0.5f;
            var regionSpace = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
                regionSpace[i] = worldToRegion.MultiplyPoint3x4(localToWorld.MultiplyPoint3x4(vertices[i]));

            var keptIndexByOriginal = new Dictionary<int, int>(vertices.Length);
            var keptOriginalIndices = new List<int>(vertices.Length);
            var keptSubMeshes = new List<List<int>>(source.subMeshCount);

            for (int subMesh = 0; subMesh < source.subMeshCount; subMesh++)
            {
                int[] triangles = source.GetTriangles(subMesh);
                var kept = new List<int>(triangles.Length);

                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    int a = triangles[t];
                    int b = triangles[t + 1];
                    int c = triangles[t + 2];

                    Vector3 centroid = (regionSpace[a] + regionSpace[b] + regionSpace[c]) / 3f;
                    if (IsInside(centroid, halfExtents))
                    {
                        removedTriangles++;
                        continue;
                    }

                    kept.Add(Remap(a, keptIndexByOriginal, keptOriginalIndices));
                    kept.Add(Remap(b, keptIndexByOriginal, keptOriginalIndices));
                    kept.Add(Remap(c, keptIndexByOriginal, keptOriginalIndices));
                }

                keptSubMeshes.Add(kept);
            }

            if (keptOriginalIndices.Count == 0)
                return null;
            if (removedTriangles == 0)
                return null; // Nothing to split: caller can keep the original mesh as-is.

            return BuildMesh(source, keptOriginalIndices, keptSubMeshes);
        }

        private static bool IsInside(Vector3 regionSpacePoint, Vector3 halfExtents) =>
            Mathf.Abs(regionSpacePoint.x) <= halfExtents.x &&
            Mathf.Abs(regionSpacePoint.y) <= halfExtents.y &&
            Mathf.Abs(regionSpacePoint.z) <= halfExtents.z;

        private static int Remap(int originalIndex, Dictionary<int, int> map, List<int> order)
        {
            if (map.TryGetValue(originalIndex, out int mapped))
                return mapped;

            mapped = order.Count;
            map[originalIndex] = mapped;
            order.Add(originalIndex);
            return mapped;
        }

        private static Mesh BuildMesh(Mesh source, List<int> keptOriginalIndices, List<List<int>> subMeshes)
        {
            var mesh = new Mesh
            {
                name = $"{source.name}_Outside",
                // Reconstructions routinely exceed 65k vertices.
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
            };

            mesh.SetVertices(Gather(source.vertices, keptOriginalIndices));

            Vector3[] normals = source.normals;
            if (normals.Length == source.vertexCount)
                mesh.SetNormals(Gather(normals, keptOriginalIndices));

            Vector4[] tangents = source.tangents;
            if (tangents.Length == source.vertexCount)
                mesh.SetTangents(Gather(tangents, keptOriginalIndices));

            Color[] colors = source.colors;
            if (colors.Length == source.vertexCount)
                mesh.SetColors(Gather(colors, keptOriginalIndices));

            for (int channel = 0; channel < 4; channel++)
            {
                var uvs = new List<Vector2>();
                source.GetUVs(channel, uvs);
                if (uvs.Count == source.vertexCount)
                    mesh.SetUVs(channel, Gather(uvs.ToArray(), keptOriginalIndices));
            }

            mesh.subMeshCount = subMeshes.Count;
            for (int i = 0; i < subMeshes.Count; i++)
                mesh.SetTriangles(subMeshes[i], i, calculateBounds: false);

            mesh.RecalculateBounds();
            if (normals.Length != source.vertexCount)
                mesh.RecalculateNormals();

            return mesh;
        }

        private static List<T> Gather<T>(T[] source, List<int> indices)
        {
            var gathered = new List<T>(indices.Count);
            foreach (int index in indices)
                gathered.Add(source[index]);
            return gathered;
        }
    }
}
