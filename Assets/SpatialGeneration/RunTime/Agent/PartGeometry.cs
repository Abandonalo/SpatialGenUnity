using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SpatialGeneration.Agent
{
    // Each cell is a closed, convex triangle soup in the part's local coordinates.
    // Retaining cells lets later edits cut the result again, including after a scene reload.
    [Serializable]
    public sealed class PartCell
    {
        public Vector3[] triangles;

        public double Volume()
        {
            double sum = 0;
            Vector3 origin = triangles[0];
            for (int i = 0; i < triangles.Length; i += 3)
                sum += Vector3.Dot(triangles[i] - origin,
                    Vector3.Cross(triangles[i + 1] - origin, triangles[i + 2] - origin));
            return Math.Abs(sum) / 6;
        }
    }

    public sealed class PartCut
    {
        public readonly List<PartCell> inside = new();
        public readonly List<PartCell> outside = new();
        public float coverage;
    }

    public static class PartGeometry
    {
        public static List<PartCell> Cells(AgentPart part)
        {
            if (part.fragments != null && part.fragments.Count > 0) return part.fragments;
            Mesh mesh = LowPolyMeshes.Get(part.shape, part.segments);
            var vertices = mesh.vertices;
            var indices = mesh.triangles;
            var triangles = new Vector3[indices.Length];
            for (int i = 0; i < indices.Length; i++) triangles[i] = vertices[indices[i]];
            return new List<PartCell> { new() { triangles = triangles } };
        }

        public static Mesh BuildMesh(List<PartCell> cells)
        {
            var vertices = new List<Vector3>();
            foreach (PartCell cell in cells) vertices.AddRange(cell.triangles);
            var indices = new int[vertices.Count];
            for (int i = 0; i < indices.Length; i++) indices[i] = i;
            var mesh = new Mesh { name = "Refined part", hideFlags = HideFlags.DontSave,
                indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateNormals();
            return mesh;
        }

        public static PartCut Cut(AgentPart part, RegionSelection box)
        {
            Matrix4x4 matrix = box.WorldToLocal() * part.transform.localToWorldMatrix;
            return Cut(part, matrix, box.size * 0.5f);
        }

        public static PartCut Cut(AgentPart part, Matrix4x4 matrix, Vector3 half)
        {
            var result = new PartCut();
            double volume = 0, inside = 0;
            foreach (PartCell source in Cells(part))
            {
                volume += source.Volume();
                PartCell remaining = source;
                for (int axis = 0; axis < 3 && remaining != null; axis++)
                for (int sign = -1; sign <= 1 && remaining != null; sign += 2)
                {
                    Vector3 normal = new(matrix[axis,0], matrix[axis,1], matrix[axis,2]);
                    float offset = half[axis] - sign * matrix[axis,3];
                    Split(remaining, sign * normal, offset, out remaining, out PartCell outside);
                    if (outside != null) result.outside.Add(outside);
                }
                if (remaining == null) continue;
                result.inside.Add(remaining);
                inside += remaining.Volume();
            }
            result.coverage = volume > 0 ? Mathf.Clamp01((float)(inside / volume)) : 0;
            return result;
        }

        // Clip every face, then close both halves with oppositely wound copies of the cut face.
        private static void Split(PartCell cell, Vector3 normal, float offset, out PartCell inside, out PartCell outside)
        {
            Vector3[] vertices = cell.triangles;
            float tolerance = normal.magnitude * 1e-6f;
            bool above = false, below = false;
            foreach (Vector3 v in vertices)
            {
                float d = Vector3.Dot(normal, v) - offset;
                above |= d > tolerance;
                below |= d < -tolerance;
            }
            if (!above || !below)
            {
                inside = above ? null : cell;
                outside = above ? cell : null;
                return;
            }

            var negative = new List<Vector3>();
            var positive = new List<Vector3>();
            var seam = new List<Vector3>();
            var left = new List<Vector3>(4);
            var right = new List<Vector3>(4);
            for (int i = 0; i < vertices.Length; i += 3)
            {
                left.Clear();
                right.Clear();
                for (int corner = 0; corner < 3; corner++)
                {
                    Vector3 a = vertices[i + corner], b = vertices[i + (corner + 1) % 3];
                    float da = Vector3.Dot(normal, a) - offset, db = Vector3.Dot(normal, b) - offset;
                    if (Mathf.Abs(da) <= tolerance) da = 0;
                    if (Mathf.Abs(db) <= tolerance) db = 0;
                    if (da <= 0) left.Add(a);
                    if (da >= 0) right.Add(a);
                    if (da == 0) seam.Add(a);
                    if (da * db >= 0) continue;
                    Vector3 hit = Vector3.LerpUnclamped(a, b, da / (da - db));
                    left.Add(hit);
                    right.Add(hit);
                    seam.Add(hit);
                }
                Fan(left, negative);
                Fan(right, positive);
            }
            // For a convex solid all intersections lie on the perimeter. Sorting them
            // around an interior point also handles cuts through existing vertices.
            var unique = new List<Vector3>();
            foreach (Vector3 vertex in seam)
                if (!unique.Exists(p => (p - vertex).sqrMagnitude < 1e-12f)) unique.Add(vertex);
            if (unique.Count >= 3)
            {
                Vector3 center = Vector3.zero;
                foreach (Vector3 vertex in unique) center += vertex;
                center /= unique.Count;
                Vector3 u = (unique[0] - center).normalized;
                Vector3 v = Vector3.Cross(normal.normalized, u);
                float Angle(Vector3 p) => Mathf.Atan2(Vector3.Dot(p - center, v), Vector3.Dot(p - center, u));
                unique.Sort((a, b) => Angle(a).CompareTo(Angle(b)));
                Fan(unique, negative);
                unique.Reverse();
                Fan(unique, positive);
            }
            inside = negative.Count > 0 ? new PartCell { triangles = negative.ToArray() } : null;
            outside = positive.Count > 0 ? new PartCell { triangles = positive.ToArray() } : null;
        }

        private static void Fan(List<Vector3> face, List<Vector3> vertices)
        {
            for (int i = 2; i < face.Count; i++)
            {
                if (Vector3.Cross(face[i-1] - face[0], face[i] - face[0]).sqrMagnitude < 1e-24f) continue;
                vertices.Add(face[0]);
                vertices.Add(face[i-1]);
                vertices.Add(face[i]);
            }
        }
    }
}
