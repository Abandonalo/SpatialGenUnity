using NUnit.Framework;
using UnityEngine;
using SpatialGeneration.Generation.Refinement;

/// <summary>
/// Guards the property the whole local-refinement story rests on: geometry outside the
/// selection box comes through the split untouched.
/// </summary>
public class MeshRegionSplitterTests
{
    /// <summary>Two disjoint unit quads: one at x = -2, one at x = +2.</summary>
    private static Mesh BuildTwoQuads()
    {
        var mesh = new Mesh { name = "TwoQuads" };
        mesh.SetVertices(new[]
        {
            new Vector3(-2.5f, -0.5f, 0f), new Vector3(-1.5f, -0.5f, 0f),
            new Vector3(-1.5f, 0.5f, 0f), new Vector3(-2.5f, 0.5f, 0f),
            new Vector3(1.5f, -0.5f, 0f), new Vector3(2.5f, -0.5f, 0f),
            new Vector3(2.5f, 0.5f, 0f), new Vector3(1.5f, 0.5f, 0f)
        });
        mesh.SetUVs(0, new[]
        {
            Vector2.zero, Vector2.right, Vector2.one, Vector2.up,
            Vector2.zero, Vector2.right, Vector2.one, Vector2.up
        });
        mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 }, 0);
        mesh.RecalculateNormals();
        return mesh;
    }

    private static RegionSelection BoxAt(Vector3 center, Vector3 size) => new()
    {
        selectionId = "test",
        center = center,
        size = size,
        rotation = Quaternion.identity
    };

    [Test]
    public void RemovesOnlyTrianglesInsideTheBox()
    {
        Mesh source = BuildTwoQuads();
        // Covers the +x quad only.
        RegionSelection region = BoxAt(new Vector3(2f, 0f, 0f), new Vector3(2f, 2f, 2f));

        Mesh outside = MeshRegionSplitter.BuildOutsideMesh(source, Matrix4x4.identity, region, out int removed);

        Assert.AreEqual(2, removed, "both triangles of the +x quad should be removed");
        Assert.IsNotNull(outside);
        Assert.AreEqual(6, outside.GetIndexCount(0), "the -x quad's two triangles should survive");

        // Surviving vertices must be identical, not re-derived.
        foreach (Vector3 vertex in outside.vertices)
            Assert.Less(vertex.x, 0f, "no vertex from the removed quad should remain");

        Object.DestroyImmediate(source);
        Object.DestroyImmediate(outside);
    }

    [Test]
    public void PreservesVertexAttributesOfSurvivingTriangles()
    {
        Mesh source = BuildTwoQuads();
        RegionSelection region = BoxAt(new Vector3(2f, 0f, 0f), new Vector3(2f, 2f, 2f));

        Mesh outside = MeshRegionSplitter.BuildOutsideMesh(source, Matrix4x4.identity, region, out _);

        Assert.AreEqual(outside.vertexCount, outside.uv.Length, "UVs must be carried over");
        Assert.AreEqual(outside.vertexCount, outside.normals.Length, "normals must be carried over");

        Object.DestroyImmediate(source);
        Object.DestroyImmediate(outside);
    }

    [Test]
    public void ReturnsNullWhenNothingIntersects()
    {
        Mesh source = BuildTwoQuads();
        RegionSelection region = BoxAt(new Vector3(0f, 20f, 0f), Vector3.one);

        Mesh outside = MeshRegionSplitter.BuildOutsideMesh(source, Matrix4x4.identity, region, out int removed);

        Assert.AreEqual(0, removed);
        Assert.IsNull(outside, "callers keep the original mesh when the region misses it entirely");

        Object.DestroyImmediate(source);
    }

    [Test]
    public void ReturnsNullWhenEverythingIsInside()
    {
        Mesh source = BuildTwoQuads();
        RegionSelection region = BoxAt(Vector3.zero, new Vector3(20f, 20f, 20f));

        Mesh outside = MeshRegionSplitter.BuildOutsideMesh(source, Matrix4x4.identity, region, out int removed);

        Assert.AreEqual(4, removed);
        Assert.IsNull(outside, "nothing survives, so the caller should drop the object");

        Object.DestroyImmediate(source);
    }

    [Test]
    public void RespectsTheObjectTransform()
    {
        Mesh source = BuildTwoQuads();
        // The box sits where the -x quad lands after a 180-degree turn about Y.
        RegionSelection region = BoxAt(new Vector3(2f, 0f, 0f), new Vector3(2f, 2f, 2f));
        Matrix4x4 rotated = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(0f, 180f, 0f), Vector3.one);

        Mesh outside = MeshRegionSplitter.BuildOutsideMesh(source, rotated, region, out int removed);

        Assert.AreEqual(2, removed);
        Assert.IsNotNull(outside);
        foreach (Vector3 vertex in outside.vertices)
            Assert.Greater(vertex.x, 0f, "the rotated -x quad is the one now inside the box");

        Object.DestroyImmediate(source);
        Object.DestroyImmediate(outside);
    }

    [Test]
    public void RespectsBoxRotation()
    {
        Mesh source = BuildTwoQuads();
        // A thin box rotated 45 degrees about Z still contains the +x quad's centroids.
        var region = new RegionSelection
        {
            selectionId = "rotated",
            center = new Vector3(2f, 0f, 0f),
            size = new Vector3(2f, 2f, 2f),
            rotation = Quaternion.Euler(0f, 0f, 45f)
        };

        Mesh outside = MeshRegionSplitter.BuildOutsideMesh(source, Matrix4x4.identity, region, out int removed);

        Assert.AreEqual(2, removed);
        Assert.IsNotNull(outside);

        Object.DestroyImmediate(source);
        Object.DestroyImmediate(outside);
    }
}
