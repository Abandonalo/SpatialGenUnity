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

    [Test]
    public void SplitsCrossingTrianglesAndInterpolatesAttributes()
    {
        var source = new Mesh { name = "Crossing" };
        source.vertices = new[]
        {
            new Vector3(-2f, -2f, 0f), new Vector3(2f, -2f, 0f), new Vector3(0f, 2f, 0f)
        };
        source.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
        source.colors = new[] { Color.red, Color.green, Color.blue };
        source.triangles = new[] { 0, 1, 2 };
        source.RecalculateNormals();

        MeshCutResult cut = MeshRegionSplitter.Cut(
            source, Matrix4x4.identity, BoxAt(Vector3.zero, new Vector3(2f, 2f, 2f)));

        Assert.AreEqual(1, cut.removedTriangles);
        Assert.IsNotNull(cut.outsideMesh);
        Assert.IsNotNull(cut.insideMesh);
        Assert.AreEqual(cut.insideMesh.vertexCount, cut.insideMesh.uv.Length);
        Assert.AreEqual(cut.insideMesh.vertexCount, cut.insideMesh.colors.Length);
        foreach (Vector3 vertex in cut.insideMesh.vertices)
        {
            Assert.LessOrEqual(Mathf.Abs(vertex.x), 1.0001f);
            Assert.LessOrEqual(Mathf.Abs(vertex.y), 1.0001f);
        }

        Object.DestroyImmediate(source);
        Object.DestroyImmediate(cut.outsideMesh);
        Object.DestroyImmediate(cut.insideMesh);
    }

    [Test]
    public void PreservesSmallTripoTrianglesAfterWorldScale()
    {
        var source = new Mesh { name = "DenseTripoPatch" };
        source.vertices = new[]
        {
            new Vector3(-0.001f, -0.001f, 0f),
            new Vector3(0.001f, -0.001f, 0f),
            new Vector3(0f, 0.001f, 0f)
        };
        source.triangles = new[] { 0, 1, 2 };
        source.RecalculateNormals();

        MeshCutResult cut = MeshRegionSplitter.Cut(
            source,
            Matrix4x4.Scale(Vector3.one * 100f),
            BoxAt(Vector3.zero, new Vector3(0.1f, 0.1f, 0.1f)));

        Assert.IsNotNull(cut.outsideMesh,
            "small local-space triangles are valid after the imported mesh is scaled in Unity");
        Assert.Greater(cut.outsideMesh.triangles.Length, 0);

        Object.DestroyImmediate(source);
        Object.DestroyImmediate(cut.outsideMesh);
        Object.DestroyImmediate(cut.insideMesh);
    }

    [Test]
    public void ReturnsAClosedBoundaryLoopForAClippedSurface()
    {
        var source = new Mesh { name = "LargeQuad" };
        source.vertices = new[]
        {
            new Vector3(-2f, -2f, 0f), new Vector3(2f, -2f, 0f),
            new Vector3(2f, 2f, 0f), new Vector3(-2f, 2f, 0f)
        };
        source.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        source.RecalculateNormals();

        MeshCutResult cut = MeshRegionSplitter.Cut(
            source, Matrix4x4.identity, BoxAt(Vector3.zero, new Vector3(2f, 2f, 2f)));

        Assert.IsFalse(cut.hasOpenBoundary);
        Assert.AreEqual(1, cut.boundaryLoops.Count);
        Assert.IsTrue(cut.boundaryLoops[0].isClosed);
        Assert.GreaterOrEqual(cut.boundaryLoops[0].worldPoints.Count, 4);

        Object.DestroyImmediate(source);
        Object.DestroyImmediate(cut.outsideMesh);
        Object.DestroyImmediate(cut.insideMesh);
    }

    [Test]
    public void CancelsInteriorEdgesOnAnObbFace()
    {
        var source = new Mesh { name = "CoplanarLargeQuad" };
        source.vertices = new[]
        {
            new Vector3(-2f, -2f, 0f), new Vector3(2f, -2f, 0f),
            new Vector3(2f, 2f, 0f), new Vector3(-2f, 2f, 0f)
        };
        source.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        source.RecalculateNormals();

        // The quad lies exactly on the box's +Z face. Its triangulation diagonal is
        // reported by both clipped polygons and must not become a branch in the cut loop.
        MeshCutResult cut = MeshRegionSplitter.Cut(
            source, Matrix4x4.identity,
            BoxAt(new Vector3(0f, 0f, -1f), new Vector3(2f, 2f, 2f)));

        Assert.IsFalse(cut.hasOpenBoundary);
        Assert.AreEqual(1, cut.boundaryLoops.Count);
        Assert.IsTrue(cut.boundaryLoops[0].isClosed);

        Object.DestroyImmediate(source);
        Object.DestroyImmediate(cut.outsideMesh);
        Object.DestroyImmediate(cut.insideMesh);
    }

    [Test]
    public void WeldsNumericallySplitCutVertices()
    {
        const float epsilon = 0.00125f;
        var source = new Mesh { name = "NumericallySplitQuad" };
        source.vertices = new[]
        {
            new Vector3(-3f, -2f, 0f), new Vector3(3f, -2f, 0f),
            new Vector3(3f, 2f, 0f),
            new Vector3(-3f, -2f + epsilon, 0f),
            new Vector3(3f, 2f + epsilon, 0f), new Vector3(-3f, 2f, 0f)
        };
        source.triangles = new[] { 0, 1, 2, 3, 4, 5 };
        source.RecalculateNormals();

        MeshCutResult cut = MeshRegionSplitter.Cut(
            source, Matrix4x4.identity, BoxAt(Vector3.zero, new Vector3(2f, 2f, 2f)));

        Assert.IsFalse(cut.hasOpenBoundary,
            "independently clipped neighbours should be welded within numerical tolerance");
        Assert.AreEqual(1, cut.boundaryLoops.Count);
        Assert.IsTrue(cut.boundaryLoops[0].isClosed);

        Object.DestroyImmediate(source);
        Object.DestroyImmediate(cut.outsideMesh);
        Object.DestroyImmediate(cut.insideMesh);
    }

    [Test]
    public void KeepsMultipleDisconnectedBoundaryLoopsSeparate()
    {
        var source = new Mesh { name = "ParallelQuads" };
        source.vertices = new[]
        {
            new Vector3(-2f,-2f,-0.25f), new Vector3(2f,-2f,-0.25f),
            new Vector3(2f,2f,-0.25f), new Vector3(-2f,2f,-0.25f),
            new Vector3(-2f,-2f,0.25f), new Vector3(2f,-2f,0.25f),
            new Vector3(2f,2f,0.25f), new Vector3(-2f,2f,0.25f),
        };
        source.triangles = new[] { 0,1,2, 0,2,3, 4,5,6, 4,6,7 };
        source.RecalculateNormals();

        MeshCutResult cut = MeshRegionSplitter.Cut(
            source, Matrix4x4.identity, BoxAt(Vector3.zero, new Vector3(2f, 2f, 2f)));

        Assert.IsFalse(cut.hasOpenBoundary);
        Assert.AreEqual(2, cut.boundaryLoops.Count);
        Assert.IsTrue(cut.boundaryLoops.TrueForAll(loop => loop.isClosed));

        Object.DestroyImmediate(source);
        Object.DestroyImmediate(cut.outsideMesh);
        Object.DestroyImmediate(cut.insideMesh);
    }

    [Test]
    public void ReplacementCanClipOnlyFacesThatCarrySourceSeams()
    {
        GameObject primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Mesh source = Object.Instantiate(primitive.GetComponent<MeshFilter>().sharedMesh);
        Object.DestroyImmediate(primitive);
        RegionSelection region = BoxAt(Vector3.zero, Vector3.one * 0.8f);

        MeshCutResult cut = MeshRegionSplitter.Cut(
            source,
            Matrix4x4.identity,
            region,
            new[] { RegionBoundaryFace.PositiveX });

        Assert.IsNotNull(cut.insideMesh);
        Assert.AreEqual(1, cut.boundaryLoops.Count);
        Assert.IsTrue(cut.boundaryLoops[0].faces.SetEquals(new[] { RegionBoundaryFace.PositiveX }));
        Assert.AreEqual(-0.5f, cut.insideMesh.bounds.min.x, 1e-4f,
            "the unrelated -X side must not be clipped");
        Assert.LessOrEqual(cut.insideMesh.bounds.max.x, 0.4001f);

        Object.DestroyImmediate(source);
        Object.DestroyImmediate(cut.outsideMesh);
        Object.DestroyImmediate(cut.insideMesh);
    }
}
