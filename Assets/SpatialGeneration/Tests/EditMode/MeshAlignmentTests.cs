using NUnit.Framework;
using UnityEngine;
using SpatialGeneration.Generation;

/// <summary>
/// A reconstruction arrives in the source image's camera frame. These pin the correction
/// that brings it back to level and square.
/// </summary>
public class MeshAlignmentTests
{
    private GameObject _target;

    [TearDown]
    public void TearDown()
    {
        if (_target == null) return;
        Object.DestroyImmediate(_target.GetComponent<MeshFilter>().sharedMesh);
        Object.DestroyImmediate(_target);
        _target = null;
    }

    /// <summary>A box with a deliberately dominant base, like a building on a slab.</summary>
    private GameObject BuildHouseLike()
    {
        var mesh = new Mesh { name = "HouseLike" };
        Vector3 h = new(1.5f, 0.5f, 0.8f);
        mesh.SetVertices(new[]
        {
            new Vector3(-h.x, -h.y, -h.z), new Vector3(h.x, -h.y, -h.z),
            new Vector3(h.x, -h.y, h.z),   new Vector3(-h.x, -h.y, h.z),
            new Vector3(-h.x, h.y, -h.z),  new Vector3(h.x, h.y, -h.z),
            new Vector3(h.x, h.y, h.z),    new Vector3(-h.x, h.y, h.z)
        });
        mesh.SetTriangles(new[]
        {
            0, 2, 1, 0, 3, 2,       // base
            4, 5, 6, 4, 6, 7,       // top
            0, 1, 5, 0, 5, 4,
            2, 3, 7, 2, 7, 6,
            1, 2, 6, 1, 6, 5,
            0, 4, 7, 0, 7, 3
        }, 0);
        mesh.RecalculateNormals();

        _target = new GameObject("Lifted");
        _target.AddComponent<MeshFilter>().sharedMesh = mesh;
        _target.AddComponent<MeshRenderer>();
        return _target;
    }

    private static float TiltFromVertical(GameObject go) =>
        Vector3.Angle(go.transform.rotation * Vector3.up, Vector3.up);

    [Test]
    public void RemovesTiltFromAnElevatedCameraFrame()
    {
        GameObject go = BuildHouseLike();
        go.transform.rotation = Quaternion.Euler(23f, 0f, 11f);

        MeshAlignment.Level(go);

        Assert.Less(TiltFromVertical(go), 1f, "the object should end up level");
    }

    [Test]
    public void SquaresAThreeQuarterYawToTheAxes()
    {
        GameObject go = BuildHouseLike();
        go.transform.rotation = Quaternion.Euler(0f, 37f, 0f);

        MeshAlignment.Level(go);

        // Any multiple of 90 degrees is square; the algorithm has no notion of "front".
        float yaw = Mathf.Repeat(go.transform.rotation.eulerAngles.y, 90f);
        float offBy = Mathf.Min(yaw, 90f - yaw);
        Assert.Less(offBy, 1.5f, $"footprint should be axis-aligned, off by {offBy} degrees");
    }

    [Test]
    public void HandlesTiltAndYawTogether()
    {
        GameObject go = BuildHouseLike();
        go.transform.rotation = Quaternion.Euler(18f, 52f, 7f);

        MeshAlignment.Level(go);

        Assert.Less(TiltFromVertical(go), 1.5f);
        Bounds bounds = go.GetComponent<MeshRenderer>().bounds;
        // A square-on box: its world AABB matches its true dimensions.
        Assert.AreEqual(3.0f, bounds.size.x, 0.1f);
        Assert.AreEqual(1.0f, bounds.size.y, 0.1f);
        Assert.AreEqual(1.6f, bounds.size.z, 0.1f);
    }

    [Test]
    public void LeavesAnAlreadyLevelMeshAlone()
    {
        GameObject go = BuildHouseLike();
        Vector3 before = go.GetComponent<MeshRenderer>().bounds.size;

        MeshAlignment.Level(go);

        Assert.Less(TiltFromVertical(go), 0.5f);
        Vector3 after = go.GetComponent<MeshRenderer>().bounds.size;
        Assert.AreEqual(before.x, after.x, 0.05f);
        Assert.AreEqual(before.z, after.z, 0.05f);
    }

    [Test]
    public void DoesNotThrowWithoutGeometry()
    {
        var empty = new GameObject("Empty");
        Assert.DoesNotThrow(() => MeshAlignment.Level(empty));
        Assert.DoesNotThrow(() => MeshAlignment.Level(null));
        Object.DestroyImmediate(empty);
    }
}
