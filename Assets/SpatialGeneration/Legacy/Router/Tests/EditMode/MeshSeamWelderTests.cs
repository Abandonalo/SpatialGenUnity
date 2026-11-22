using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using SpatialGeneration.Generation.Refinement;

public class MeshSeamWelderTests
{
    private GameObject space;

    [SetUp]
    public void SetUp() => space = new GameObject("SeamSpace");

    [TearDown]
    public void TearDown() => Object.DestroyImmediate(space);

    [Test]
    public void BuildsTransitionForUnequalClosedLoopsWithoutDegenerateTriangles()
    {
        RegionBoundaryLoop outer = Loop(1f, 4);
        RegionBoundaryLoop inner = Loop(0.75f, 8);
        inner.worldPoints.Reverse(); // pairing must repair opposite winding
        var selection = new RegionSelection
        {
            center = Vector3.zero,
            size = new Vector3(2f, 2f, 1f),
            rotation = Quaternion.identity
        };

        bool success = MeshSeamWelder.TryBuildTransition(
            new[] { outer }, new[] { inner }, selection, space.transform,
            out Mesh mesh, out string error);

        Assert.IsTrue(success, error);
        Assert.IsNotNull(mesh);
        Assert.AreEqual(outer.worldPoints.Count + inner.worldPoints.Count, mesh.triangles.Length / 3);
        for (int i = 0; i < mesh.triangles.Length; i += 3)
        {
            Vector3 a = mesh.vertices[mesh.triangles[i]];
            Vector3 b = mesh.vertices[mesh.triangles[i + 1]];
            Vector3 c = mesh.vertices[mesh.triangles[i + 2]];
            Assert.Greater(Vector3.Cross(b - a, c - a).sqrMagnitude, 1e-12f);
        }
        Object.DestroyImmediate(mesh);
    }

    [Test]
    public void RejectsAnOpenBoundaryWithoutCreatingAMesh()
    {
        RegionBoundaryLoop open = Loop(1f, 4);
        open.isClosed = false;
        bool success = MeshSeamWelder.TryBuildTransition(
            new[] { open }, new[] { Loop(0.8f, 4) },
            new RegionSelection { size = Vector3.one }, space.transform,
            out Mesh mesh, out string error);

        Assert.IsFalse(success);
        Assert.IsNull(mesh);
        StringAssert.Contains("open", error.ToLowerInvariant());
    }

    [Test]
    public void CapsAMinorUnmatchedSourceIslandWithoutBranchingTheSeam()
    {
        RegionBoundaryLoop main = Loop(0.9f, 8);
        RegionBoundaryLoop island = Loop(0.04f, 8, new Vector2(0.6f, 0.6f));
        RegionBoundaryLoop replacement = Loop(0.75f, 8);
        var selection = new RegionSelection
        {
            center = Vector3.zero,
            size = new Vector3(2f, 2f, 1f),
            rotation = Quaternion.identity
        };

        bool success = MeshSeamWelder.TryBuildTransition(
            new[] { main, island }, new[] { replacement }, selection, space.transform,
            out Mesh mesh, out string error);

        Assert.IsTrue(success, error);
        Assert.IsNotNull(mesh);
        Assert.AreEqual(
            main.worldPoints.Count + replacement.worldPoints.Count + island.worldPoints.Count - 2,
            mesh.triangles.Length / 3);
        Assert.AreEqual(
            main.worldPoints.Count + replacement.worldPoints.Count + island.worldPoints.Count,
            CountBoundaryEdges(mesh));
        Object.DestroyImmediate(mesh);
    }

    [Test]
    public void RejectsASecondSubstantialSourceLoopWhenReplacementHasOnlyOne()
    {
        RegionBoundaryLoop first = Loop(0.3f, 8, new Vector2(-0.5f, 0f));
        RegionBoundaryLoop second = Loop(0.3f, 8, new Vector2(0.5f, 0f));
        RegionBoundaryLoop replacement = Loop(0.25f, 8, new Vector2(-0.5f, 0f));
        var selection = new RegionSelection
        {
            center = Vector3.zero,
            size = new Vector3(2f, 2f, 1f),
            rotation = Quaternion.identity
        };

        bool success = MeshSeamWelder.TryBuildTransition(
            new[] { first, second }, new[] { replacement }, selection, space.transform,
            out Mesh mesh, out string error);

        Assert.IsFalse(success);
        Assert.IsNull(mesh);
        StringAssert.Contains("substantial", error.ToLowerInvariant());
    }

    private static RegionBoundaryLoop Loop(float radius, int count, Vector2? offset = null)
    {
        Vector2 centre = offset ?? Vector2.zero;
        var loop = new RegionBoundaryLoop
        {
            face = RegionBoundaryFace.PositiveZ,
            isClosed = true
        };
        loop.faces.Add(RegionBoundaryFace.PositiveZ);
        for (int i = 0; i < count; i++)
        {
            float angle = i * Mathf.PI * 2f / count;
            loop.worldPoints.Add(new Vector3(
                centre.x + Mathf.Cos(angle) * radius,
                centre.y + Mathf.Sin(angle) * radius,
                0.5f));
        }
        return loop;
    }

    private static int CountBoundaryEdges(Mesh mesh)
    {
        var incidence = new Dictionary<(int, int), int>();
        int[] triangles = mesh.triangles;
        for (int i = 0; i < triangles.Length; i += 3)
        {
            Count(triangles[i], triangles[i + 1]);
            Count(triangles[i + 1], triangles[i + 2]);
            Count(triangles[i + 2], triangles[i]);
        }

        int result = 0;
        foreach (int count in incidence.Values)
            if (count == 1) result++;
        return result;

        void Count(int a, int b)
        {
            var edge = a <= b ? (a, b) : (b, a);
            incidence[edge] = incidence.TryGetValue(edge, out int value) ? value + 1 : 1;
        }
    }
}
