using NUnit.Framework;
using UnityEngine;

public class SelectionSpaceBoundsTests
{
    [Test]
    public void MeasuresRemovedGeometryInsteadOfThePaddedSelectionBox()
    {
        var selection = new RegionSelection
        {
            center = new Vector3(4f, 2f, -3f),
            size = new Vector3(10f, 8f, 6f),
            rotation = Quaternion.Euler(0f, 35f, 0f)
        };
        Mesh mesh = Box(new Vector3(2f, 1f, 0.5f));
        Vector3 localOffset = new(1.25f, -0.5f, 0.75f);
        Matrix4x4 localToWorld = Matrix4x4.TRS(
            selection.center + selection.rotation * localOffset,
            selection.rotation,
            Vector3.one);
        var measured = new SelectionSpaceBounds(selection);

        measured.Encapsulate(mesh, localToWorld);

        Assert.IsTrue(measured.IsValid);
        Assert.That(measured.Size.x, Is.EqualTo(2f).Within(1e-4f));
        Assert.That(measured.Size.y, Is.EqualTo(1f).Within(1e-4f));
        Assert.That(measured.Size.z, Is.EqualTo(0.5f).Within(1e-4f));
        Assert.That(Vector3.Distance(
            measured.WorldCenter,
            selection.center + selection.rotation * localOffset), Is.LessThan(1e-4f));
        Assert.AreNotEqual(selection.size, measured.Size);

        Object.DestroyImmediate(mesh);
    }

    [Test]
    public void CombinesAllRemovedSourceFragments()
    {
        var selection = new RegionSelection
        {
            center = Vector3.zero,
            size = Vector3.one * 20f,
            rotation = Quaternion.identity
        };
        Mesh first = Box(Vector3.one);
        Mesh second = Box(Vector3.one);
        var measured = new SelectionSpaceBounds(selection);

        measured.Encapsulate(first, Matrix4x4.Translate(new Vector3(-2f, 0f, 0f)));
        measured.Encapsulate(second, Matrix4x4.Translate(new Vector3(2f, 0f, 0f)));

        Assert.That(measured.Size.x, Is.EqualTo(5f).Within(1e-4f));
        Assert.That(measured.Size.y, Is.EqualTo(1f).Within(1e-4f));
        Assert.That(measured.Size.z, Is.EqualTo(1f).Within(1e-4f));
        Assert.That(measured.WorldCenter, Is.EqualTo(Vector3.zero));

        Object.DestroyImmediate(first);
        Object.DestroyImmediate(second);
    }

    private static Mesh Box(Vector3 size)
    {
        Vector3 h = size * 0.5f;
        var mesh = new Mesh();
        mesh.vertices = new[]
        {
            new Vector3(-h.x, -h.y, -h.z), new Vector3(h.x, -h.y, -h.z),
            new Vector3(h.x, h.y, -h.z), new Vector3(-h.x, h.y, -h.z),
            new Vector3(-h.x, -h.y, h.z), new Vector3(h.x, -h.y, h.z),
            new Vector3(h.x, h.y, h.z), new Vector3(-h.x, h.y, h.z)
        };
        mesh.triangles = new[]
        {
            0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7,
            0, 1, 5, 0, 5, 4, 2, 3, 7, 2, 7, 6,
            1, 2, 6, 1, 6, 5, 0, 4, 7, 0, 7, 3
        };
        return mesh;
    }
}
