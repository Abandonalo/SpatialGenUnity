using NUnit.Framework;
using UnityEngine;
using SpatialGeneration.Generation;

/// <summary>
/// Pins the placement rule that decides how much of the authored proxy a generated asset
/// occupies. The numbers come from a real TripoSR output measured against a unit proxy.
/// </summary>
public class MeshFittingTests
{
    /// <summary>Bounds of an actual generated crate: TripoSR normalises the long axis to ~1.</summary>
    private static readonly Vector3 ReconstructionSize = new(1.0054f, 0.4422f, 0.3676f);

    private GameObject _target;

    [TearDown]
    public void TearDown()
    {
        if (_target == null)
            return;

        Object.DestroyImmediate(_target.GetComponent<MeshFilter>().sharedMesh);
        Object.DestroyImmediate(_target);
        _target = null;
    }

    /// <summary>A box mesh of the given size, centred on its own origin.</summary>
    private GameObject BuildBox(Vector3 size)
    {
        Vector3 h = size * 0.5f;
        var mesh = new Mesh { name = "Box" };
        mesh.SetVertices(new[]
        {
            new Vector3(-h.x, -h.y, -h.z), new Vector3(h.x, -h.y, -h.z),
            new Vector3(h.x, h.y, -h.z), new Vector3(-h.x, h.y, -h.z),
            new Vector3(-h.x, -h.y, h.z), new Vector3(h.x, -h.y, h.z),
            new Vector3(h.x, h.y, h.z), new Vector3(-h.x, h.y, h.z)
        });
        mesh.SetTriangles(new[]
        {
            0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7,
            0, 1, 5, 0, 5, 4, 2, 3, 7, 2, 7, 6,
            1, 2, 6, 1, 6, 5, 0, 4, 7, 0, 7, 3
        }, 0);
        mesh.RecalculateNormals();

        _target = new GameObject("Generated");
        _target.AddComponent<MeshFilter>().sharedMesh = mesh;
        _target.AddComponent<MeshRenderer>();
        return _target;
    }

    private static Bounds WorldBounds(GameObject go) => go.GetComponent<MeshRenderer>().bounds;

    [Test]
    public void FillMode_OccupiesTheWholeProxy()
    {
        GameObject go = BuildBox(ReconstructionSize);
        Vector3 proxySize = Vector3.one;

        MeshFitting.FitToVolume(go, proxySize, Vector3.zero, preserveProportions: false);

        Bounds bounds = WorldBounds(go);
        Assert.AreEqual(proxySize.x, bounds.size.x, 1e-3f);
        Assert.AreEqual(proxySize.y, bounds.size.y, 1e-3f);
        Assert.AreEqual(proxySize.z, bounds.size.z, 1e-3f);
    }

    [Test]
    public void PreserveProportions_KeepsShapeAndStaysInsideTheProxy()
    {
        GameObject go = BuildBox(ReconstructionSize);

        MeshFitting.FitToVolume(go, Vector3.one, Vector3.zero, preserveProportions: true);

        Bounds bounds = WorldBounds(go);
        Assert.LessOrEqual(bounds.size.x, 1f + 1e-3f);
        Assert.LessOrEqual(bounds.size.y, 1f + 1e-3f);
        Assert.LessOrEqual(bounds.size.z, 1f + 1e-3f);

        // Aspect ratio survives, which is the whole point of this mode.
        Assert.AreEqual(
            ReconstructionSize.y / ReconstructionSize.x, bounds.size.y / bounds.size.x, 1e-3f);
    }

    [Test]
    public void PreserveProportions_IsTheModeThatUnderfills()
    {
        // Documents why fill is the default: contain leaves this asset at 44% and 37%.
        GameObject go = BuildBox(ReconstructionSize);

        MeshFitting.FitToVolume(go, Vector3.one, Vector3.zero, preserveProportions: true);

        Bounds bounds = WorldBounds(go);
        Assert.Less(bounds.size.y, 0.5f);
        Assert.Less(bounds.size.z, 0.5f);
    }

    [Test]
    public void CentresOnTheProxy()
    {
        GameObject go = BuildBox(ReconstructionSize);
        var center = new Vector3(3f, -2f, 5f);

        MeshFitting.FitToVolume(go, Vector3.one, center, preserveProportions: false);

        Bounds bounds = WorldBounds(go);
        Assert.AreEqual(center.x, bounds.center.x, 1e-3f);
        Assert.AreEqual(center.y, bounds.center.y, 1e-3f);
        Assert.AreEqual(center.z, bounds.center.z, 1e-3f);
    }

    [Test]
    public void RotatedAsset_IsNotShrunkByItsWorldAlignedBounds()
    {
        // Measuring a 45-degree-rotated asset with Renderer.bounds overstates its size by
        // ~41%, which used to scale it down for no reason. Measuring along its own axes
        // must give the same result as the unrotated case.
        GameObject go = BuildBox(ReconstructionSize);
        go.transform.rotation = Quaternion.Euler(0f, 45f, 0f);

        MeshFitting.FitToVolume(go, Vector3.one, Vector3.zero, preserveProportions: false);

        // The oriented extent should match the proxy exactly; the world AABB is larger
        // because the box is turned, so compare in the object's own frame.
        Mesh mesh = go.GetComponent<MeshFilter>().sharedMesh;
        Vector3 scaled = Vector3.Scale(mesh.bounds.size, go.transform.localScale);
        Assert.AreEqual(1f, scaled.x, 1e-3f);
        Assert.AreEqual(1f, scaled.y, 1e-3f);
        Assert.AreEqual(1f, scaled.z, 1e-3f);
    }

    [Test]
    public void HandlesNonUnitProxyDimensions()
    {
        GameObject go = BuildBox(ReconstructionSize);
        var proxySize = new Vector3(0.4f, 2f, 0.9f);

        MeshFitting.FitToVolume(go, proxySize, Vector3.zero, preserveProportions: false);

        Bounds bounds = WorldBounds(go);
        Assert.AreEqual(proxySize.x, bounds.size.x, 1e-3f);
        Assert.AreEqual(proxySize.y, bounds.size.y, 1e-3f);
        Assert.AreEqual(proxySize.z, bounds.size.z, 1e-3f);
    }
}
