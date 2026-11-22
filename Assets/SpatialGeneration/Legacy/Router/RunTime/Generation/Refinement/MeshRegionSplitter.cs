using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialGeneration.Generation.Refinement
{
    public enum RegionBoundaryFace
    {
        PositiveX,
        NegativeX,
        PositiveY,
        NegativeY,
        PositiveZ,
        NegativeZ
    }

    /// <summary>A closed intersection curve between a source surface and the OBB.</summary>
    public sealed class RegionBoundaryLoop
    {
        public RegionBoundaryFace face;
        public readonly HashSet<RegionBoundaryFace> faces = new();
        public readonly List<Vector3> worldPoints = new();
        public bool isClosed;
    }

    public sealed class MeshCutResult
    {
        public Mesh outsideMesh;
        public Mesh insideMesh;
        public int removedTriangles;
        public int keptTriangles;
        public readonly List<RegionBoundaryLoop> boundaryLoops = new();
        public bool hasOpenBoundary;
    }

    /// <summary>Exact triangle/OBB clipping for local refinement.</summary>
    public static class MeshRegionSplitter
    {
        // Compared against cross.sqrMagnitude (four times area squared), not area itself.
        // TripoSR commonly emits sub-millimetre triangles after fitting; 1e-10 discarded a
        // legitimate strip along the cut and moved the apparent boundary several millimetres.
        private const float MinTriangleCrossSquared = 1e-16f;

        /// <summary>
        /// Backward-compatible wrapper. Unlike the former centroid classifier, triangles that
        /// cross the box are split at the six planes and only their outside fragments survive.
        /// </summary>
        public static Mesh BuildOutsideMesh(
            Mesh source,
            Matrix4x4 localToWorld,
            RegionSelection region,
            out int removedTriangles)
        {
            MeshCutResult result = Cut(source, localToWorld, region);
            removedTriangles = result.removedTriangles;
            return result.outsideMesh;
        }

        public static MeshCutResult Cut(
            Mesh source,
            Matrix4x4 localToWorld,
            RegionSelection region)
            => Cut(source, localToWorld, region, activeFaces: null);

        /// <summary>
        /// Clips against only the requested OBB faces. Source removal uses all six; a
        /// replacement uses only faces carrying a source seam so unrelated sides stay closed.
        /// </summary>
        public static MeshCutResult Cut(
            Mesh source,
            Matrix4x4 localToWorld,
            RegionSelection region,
            IReadOnlyCollection<RegionBoundaryFace> activeFaces)
        {
            var result = new MeshCutResult();
            if (source == null || region == null || source.vertexCount == 0)
                return result;

            Vector3[] positions = source.vertices;
            Vector3[] normals = source.normals;
            Vector4[] tangents = source.tangents;
            Color[] colors = source.colors;
            Vector2[][] uv = ReadUvs(source);
            bool hasNormals = normals.Length == source.vertexCount;
            bool hasTangents = tangents.Length == source.vertexCount;
            bool hasColors = colors.Length == source.vertexCount;
            bool[] hasUv = { uv[0].Length == source.vertexCount, uv[1].Length == source.vertexCount,
                             uv[2].Length == source.vertexCount, uv[3].Length == source.vertexCount };

            Matrix4x4 worldToRegion = region.WorldToLocal();
            Matrix4x4 localToRegion = worldToRegion * localToWorld;
            Vector3 half = RegionSelection.ClampSize(region.size) * 0.5f;
            float tolerance = Mathf.Max(1e-6f, Mathf.Min(half.x, Mathf.Min(half.y, half.z)) * 1e-4f);
            bool[] activePlanes = ActivePlanes(activeFaces);

            var builder = new MeshBuilder(
                source.subMeshCount, hasNormals, hasTangents, hasColors, hasUv, $"{source.name}_Outside");
            var insideBuilder = new MeshBuilder(
                source.subMeshCount, hasNormals, hasTangents, hasColors, hasUv, $"{source.name}_Inside");
            var segments = new List<BoundarySegment>();

            for (int subMesh = 0; subMesh < source.subMeshCount; subMesh++)
            {
                int[] triangles = source.GetTriangles(subMesh);
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    var polygon = new List<VertexData>(3)
                    {
                        Vertex(triangles[t], positions, normals, tangents, colors, uv,
                            hasNormals, hasTangents, hasColors, hasUv, localToRegion),
                        Vertex(triangles[t + 1], positions, normals, tangents, colors, uv,
                            hasNormals, hasTangents, hasColors, hasUv, localToRegion),
                        Vertex(triangles[t + 2], positions, normals, tangents, colors, uv,
                            hasNormals, hasTangents, hasColors, hasUv, localToRegion)
                    };

                    var outsideFragments = new List<List<VertexData>>(6);
                    for (int plane = 0; plane < 6 && polygon.Count >= 3; plane++)
                    {
                        if (!activePlanes[plane])
                            continue;
                        SplitAgainstPlane(polygon, plane, half, tolerance,
                            out List<VertexData> inside, out List<VertexData> outside);
                        if (outside.Count >= 3)
                            outsideFragments.Add(outside);
                        polygon = inside;
                    }

                    bool intersectsRegion = polygon.Count >= 3;
                    if (intersectsRegion)
                    {
                        result.removedTriangles++;
                        insideBuilder.AddPolygon(polygon, subMesh);
                        CollectBoundarySegments(
                            polygon, half, tolerance, localToWorld, activePlanes, segments);
                    }

                    foreach (List<VertexData> fragment in outsideFragments)
                        builder.AddPolygon(intersectsRegion
                            ? InsertBoundaryVertices(fragment, polygon, tolerance * 4f)
                            : fragment, subMesh);
                }
            }

            if (result.removedTriangles == 0)
                return result;

            result.outsideMesh = builder.TriangleCount > 0 ? builder.Build() : null;
            result.insideMesh = insideBuilder.TriangleCount > 0 ? insideBuilder.Build() : null;
            result.keptTriangles = builder.TriangleCount;
            // Adjacent source triangles reach the same plane intersection through separate
            // floating-point calculations. A slightly wider weld tolerance closes those
            // numerical cracks without moving the exact cut vertices stored in the meshes.
            float loopTolerance = tolerance * 4f;
            BuildLoops(segments, loopTolerance, result.boundaryLoops, out bool open);
            result.hasOpenBoundary = open;
            if (!open && result.boundaryLoops.Count > 0)
            {
                float repairTolerance = loopTolerance * 8f;
                SnapBoundaryVertices(
                    result.outsideMesh, localToWorld, worldToRegion, half,
                    activePlanes, result.boundaryLoops, repairTolerance);
                SnapBoundaryVertices(
                    result.insideMesh, localToWorld, worldToRegion, half,
                    activePlanes, result.boundaryLoops, repairTolerance);
            }
            return result;
        }

        private static bool[] ActivePlanes(IReadOnlyCollection<RegionBoundaryFace> activeFaces)
        {
            var result = new bool[6];
            if (activeFaces == null)
            {
                for (int i = 0; i < result.Length; i++) result[i] = true;
                return result;
            }
            foreach (RegionBoundaryFace face in activeFaces)
                result[Mathf.Clamp((int)face, 0, result.Length - 1)] = true;
            return result;
        }

        private static VertexData Vertex(
            int index,
            Vector3[] positions,
            Vector3[] normals,
            Vector4[] tangents,
            Color[] colors,
            Vector2[][] uv,
            bool hasNormals,
            bool hasTangents,
            bool hasColors,
            bool[] hasUv,
            Matrix4x4 localToRegion)
        {
            Vector3 position = positions[index];
            return new VertexData
            {
                position = position,
                regionPosition = localToRegion.MultiplyPoint3x4(position),
                normal = hasNormals ? normals[index] : Vector3.zero,
                tangent = hasTangents ? tangents[index] : Vector4.zero,
                color = hasColors ? colors[index] : Color.white,
                uv0 = hasUv[0] ? uv[0][index] : Vector2.zero,
                uv1 = hasUv[1] ? uv[1][index] : Vector2.zero,
                uv2 = hasUv[2] ? uv[2][index] : Vector2.zero,
                uv3 = hasUv[3] ? uv[3][index] : Vector2.zero
            };
        }

        private static void SplitAgainstPlane(
            List<VertexData> polygon,
            int plane,
            Vector3 half,
            float tolerance,
            out List<VertexData> inside,
            out List<VertexData> outside)
        {
            inside = new List<VertexData>(polygon.Count + 2);
            outside = new List<VertexData>(polygon.Count + 2);
            for (int i = 0; i < polygon.Count; i++)
            {
                VertexData start = polygon[i];
                VertexData end = polygon[(i + 1) % polygon.Count];
                float startDistance = SignedDistance(start.regionPosition, plane, half);
                float endDistance = SignedDistance(end.regionPosition, plane, half);
                bool startInside = startDistance <= tolerance;
                bool endInside = endDistance <= tolerance;

                if (startInside && endInside)
                {
                    AddDistinct(inside, end, tolerance);
                }
                else if (startInside != endInside)
                {
                    float denominator = startDistance - endDistance;
                    float amount = Mathf.Abs(denominator) <= 1e-12f
                        ? 0.5f
                        : Mathf.Clamp01(startDistance / denominator);
                    VertexData intersection = VertexData.Lerp(start, end, amount);
                    AddDistinct(inside, intersection, tolerance);
                    AddDistinct(outside, intersection, tolerance);
                    if (endInside)
                        AddDistinct(inside, end, tolerance);
                    else
                        AddDistinct(outside, end, tolerance);
                }
                else
                {
                    AddDistinct(outside, end, tolerance);
                }
            }
            RemoveClosingDuplicate(inside, tolerance);
            RemoveClosingDuplicate(outside, tolerance);
        }

        private static float SignedDistance(Vector3 point, int plane, Vector3 half) => plane switch
        {
            0 => point.x - half.x,
            1 => -point.x - half.x,
            2 => point.y - half.y,
            3 => -point.y - half.y,
            4 => point.z - half.z,
            _ => -point.z - half.z
        };

        private static List<VertexData> InsertBoundaryVertices(
            List<VertexData> outside,
            List<VertexData> inside,
            float tolerance)
        {
            var result = new List<VertexData>(outside.Count + inside.Count);
            float toleranceSquared = tolerance * tolerance;
            for (int i = 0; i < outside.Count; i++)
            {
                VertexData start = outside[i];
                VertexData end = outside[(i + 1) % outside.Count];
                result.Add(start);
                Vector3 direction = end.regionPosition - start.regionPosition;
                float lengthSquared = direction.sqrMagnitude;
                if (lengthSquared <= toleranceSquared)
                    continue;

                var insertions = new List<(float amount, VertexData vertex)>();
                foreach (VertexData candidate in inside)
                {
                    float amount = Vector3.Dot(
                        candidate.regionPosition - start.regionPosition, direction) / lengthSquared;
                    if (amount <= 0f || amount >= 1f)
                        continue;
                    Vector3 closest = start.regionPosition + direction * amount;
                    if ((candidate.regionPosition - closest).sqrMagnitude > toleranceSquared)
                        continue;
                    insertions.Add((amount, candidate));
                }
                insertions.Sort((a, b) => a.amount.CompareTo(b.amount));
                foreach (var insertion in insertions)
                    AddDistinct(result, insertion.vertex, tolerance);
            }
            RemoveClosingDuplicate(result, tolerance);
            return result;
        }

        private static void CollectBoundarySegments(
            List<VertexData> insidePolygon,
            Vector3 half,
            float tolerance,
            Matrix4x4 localToWorld,
            bool[] activePlanes,
            List<BoundarySegment> segments)
        {
            for (int i = 0; i < insidePolygon.Count; i++)
            {
                VertexData a = insidePolygon[i];
                VertexData b = insidePolygon[(i + 1) % insidePolygon.Count];
                int face = CommonBoundaryFace(
                    a.regionPosition, b.regionPosition, half, tolerance * 2f, activePlanes);
                if (face < 0 || (a.regionPosition - b.regionPosition).sqrMagnitude <= tolerance * tolerance)
                    continue;

                // Use the exact transformed clipping vertices. Transition meshes reuse these
                // positions, so their boundary edges coincide with the cut source exactly.
                Vector3 worldA = localToWorld.MultiplyPoint3x4(a.position);
                Vector3 worldB = localToWorld.MultiplyPoint3x4(b.position);
                segments.Add(new BoundarySegment(worldA, worldB, (RegionBoundaryFace)face));
            }
        }

        private static int CommonBoundaryFace(
            Vector3 a, Vector3 b, Vector3 half, float tolerance, bool[] activePlanes)
        {
            for (int plane = 0; plane < 6; plane++)
            {
                if (!activePlanes[plane])
                    continue;
                if (Mathf.Abs(SignedDistance(a, plane, half)) <= tolerance &&
                    Mathf.Abs(SignedDistance(b, plane, half)) <= tolerance)
                    return plane;
            }
            return -1;
        }

        private static void BuildLoops(
            List<BoundarySegment> segments,
            float tolerance,
            List<RegionBoundaryLoop> loops,
            out bool hasOpenBoundary)
        {
            hasOpenBoundary = false;
            if (segments.Count == 0)
                return;

            // Polygon clipping reports one segment per clipped triangle. On an OBB face,
            // adjacent coplanar triangles can therefore report the same interior edge twice.
            // The old nearest-unused-segment walk entered those duplicate branches and then
            // stopped at a dead end, falsely declaring a closed source mesh open. Build an
            // actual welded edge graph and parity-cancel duplicate interior edges first.
            var nodes = new List<BoundaryNode>();
            var cells = new Dictionary<GridKey, List<int>>();
            var edgeLookup = new Dictionary<NodeEdgeKey, BoundaryEdge>();
            var edgeOrder = new List<BoundaryEdge>();

            foreach (BoundarySegment segment in segments)
            {
                int a = FindOrAddNode(segment.a, tolerance, nodes, cells);
                int b = FindOrAddNode(segment.b, tolerance, nodes, cells);
                if (a == b)
                    continue;

                var key = new NodeEdgeKey(a, b);
                if (!edgeLookup.TryGetValue(key, out BoundaryEdge edge))
                {
                    edge = new BoundaryEdge(key.a, key.b, segment.face);
                    edgeLookup.Add(key, edge);
                    edgeOrder.Add(edge);
                }
                edge.occurrences++;
                edge.faces.Add(segment.face);
            }

            var edges = new List<BoundaryEdge>();
            foreach (BoundaryEdge edge in edgeOrder)
            {
                // Two matching occurrences bound neighbouring clipped polygons and are not
                // part of the exterior cut. Odd multiplicity leaves one real boundary edge.
                if ((edge.occurrences & 1) == 0)
                    continue;
                int index = edges.Count;
                edges.Add(edge);
                nodes[edge.a].edges.Add(index);
                nodes[edge.b].edges.Add(index);
            }

            // Generated meshes can contain sub-millimetre cracks even when their index
            // topology is otherwise closed. Pair only unmatched contour endpoints, and only
            // when their incident edges lie on the same OBB face. Unlike globally increasing
            // the weld tolerance, this cannot collapse genuine short contour edges.
            RepairDegreeOneGaps(nodes, ref edges, tolerance * 8f);

            foreach (BoundaryNode node in nodes)
            {
                if (node.edges.Count != 0 && node.edges.Count != 2)
                    hasOpenBoundary = true;
            }

            bool[] used = new bool[edges.Count];
            for (int seed = 0; seed < edges.Count; seed++)
            {
                if (used[seed])
                    continue;

                BoundaryEdge first = edges[seed];
                var loop = new RegionBoundaryLoop { face = first.face };
                int startNode = first.a;
                int currentNode = startNode;
                int currentEdge = seed;

                while (loop.worldPoints.Count <= edges.Count)
                {
                    if (used[currentEdge])
                        break;

                    used[currentEdge] = true;
                    BoundaryEdge edge = edges[currentEdge];
                    loop.worldPoints.Add(nodes[currentNode].Position);
                    loop.faces.UnionWith(edge.faces);
                    currentNode = edge.Other(currentNode);

                    if (currentNode == startNode)
                    {
                        loop.isClosed = true;
                        break;
                    }

                    List<int> neighbours = nodes[currentNode].edges;
                    if (neighbours.Count != 2)
                        break;
                    currentEdge = neighbours[0] == currentEdge ? neighbours[1] : neighbours[0];
                }

                if (loop.isClosed && loop.worldPoints.Count >= 3)
                    loops.Add(loop);
                else
                    hasOpenBoundary = true;
            }
        }

        private static void RepairDegreeOneGaps(
            List<BoundaryNode> nodes,
            ref List<BoundaryEdge> edges,
            float repairTolerance)
        {
            var endpoints = new List<int>();
            for (int i = 0; i < nodes.Count; i++)
                if (nodes[i].edges.Count == 1)
                    endpoints.Add(i);
            if (endpoints.Count < 2)
                return;

            int[] parent = new int[nodes.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            bool[] paired = new bool[nodes.Count];
            float repairSquared = repairTolerance * repairTolerance;
            bool changed = false;

            foreach (int endpoint in endpoints)
            {
                if (paired[endpoint])
                    continue;
                int incident = nodes[endpoint].edges[0];
                BoundaryEdge incidentEdge = edges[incident];
                int closest = -1;
                float closestDistance = repairSquared;

                foreach (int candidate in endpoints)
                {
                    if (candidate == endpoint || paired[candidate])
                        continue;
                    int candidateIncident = nodes[candidate].edges[0];
                    if (candidateIncident == incident ||
                        !incidentEdge.faces.Overlaps(edges[candidateIncident].faces))
                        continue;
                    float distance = (nodes[endpoint].Position - nodes[candidate].Position).sqrMagnitude;
                    if (distance > closestDistance)
                        continue;
                    closest = candidate;
                    closestDistance = distance;
                }

                if (closest < 0)
                    continue;
                paired[endpoint] = true;
                paired[closest] = true;
                parent[closest] = endpoint;
                changed = true;
            }

            if (!changed)
                return;

            foreach (BoundaryNode node in nodes)
                node.edges.Clear();
            var repaired = new List<BoundaryEdge>(edges.Count);
            foreach (BoundaryEdge edge in edges)
            {
                int a = Root(parent, edge.a);
                int b = Root(parent, edge.b);
                if (a == b)
                    continue;
                var copy = new BoundaryEdge(a, b, edge.face) { occurrences = 1 };
                copy.faces.UnionWith(edge.faces);
                int index = repaired.Count;
                repaired.Add(copy);
                nodes[a].edges.Add(index);
                nodes[b].edges.Add(index);
            }
            edges = repaired;
        }

        private static int Root(int[] parent, int value)
        {
            while (parent[value] != value)
            {
                parent[value] = parent[parent[value]];
                value = parent[value];
            }
            return value;
        }

        private static void SnapBoundaryVertices(
            Mesh mesh,
            Matrix4x4 localToWorld,
            Matrix4x4 worldToRegion,
            Vector3 half,
            bool[] activePlanes,
            IReadOnlyList<RegionBoundaryLoop> loops,
            float tolerance)
        {
            if (mesh == null || loops == null || loops.Count == 0)
                return;

            var cells = new Dictionary<GridKey, List<Vector3>>();
            foreach (RegionBoundaryLoop loop in loops)
            foreach (Vector3 point in loop.worldPoints)
            {
                GridKey cell = GridKey.From(point, tolerance);
                if (!cells.TryGetValue(cell, out List<Vector3> values))
                {
                    values = new List<Vector3>();
                    cells.Add(cell, values);
                }
                values.Add(point);
            }

            Matrix4x4 worldToLocal = localToWorld.inverse;
            Vector3[] vertices = mesh.vertices;
            float toleranceSquared = tolerance * tolerance;
            bool changed = false;
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 world = localToWorld.MultiplyPoint3x4(vertices[i]);
                Vector3 regionPoint = worldToRegion.MultiplyPoint3x4(world);
                if (CommonBoundaryFace(
                        regionPoint, regionPoint, half, tolerance, activePlanes) < 0)
                    continue;

                GridKey cell = GridKey.From(world, tolerance);
                Vector3 nearest = default;
                float nearestDistance = toleranceSquared;
                bool found = false;
                for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                for (int z = -1; z <= 1; z++)
                {
                    var neighbour = new GridKey(cell.x + x, cell.y + y, cell.z + z);
                    if (!cells.TryGetValue(neighbour, out List<Vector3> candidates))
                        continue;
                    foreach (Vector3 candidate in candidates)
                    {
                        float distance = (candidate - world).sqrMagnitude;
                        if (distance > nearestDistance)
                            continue;
                        nearest = candidate;
                        nearestDistance = distance;
                        found = true;
                    }
                }

                if (!found)
                    continue;
                vertices[i] = worldToLocal.MultiplyPoint3x4(nearest);
                changed = true;
            }

            if (!changed)
                return;
            mesh.vertices = vertices;
            mesh.RecalculateBounds();
        }

        private static int FindOrAddNode(
            Vector3 point,
            float tolerance,
            List<BoundaryNode> nodes,
            Dictionary<GridKey, List<int>> cells)
        {
            GridKey cell = GridKey.From(point, tolerance);
            float toleranceSquared = tolerance * tolerance;
            int nearest = -1;
            float nearestDistance = toleranceSquared;

            for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
            for (int z = -1; z <= 1; z++)
            {
                var neighbour = new GridKey(cell.x + x, cell.y + y, cell.z + z);
                if (!cells.TryGetValue(neighbour, out List<int> candidates))
                    continue;
                foreach (int candidate in candidates)
                {
                    float distance = (nodes[candidate].Position - point).sqrMagnitude;
                    if (distance > nearestDistance)
                        continue;
                    nearest = candidate;
                    nearestDistance = distance;
                }
            }

            if (nearest >= 0)
                return nearest;

            int index = nodes.Count;
            nodes.Add(new BoundaryNode(point));
            if (!cells.TryGetValue(cell, out List<int> values))
            {
                values = new List<int>();
                cells.Add(cell, values);
            }
            values.Add(index);
            return index;
        }

        private static void AddDistinct(List<VertexData> values, VertexData value, float tolerance)
        {
            if (values.Count == 0 ||
                (values[^1].regionPosition - value.regionPosition).sqrMagnitude > tolerance * tolerance)
                values.Add(value);
        }

        private static void RemoveClosingDuplicate(List<VertexData> values, float tolerance)
        {
            if (values.Count > 1 &&
                (values[0].regionPosition - values[^1].regionPosition).sqrMagnitude <= tolerance * tolerance)
                values.RemoveAt(values.Count - 1);
        }

        private static Vector2[][] ReadUvs(Mesh source)
        {
            var result = new Vector2[4][];
            for (int channel = 0; channel < 4; channel++)
            {
                var values = new List<Vector2>();
                source.GetUVs(channel, values);
                result[channel] = values.ToArray();
            }
            return result;
        }

        private readonly struct BoundarySegment
        {
            public readonly Vector3 a;
            public readonly Vector3 b;
            public readonly RegionBoundaryFace face;

            public BoundarySegment(Vector3 a, Vector3 b, RegionBoundaryFace face)
            {
                this.a = a;
                this.b = b;
                this.face = face;
            }
        }

        private sealed class BoundaryNode
        {
            public readonly List<int> edges = new();
            public readonly Vector3 Position;

            // Keep one exact cut point as the canonical seam coordinate. Averaging welded
            // endpoints moves the transition away from every preserved source edge.
            public BoundaryNode(Vector3 point) => Position = point;
        }

        private sealed class BoundaryEdge
        {
            public readonly int a;
            public readonly int b;
            public readonly RegionBoundaryFace face;
            public readonly HashSet<RegionBoundaryFace> faces = new();
            public int occurrences;

            public BoundaryEdge(int a, int b, RegionBoundaryFace face)
            {
                this.a = a;
                this.b = b;
                this.face = face;
            }

            public int Other(int node) => node == a ? b : a;
        }

        private readonly struct NodeEdgeKey : IEquatable<NodeEdgeKey>
        {
            public readonly int a;
            public readonly int b;

            public NodeEdgeKey(int first, int second)
            {
                if (first <= second) { a = first; b = second; }
                else { a = second; b = first; }
            }

            public bool Equals(NodeEdgeKey other) => a == other.a && b == other.b;
            public override bool Equals(object obj) => obj is NodeEdgeKey other && Equals(other);
            public override int GetHashCode() => unchecked((a * 397) ^ b);
        }

        private readonly struct GridKey : IEquatable<GridKey>
        {
            public readonly int x;
            public readonly int y;
            public readonly int z;

            public GridKey(int x, int y, int z)
            {
                this.x = x;
                this.y = y;
                this.z = z;
            }

            public static GridKey From(Vector3 point, float cellSize) => new(
                Mathf.FloorToInt(point.x / cellSize),
                Mathf.FloorToInt(point.y / cellSize),
                Mathf.FloorToInt(point.z / cellSize));

            public bool Equals(GridKey other) => x == other.x && y == other.y && z == other.z;
            public override bool Equals(object obj) => obj is GridKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = x;
                    hash = (hash * 397) ^ y;
                    return (hash * 397) ^ z;
                }
            }
        }

        private struct VertexData
        {
            public Vector3 position;
            public Vector3 regionPosition;
            public Vector3 normal;
            public Vector4 tangent;
            public Color color;
            public Vector2 uv0, uv1, uv2, uv3;

            public static VertexData Lerp(VertexData a, VertexData b, float t) => new()
            {
                position = Vector3.LerpUnclamped(a.position, b.position, t),
                regionPosition = Vector3.LerpUnclamped(a.regionPosition, b.regionPosition, t),
                normal = Vector3.LerpUnclamped(a.normal, b.normal, t).normalized,
                tangent = Vector4.LerpUnclamped(a.tangent, b.tangent, t),
                color = Color.LerpUnclamped(a.color, b.color, t),
                uv0 = Vector2.LerpUnclamped(a.uv0, b.uv0, t),
                uv1 = Vector2.LerpUnclamped(a.uv1, b.uv1, t),
                uv2 = Vector2.LerpUnclamped(a.uv2, b.uv2, t),
                uv3 = Vector2.LerpUnclamped(a.uv3, b.uv3, t)
            };
        }

        private sealed class MeshBuilder
        {
            private readonly List<Vector3> _vertices = new();
            private readonly List<Vector3> _normals = new();
            private readonly List<Vector4> _tangents = new();
            private readonly List<Color> _colors = new();
            private readonly List<Vector2>[] _uv = { new(), new(), new(), new() };
            private readonly List<int>[] _subMeshes;
            private readonly bool _hasNormals, _hasTangents, _hasColors;
            private readonly bool[] _hasUv;
            private readonly string _name;

            public int TriangleCount { get; private set; }

            public MeshBuilder(
                int subMeshCount,
                bool hasNormals,
                bool hasTangents,
                bool hasColors,
                bool[] hasUv,
                string name)
            {
                _subMeshes = new List<int>[Mathf.Max(1, subMeshCount)];
                for (int i = 0; i < _subMeshes.Length; i++)
                    _subMeshes[i] = new List<int>();
                _hasNormals = hasNormals;
                _hasTangents = hasTangents;
                _hasColors = hasColors;
                _hasUv = hasUv;
                _name = name;
            }

            public void AddPolygon(List<VertexData> polygon, int subMesh)
            {
                if (polygon.Count < 3)
                    return;
                for (int i = 1; i + 1 < polygon.Count; i++)
                {
                    Vector3 cross = Vector3.Cross(
                        polygon[i].position - polygon[0].position,
                        polygon[i + 1].position - polygon[0].position);
                    if (cross.sqrMagnitude <= MinTriangleCrossSquared)
                        continue;
                    AddTriangle(polygon[0], polygon[i], polygon[i + 1], subMesh);
                }
            }

            private void AddTriangle(VertexData a, VertexData b, VertexData c, int subMesh)
            {
                AddVertex(a, subMesh);
                AddVertex(b, subMesh);
                AddVertex(c, subMesh);
                TriangleCount++;
            }

            private void AddVertex(VertexData value, int subMesh)
            {
                int index = _vertices.Count;
                _vertices.Add(value.position);
                if (_hasNormals) _normals.Add(value.normal);
                if (_hasTangents) _tangents.Add(value.tangent);
                if (_hasColors) _colors.Add(value.color);
                if (_hasUv[0]) _uv[0].Add(value.uv0);
                if (_hasUv[1]) _uv[1].Add(value.uv1);
                if (_hasUv[2]) _uv[2].Add(value.uv2);
                if (_hasUv[3]) _uv[3].Add(value.uv3);
                _subMeshes[Mathf.Clamp(subMesh, 0, _subMeshes.Length - 1)].Add(index);
            }

            public Mesh Build()
            {
                var mesh = new Mesh
                {
                    name = _name,
                    indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
                };
                mesh.SetVertices(_vertices);
                if (_hasNormals) mesh.SetNormals(_normals);
                if (_hasTangents) mesh.SetTangents(_tangents);
                if (_hasColors) mesh.SetColors(_colors);
                for (int channel = 0; channel < 4; channel++)
                    if (_hasUv[channel]) mesh.SetUVs(channel, _uv[channel]);
                mesh.subMeshCount = _subMeshes.Length;
                for (int i = 0; i < _subMeshes.Length; i++)
                    mesh.SetTriangles(_subMeshes[i], i, false);
                mesh.RecalculateBounds();
                if (!_hasNormals) mesh.RecalculateNormals();
                return mesh;
            }
        }
    }
}
