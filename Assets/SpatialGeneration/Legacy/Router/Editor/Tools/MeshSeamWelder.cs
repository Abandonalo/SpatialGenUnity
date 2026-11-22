using System;
using System.Collections.Generic;
using UnityEngine;
using SpatialGeneration.Generation.Refinement;

/// <summary>Builds a watertight transition strip between two exact cut boundaries.</summary>
public static class MeshSeamWelder
{
    private const float MinAreaSquared = 1e-12f;
    private const float MinorOrphanPerimeterRatio = 0.10f;
    private const float MinorOrphanSpanRatio = 0.10f;

    public static bool TryBuildTransition(
        IReadOnlyList<RegionBoundaryLoop> sourceLoops,
        IReadOnlyList<RegionBoundaryLoop> replacementLoops,
        RegionSelection selection,
        Transform outputSpace,
        out Mesh transition,
        out string error)
    {
        transition = null;
        error = string.Empty;
        if (selection == null || outputSpace == null)
        {
            error = "The transition has no selection or output transform.";
            return false;
        }
        if (sourceLoops == null || sourceLoops.Count == 0)
        {
            error = "The selected source surface produced no closed boundary loop.";
            return false;
        }
        if (replacementLoops == null || replacementLoops.Count == 0)
        {
            error = "The replacement produced no boundary loop to weld to the source.";
            return false;
        }

        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        var usedReplacement = new bool[replacementLoops.Count];
        Matrix4x4 worldToLocal = outputSpace.worldToLocalMatrix;
        float largestSourcePerimeter = 0f;
        var sourceOrder = new List<int>(sourceLoops.Count);

        for (int i = 0; i < sourceLoops.Count; i++)
        {
            if (!IsUsable(sourceLoops[i]))
            {
                error = $"Source boundary loop {i} is open or degenerate.";
                return false;
            }
            largestSourcePerimeter = Mathf.Max(largestSourcePerimeter, Perimeter(sourceLoops[i].worldPoints));
            sourceOrder.Add(i);
        }
        for (int i = 0; i < replacementLoops.Count; i++)
        {
            if (IsUsable(replacementLoops[i]))
                continue;
            error = $"Replacement boundary loop {i} is open or degenerate.";
            return false;
        }

        // Match the principal contours first. Generated meshes occasionally contain a tiny
        // disconnected island at the cut plane; list order is topology-dependent, so pairing
        // in discovery order can spend the only replacement contour on that island and leave
        // the actual seam unmatched.
        sourceOrder.Sort((a, b) =>
            Perimeter(sourceLoops[b].worldPoints).CompareTo(Perimeter(sourceLoops[a].worldPoints)));

        int cappedMinorLoops = 0;

        foreach (int i in sourceOrder)
        {
            RegionBoundaryLoop source = sourceLoops[i];
            int match = FindClosestLoop(source, replacementLoops, usedReplacement, selection);
            if (match < 0)
            {
                if (!IsMinorPlanarOrphan(source, largestSourcePerimeter, selection))
                {
                    error = $"No compatible replacement boundary was found for substantial source loop {i}. " +
                            "The original was left unchanged to avoid a non-manifold seam.";
                    return false;
                }
                if (!AppendCap(source, selection, worldToLocal, vertices, triangles, out error))
                {
                    error = $"Minor source loop {i} could not be capped safely: {error}";
                    return false;
                }
                cappedMinorLoops++;
                continue;
            }

            usedReplacement[match] = true;
            if (!AppendZipper(source.worldPoints, replacementLoops[match].worldPoints,
                    worldToLocal, vertices, triangles, out error))
                return false;
        }

        for (int i = 0; i < usedReplacement.Length; i++)
        {
            if (usedReplacement[i])
                continue;
            error = $"Replacement boundary loop {i} has no source partner; accepting it would leave an open edge.";
            return false;
        }

        if (!ValidateStrip(vertices, triangles, out error))
            return false;

        if (cappedMinorLoops > 0)
        {
            Debug.LogWarning(
                $"Spatial Generation: capped {cappedMinorLoops} minor disconnected source " +
                "boundary loop(s) instead of attaching multiple source contours to one " +
                "replacement contour. This keeps the result manifold and preserves the " +
                "small exterior islands.");
        }

        transition = new Mesh
        {
            name = "Refinement_WeldedTransition",
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
        };
        transition.SetVertices(vertices);
        transition.SetTriangles(triangles, 0, true);
        transition.RecalculateNormals();
        transition.RecalculateTangents();
        transition.colors = White(vertices.Count);
        transition.RecalculateBounds();
        return true;
    }

    private static bool IsMinorPlanarOrphan(
        RegionBoundaryLoop loop,
        float largestSourcePerimeter,
        RegionSelection selection)
    {
        if (!TryGetSingleFace(loop, out RegionBoundaryFace face) ||
            largestSourcePerimeter <= 1e-8f)
            return false;

        float perimeter = Perimeter(loop.worldPoints);
        if (perimeter > largestSourcePerimeter * MinorOrphanPerimeterRatio)
            return false;

        Matrix4x4 toRegion = selection.WorldToLocal();
        Vector3 min = Vector3.positiveInfinity;
        Vector3 max = Vector3.negativeInfinity;
        foreach (Vector3 world in loop.worldPoints)
        {
            Vector3 local = toRegion.MultiplyPoint3x4(world);
            min = Vector3.Min(min, local);
            max = Vector3.Max(max, local);
        }

        Vector3 span = max - min;
        Vector3 selectionSize = RegionSelection.ClampSize(selection.size);
        return face switch
        {
            RegionBoundaryFace.PositiveX or RegionBoundaryFace.NegativeX =>
                span.y <= selectionSize.y * MinorOrphanSpanRatio &&
                span.z <= selectionSize.z * MinorOrphanSpanRatio,
            RegionBoundaryFace.PositiveY or RegionBoundaryFace.NegativeY =>
                span.x <= selectionSize.x * MinorOrphanSpanRatio &&
                span.z <= selectionSize.z * MinorOrphanSpanRatio,
            _ =>
                span.x <= selectionSize.x * MinorOrphanSpanRatio &&
                span.y <= selectionSize.y * MinorOrphanSpanRatio
        };
    }

    private static bool AppendCap(
        RegionBoundaryLoop loop,
        RegionSelection selection,
        Matrix4x4 worldToLocal,
        List<Vector3> vertices,
        List<int> triangles,
        out string error)
    {
        error = string.Empty;
        if (!TryGetSingleFace(loop, out RegionBoundaryFace face))
        {
            error = "the contour crosses more than one OBB face";
            return false;
        }

        var world = new List<Vector3>(loop.worldPoints);
        Vector3 inward = selection.rotation * InwardNormal(face);
        if (Vector3.Dot(NewellNormal(world), inward) < 0f)
            world.Reverse();

        Matrix4x4 toRegion = selection.WorldToLocal();
        var projected = new List<Vector2>(world.Count);
        foreach (Vector3 point in world)
            projected.Add(ProjectToFace(toRegion.MultiplyPoint3x4(point), face));

        if (!TryTriangulate(projected, out List<int> capTriangles, out error))
            return false;

        int offset = vertices.Count;
        foreach (Vector3 point in world)
            vertices.Add(worldToLocal.MultiplyPoint3x4(point));
        foreach (int index in capTriangles)
            triangles.Add(offset + index);
        return true;
    }

    private static bool TryTriangulate(
        IReadOnlyList<Vector2> polygon,
        out List<int> triangles,
        out string error)
    {
        triangles = new List<int>((polygon.Count - 2) * 3);
        error = string.Empty;
        if (polygon.Count < 3)
        {
            error = "the contour has fewer than three vertices";
            return false;
        }

        float signedArea = SignedArea(polygon);
        if (Mathf.Abs(signedArea) <= 1e-10f)
        {
            error = "the contour has no planar area";
            return false;
        }

        float orientation = Mathf.Sign(signedArea);
        var remaining = new List<int>(polygon.Count);
        for (int i = 0; i < polygon.Count; i++)
            remaining.Add(i);

        int guard = polygon.Count * polygon.Count;
        while (remaining.Count > 3 && guard-- > 0)
        {
            bool clipped = false;
            for (int cursor = 0; cursor < remaining.Count; cursor++)
            {
                int previous = remaining[(cursor - 1 + remaining.Count) % remaining.Count];
                int current = remaining[cursor];
                int next = remaining[(cursor + 1) % remaining.Count];
                float cross = Cross(polygon[previous], polygon[current], polygon[next]);
                if (cross * orientation <= 1e-10f)
                    continue;

                bool containsPoint = false;
                for (int p = 0; p < remaining.Count; p++)
                {
                    int candidate = remaining[p];
                    if (candidate == previous || candidate == current || candidate == next)
                        continue;
                    if (!PointInTriangle(
                            polygon[candidate], polygon[previous], polygon[current], polygon[next], orientation))
                        continue;
                    containsPoint = true;
                    break;
                }
                if (containsPoint)
                    continue;

                triangles.Add(previous);
                triangles.Add(current);
                triangles.Add(next);
                remaining.RemoveAt(cursor);
                clipped = true;
                break;
            }

            if (!clipped)
            {
                error = "the contour is self-intersecting or numerically degenerate";
                return false;
            }
        }

        if (remaining.Count != 3)
        {
            error = "the contour triangulation did not terminate";
            return false;
        }
        triangles.Add(remaining[0]);
        triangles.Add(remaining[1]);
        triangles.Add(remaining[2]);

        for (int i = 0; i < triangles.Count; i += 3)
        {
            if (Mathf.Abs(Cross(
                    polygon[triangles[i]], polygon[triangles[i + 1]], polygon[triangles[i + 2]])) > 1e-10f)
                continue;
            error = "the contour triangulation produced a zero-area triangle";
            return false;
        }
        return true;
    }

    private static bool PointInTriangle(
        Vector2 point,
        Vector2 a,
        Vector2 b,
        Vector2 c,
        float orientation)
    {
        const float tolerance = -1e-9f;
        return Cross(a, b, point) * orientation >= tolerance &&
               Cross(b, c, point) * orientation >= tolerance &&
               Cross(c, a, point) * orientation >= tolerance;
    }

    private static float SignedArea(IReadOnlyList<Vector2> polygon)
    {
        float area = 0f;
        for (int i = 0; i < polygon.Count; i++)
        {
            Vector2 a = polygon[i];
            Vector2 b = polygon[(i + 1) % polygon.Count];
            area += a.x * b.y - b.x * a.y;
        }
        return area * 0.5f;
    }

    private static float Cross(Vector2 a, Vector2 b, Vector2 c) =>
        (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);

    private static Vector2 ProjectToFace(Vector3 point, RegionBoundaryFace face) => face switch
    {
        RegionBoundaryFace.PositiveX or RegionBoundaryFace.NegativeX => new Vector2(point.y, point.z),
        RegionBoundaryFace.PositiveY or RegionBoundaryFace.NegativeY => new Vector2(point.x, point.z),
        _ => new Vector2(point.x, point.y)
    };

    private static Vector3 InwardNormal(RegionBoundaryFace face) => face switch
    {
        RegionBoundaryFace.PositiveX => Vector3.left,
        RegionBoundaryFace.NegativeX => Vector3.right,
        RegionBoundaryFace.PositiveY => Vector3.down,
        RegionBoundaryFace.NegativeY => Vector3.up,
        RegionBoundaryFace.PositiveZ => Vector3.back,
        _ => Vector3.forward
    };

    private static Vector3 NewellNormal(IReadOnlyList<Vector3> points)
    {
        Vector3 normal = Vector3.zero;
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 current = points[i];
            Vector3 next = points[(i + 1) % points.Count];
            normal.x += (current.y - next.y) * (current.z + next.z);
            normal.y += (current.z - next.z) * (current.x + next.x);
            normal.z += (current.x - next.x) * (current.y + next.y);
        }
        return normal;
    }

    private static bool TryGetSingleFace(RegionBoundaryLoop loop, out RegionBoundaryFace face)
    {
        face = loop.face;
        if (loop.faces.Count != 1)
            return false;
        foreach (RegionBoundaryFace value in loop.faces)
            face = value;
        return true;
    }

    private static float Perimeter(IReadOnlyList<Vector3> points)
    {
        float result = 0f;
        for (int i = 0; i < points.Count; i++)
            result += Vector3.Distance(points[i], points[(i + 1) % points.Count]);
        return result;
    }

    private static bool AppendZipper(
        IReadOnlyList<Vector3> outerWorld,
        IReadOnlyList<Vector3> innerWorld,
        Matrix4x4 worldToLocal,
        List<Vector3> vertices,
        List<int> triangles,
        out string error)
    {
        error = string.Empty;
        List<Vector3> inner = Align(innerWorld, outerWorld);
        int outerCount = outerWorld.Count;
        int innerCount = inner.Count;
        int offset = vertices.Count;
        int triangleStart = triangles.Count;
        float[] outerProgress = ArcProgress(outerWorld);
        float[] innerProgress = ArcProgress(inner);

        for (int i = 0; i < outerCount; i++)
            vertices.Add(worldToLocal.MultiplyPoint3x4(outerWorld[i]));
        for (int i = 0; i < innerCount; i++)
            vertices.Add(worldToLocal.MultiplyPoint3x4(inner[i]));

        int outerIndex = 0;
        int innerIndex = 0;
        while (outerIndex < outerCount || innerIndex < innerCount)
        {
            float nextOuter = outerIndex < outerCount
                ? outerProgress[outerIndex + 1]
                : float.PositiveInfinity;
            float nextInner = innerIndex < innerCount
                ? innerProgress[innerIndex + 1]
                : float.PositiveInfinity;

            int o0 = offset + (outerIndex % outerCount);
            int r0 = offset + outerCount + (innerIndex % innerCount);
            if (Mathf.Abs(nextOuter - nextInner) <= 1e-6f)
            {
                int o1 = offset + ((outerIndex + 1) % outerCount);
                int r1 = offset + outerCount + ((innerIndex + 1) % innerCount);
                AddTriangle(triangles, vertices, o0, r0, o1);
                AddTriangle(triangles, vertices, o1, r0, r1);
                outerIndex++;
                innerIndex++;
            }
            else if (nextOuter < nextInner)
            {
                int o1 = offset + ((outerIndex + 1) % outerCount);
                AddTriangle(triangles, vertices, o0, r0, o1);
                outerIndex++;
            }
            else
            {
                int r1 = offset + outerCount + ((innerIndex + 1) % innerCount);
                AddTriangle(triangles, vertices, o0, r0, r1);
                innerIndex++;
            }
        }

        int expected = outerCount + innerCount;
        if ((triangles.Count - triangleStart) / 3 != expected)
        {
            error = "Boundary pairing produced zero-area seam triangles.";
            return false;
        }
        return true;
    }

    private static float[] ArcProgress(IReadOnlyList<Vector3> loop)
    {
        var result = new float[loop.Count + 1];
        for (int i = 0; i < loop.Count; i++)
            result[i + 1] = result[i] + Vector3.Distance(loop[i], loop[(i + 1) % loop.Count]);
        float perimeter = result[^1];
        if (perimeter <= 1e-8f)
            return result;
        for (int i = 1; i < result.Length; i++)
            result[i] /= perimeter;
        return result;
    }

    private static void AddTriangle(
        List<int> triangles,
        IReadOnlyList<Vector3> vertices,
        int a,
        int b,
        int c)
    {
        if (a == b || b == c || c == a)
            return;
        Vector3 cross = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
        if (cross.sqrMagnitude <= MinAreaSquared)
            return;
        triangles.Add(a);
        triangles.Add(b);
        triangles.Add(c);
    }

    private static List<Vector3> Align(
        IReadOnlyList<Vector3> candidate,
        IReadOnlyList<Vector3> reference)
    {
        int start = 0;
        float closest = float.PositiveInfinity;
        for (int i = 0; i < candidate.Count; i++)
        {
            float distance = (candidate[i] - reference[0]).sqrMagnitude;
            if (distance < closest)
            {
                closest = distance;
                start = i;
            }
        }

        List<Vector3> forward = Reorder(candidate, start, 1);
        List<Vector3> reverse = Reorder(candidate, start, -1);
        return AlignmentCost(reference, reverse) < AlignmentCost(reference, forward)
            ? reverse
            : forward;
    }

    private static List<Vector3> Reorder(IReadOnlyList<Vector3> values, int start, int direction)
    {
        var result = new List<Vector3>(values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            int index = (start + direction * i) % values.Count;
            if (index < 0) index += values.Count;
            result.Add(values[index]);
        }
        return result;
    }

    private static float AlignmentCost(IReadOnlyList<Vector3> outer, IReadOnlyList<Vector3> inner)
    {
        int samples = Mathf.Max(outer.Count, inner.Count);
        float cost = 0f;
        for (int i = 0; i < samples; i++)
        {
            Vector3 a = outer[Mathf.Min(outer.Count - 1, i * outer.Count / samples)];
            Vector3 b = inner[Mathf.Min(inner.Count - 1, i * inner.Count / samples)];
            cost += (a - b).sqrMagnitude;
        }
        return cost;
    }

    private static int FindClosestLoop(
        RegionBoundaryLoop source,
        IReadOnlyList<RegionBoundaryLoop> candidates,
        IReadOnlyList<bool> used,
        RegionSelection selection)
    {
        Vector3 sourceCentre = Centre(source.worldPoints);
        RegionBoundaryFace sourceFace = ClosestFace(sourceCentre, selection);
        int best = -1;
        float distance = float.PositiveInfinity;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (used[i] || !IsUsable(candidates[i]) ||
                ClosestFace(Centre(candidates[i].worldPoints), selection) != sourceFace)
                continue;
            float candidateDistance = (Centre(candidates[i].worldPoints) - sourceCentre).sqrMagnitude;
            if (candidateDistance < distance)
            {
                distance = candidateDistance;
                best = i;
            }
        }
        return best;
    }

    private static RegionBoundaryFace ClosestFace(Vector3 world, RegionSelection selection)
    {
        Vector3 local = selection.WorldToLocal().MultiplyPoint3x4(world);
        Vector3 half = RegionSelection.ClampSize(selection.size) * 0.5f;
        float[] distances =
        {
            Mathf.Abs(local.x - half.x), Mathf.Abs(local.x + half.x),
            Mathf.Abs(local.y - half.y), Mathf.Abs(local.y + half.y),
            Mathf.Abs(local.z - half.z), Mathf.Abs(local.z + half.z)
        };
        int best = 0;
        for (int i = 1; i < distances.Length; i++)
            if (distances[i] < distances[best]) best = i;
        return (RegionBoundaryFace)best;
    }

    private static bool ValidateStrip(
        IReadOnlyList<Vector3> vertices,
        IReadOnlyList<int> triangles,
        out string error)
    {
        error = string.Empty;
        if (triangles.Count == 0 || triangles.Count % 3 != 0)
        {
            error = "The transition contains no complete triangles.";
            return false;
        }
        foreach (Vector3 vertex in vertices)
        {
            if (!Finite(vertex))
            {
                error = "The transition contains a non-finite coordinate.";
                return false;
            }
        }
        for (int i = 0; i < triangles.Count; i += 3)
        {
            Vector3 cross = Vector3.Cross(
                vertices[triangles[i + 1]] - vertices[triangles[i]],
                vertices[triangles[i + 2]] - vertices[triangles[i]]);
            if (cross.sqrMagnitude <= MinAreaSquared)
            {
                error = "The transition contains a zero-area triangle.";
                return false;
            }
        }
        return true;
    }

    private static bool IsUsable(RegionBoundaryLoop loop) =>
        loop != null && loop.isClosed && loop.worldPoints.Count >= 3;

    private static Vector3 Centre(IReadOnlyList<Vector3> values)
    {
        Vector3 sum = Vector3.zero;
        for (int i = 0; i < values.Count; i++) sum += values[i];
        return sum / values.Count;
    }

    private static bool Finite(Vector3 value) =>
        !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
        !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
        !float.IsNaN(value.z) && !float.IsInfinity(value.z);

    private static Color[] White(int count)
    {
        var colors = new Color[count];
        for (int i = 0; i < count; i++) colors[i] = Color.white;
        return colors;
    }
}
