using System.Collections.Generic;
using UnityEngine;

namespace SpatialGeneration.Agent
{
    /// <summary>Primitive shapes the MCP agent assembles assets from.</summary>
    public enum AgentPartShape
    {
        Box = 0,

        /// <summary>Triangular prism: ridge along local X at the top, faces sloping down towards ±Z.</summary>
        Wedge = 1,

        /// <summary>Square base, apex at the top centre.</summary>
        Pyramid = 2,

        /// <summary>N-sided prism along local Y.</summary>
        Cylinder = 3,

        /// <summary>N-sided base, apex at the top centre.</summary>
        Cone = 4,

        /// <summary>Faceted sphere with N segments around Y.</summary>
        Sphere = 5
    }

    /// <summary>
    /// Builds flat-shaded unit meshes, each fitting the cube [-0.5, 0.5]^3, so a part's size is
    /// simply its local scale. Meshes are shared per shape and never saved: parts rebuild them
    /// on enable, so scenes stay small and survive domain reloads.
    /// </summary>
    public static class LowPolyMeshes
    {
        private static readonly Dictionary<(AgentPartShape, int), Mesh> Cache = new();
        private static Material _sharedMaterial;

        public static int ClampSegments(int segments) => Mathf.Clamp(segments, 3, 32);

        public static bool UsesSegments(AgentPartShape shape) =>
            shape == AgentPartShape.Cylinder || shape == AgentPartShape.Cone || shape == AgentPartShape.Sphere;

        public static Mesh Get(AgentPartShape shape, int segments)
        {
            int key = UsesSegments(shape) ? ClampSegments(segments) : 0;
            if (Cache.TryGetValue((shape, key), out Mesh cached) && cached != null)
                return cached;

            Mesh mesh = Build(shape, key);
            mesh.name = $"AgentPart_{shape}_{key}";
            mesh.hideFlags = HideFlags.DontSave;
            Cache[(shape, key)] = mesh;
            return mesh;
        }

        /// <summary>One flat-coloured material for every part; colour comes from a property block.</summary>
        public static Material SharedMaterial
        {
            get
            {
                if (_sharedMaterial != null)
                    return _sharedMaterial;

                Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                                ?? Shader.Find("Universal Render Pipeline/Simple Lit")
                                ?? Shader.Find("Standard");
                _sharedMaterial = new Material(shader)
                {
                    name = "AgentPart_Shared",
                    hideFlags = HideFlags.DontSave,
                    enableInstancing = true
                };
                if (_sharedMaterial.HasProperty("_Smoothness"))
                    _sharedMaterial.SetFloat("_Smoothness", 0.1f);
                if (_sharedMaterial.HasProperty("_Glossiness"))
                    _sharedMaterial.SetFloat("_Glossiness", 0.1f);
                return _sharedMaterial;
            }
        }

        private static Mesh Build(AgentPartShape shape, int segments)
        {
            var builder = new FlatMeshBuilder();
            switch (shape)
            {
                case AgentPartShape.Wedge:
                    BuildWedge(builder);
                    break;
                case AgentPartShape.Pyramid:
                    BuildPyramid(builder);
                    break;
                case AgentPartShape.Cylinder:
                    BuildCylinder(builder, segments);
                    break;
                case AgentPartShape.Cone:
                    BuildCone(builder, segments);
                    break;
                case AgentPartShape.Sphere:
                    BuildSphere(builder, segments);
                    break;
                default:
                    BuildBox(builder);
                    break;
            }

            return builder.ToMesh();
        }

        private static void BuildBox(FlatMeshBuilder b)
        {
            Vector3 c(float x, float y, float z) => new(x * 0.5f, y * 0.5f, z * 0.5f);
            b.Quad(c(-1, -1, -1), c(-1, 1, -1), c(1, 1, -1), c(1, -1, -1));
            b.Quad(c(-1, -1, 1), c(1, -1, 1), c(1, 1, 1), c(-1, 1, 1));
            b.Quad(c(-1, -1, -1), c(-1, -1, 1), c(-1, 1, 1), c(-1, 1, -1));
            b.Quad(c(1, -1, -1), c(1, 1, -1), c(1, 1, 1), c(1, -1, 1));
            b.Quad(c(-1, 1, -1), c(-1, 1, 1), c(1, 1, 1), c(1, 1, -1));
            b.Quad(c(-1, -1, -1), c(1, -1, -1), c(1, -1, 1), c(-1, -1, 1));
        }

        private static void BuildWedge(FlatMeshBuilder b)
        {
            const float h = 0.5f;
            Vector3 l0 = new(-h, -h, -h), l1 = new(-h, -h, h), lTop = new(-h, h, 0f);
            Vector3 r0 = new(h, -h, -h), r1 = new(h, -h, h), rTop = new(h, h, 0f);
            b.Tri(l0, l1, lTop);
            b.Tri(r0, rTop, r1);
            b.Quad(l0, r0, r1, l1);
            b.Quad(l0, lTop, rTop, r0);
            b.Quad(l1, r1, rTop, lTop);
        }

        private static void BuildPyramid(FlatMeshBuilder b)
        {
            const float h = 0.5f;
            Vector3 apex = new(0f, h, 0f);
            Vector3 a = new(-h, -h, -h), c = new(h, -h, -h), d = new(h, -h, h), e = new(-h, -h, h);
            b.Quad(a, c, d, e);
            b.Tri(a, apex, c);
            b.Tri(c, apex, d);
            b.Tri(d, apex, e);
            b.Tri(e, apex, a);
        }

        private static Vector3 Ring(int i, int segments, float y)
        {
            float angle = Mathf.PI * 2f * i / segments;
            return new Vector3(Mathf.Cos(angle) * 0.5f, y, Mathf.Sin(angle) * 0.5f);
        }

        private static void BuildCylinder(FlatMeshBuilder b, int segments)
        {
            Vector3 top = new(0f, 0.5f, 0f), bottom = new(0f, -0.5f, 0f);
            for (int i = 0; i < segments; i++)
            {
                Vector3 b0 = Ring(i, segments, -0.5f), b1 = Ring(i + 1, segments, -0.5f);
                Vector3 t0 = Ring(i, segments, 0.5f), t1 = Ring(i + 1, segments, 0.5f);
                b.Quad(b0, t0, t1, b1);
                b.Tri(top, t1, t0);
                b.Tri(bottom, b0, b1);
            }
        }

        private static void BuildCone(FlatMeshBuilder b, int segments)
        {
            Vector3 apex = new(0f, 0.5f, 0f), bottom = new(0f, -0.5f, 0f);
            for (int i = 0; i < segments; i++)
            {
                Vector3 b0 = Ring(i, segments, -0.5f), b1 = Ring(i + 1, segments, -0.5f);
                b.Tri(b0, apex, b1);
                b.Tri(bottom, b0, b1);
            }
        }

        private static void BuildSphere(FlatMeshBuilder b, int segments)
        {
            int rings = Mathf.Max(2, segments / 2);
            for (int r = 0; r < rings; r++)
            {
                float phi0 = Mathf.PI * r / rings, phi1 = Mathf.PI * (r + 1) / rings;
                for (int s = 0; s < segments; s++)
                {
                    Vector3 p00 = SpherePoint(phi0, s, segments), p01 = SpherePoint(phi0, s + 1, segments);
                    Vector3 p10 = SpherePoint(phi1, s, segments), p11 = SpherePoint(phi1, s + 1, segments);
                    if (r == 0)
                        b.Tri(p00, p10, p11);
                    else if (r == rings - 1)
                        b.Tri(p00, p10, p01);
                    else
                        b.Quad(p00, p10, p11, p01);
                }
            }
        }

        private static Vector3 SpherePoint(float phi, int s, int segments)
        {
            float theta = Mathf.PI * 2f * s / segments;
            float y = Mathf.Cos(phi) * 0.5f;
            float radius = Mathf.Sin(phi) * 0.5f;
            return new Vector3(Mathf.Cos(theta) * radius, y, Mathf.Sin(theta) * radius);
        }

        /// <summary>
        /// Collects triangles with their own vertices so every face gets a flat normal. Winding is
        /// fixed per triangle so its normal points away from the origin, which lies inside every
        /// shape above; callers therefore need not track vertex order.
        /// </summary>
        private sealed class FlatMeshBuilder
        {
            private readonly List<Vector3> _vertices = new();
            private readonly List<Vector3> _normals = new();
            private readonly List<int> _triangles = new();

            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                Tri(a, b, c);
                Tri(a, c, d);
            }

            public void Tri(Vector3 a, Vector3 b, Vector3 c)
            {
                Vector3 normal = Vector3.Cross(b - a, c - a);
                if (normal.sqrMagnitude < 1e-12f)
                    return;

                Vector3 centroid = (a + b + c) / 3f;
                if (Vector3.Dot(normal, centroid) < 0f)
                {
                    (b, c) = (c, b);
                    normal = -normal;
                }

                normal.Normalize();
                int start = _vertices.Count;
                _vertices.Add(a);
                _vertices.Add(b);
                _vertices.Add(c);
                _normals.Add(normal);
                _normals.Add(normal);
                _normals.Add(normal);
                _triangles.Add(start);
                _triangles.Add(start + 1);
                _triangles.Add(start + 2);
            }

            public Mesh ToMesh()
            {
                var mesh = new Mesh();
                mesh.SetVertices(_vertices);
                mesh.SetNormals(_normals);
                mesh.SetTriangles(_triangles, 0);
                mesh.RecalculateBounds();
                return mesh;
            }
        }
    }
}
