using NUnit.Framework;
using UnityEngine;
using SpatialGeneration.Generation;

/// <summary>
/// A reconstruction arrives in the source image's camera frame. These pin the correction
/// that brings it back to level and square.
///
/// The tilt is baked into the vertices rather than set on the transform, because that is
/// where it lives in reality: TripoSR emits tilted geometry into an object whose transform
/// is identity. Levelling measures the mesh in its owner's local frame, so a test that only
/// rotated the transform would present perfectly level geometry and assert nothing.
/// </summary>
public class MeshAlignmentTests
{
    private GameObject _target;

    [TearDown]
    public void TearDown()
    {
        if (_target == null)
            return;

        MeshFilter filter = _target.GetComponent<MeshFilter>();
        if (filter != null && filter.sharedMesh != null)
            Object.DestroyImmediate(filter.sharedMesh);

        Object.DestroyImmediate(_target);
        _target = null;
    }

    /// <summary>
    /// A box whose faces are subdivided, so the walls carry enough triangles to fit an axis
    /// to — a bare 12-triangle cube has only eight, and is rejected by design.
    /// </summary>
    private GameObject BuildBox(Vector3 halfExtents, Quaternion bakedRotation, int subdivisions = 3)
    {
        var vertices = new System.Collections.Generic.List<Vector3>();
        var triangles = new System.Collections.Generic.List<int>();

        void Face(Vector3 origin, Vector3 across, Vector3 down)
        {
            int start = vertices.Count;
            for (int i = 0; i <= subdivisions; i++)
            {
                for (int j = 0; j <= subdivisions; j++)
                {
                    vertices.Add(bakedRotation * (origin
                        + across * ((float)i / subdivisions)
                        + down * ((float)j / subdivisions)));
                }
            }

            for (int i = 0; i < subdivisions; i++)
            {
                for (int j = 0; j < subdivisions; j++)
                {
                    int a = start + i * (subdivisions + 1) + j;
                    int b = a + 1;
                    int c = a + subdivisions + 1;
                    int d = c + 1;
                    triangles.Add(a); triangles.Add(b); triangles.Add(d);
                    triangles.Add(a); triangles.Add(d); triangles.Add(c);
                }
            }
        }

        Vector3 size = halfExtents * 2f;
        Vector3 corner = -halfExtents;
        Vector3 x = Vector3.right * size.x;
        Vector3 y = Vector3.up * size.y;
        Vector3 z = Vector3.forward * size.z;

        Face(corner, x, y);             // walls
        Face(corner + z, x, y);
        Face(corner, y, z);
        Face(corner + x, y, z);
        Face(corner, x, z);             // base and top
        Face(corner + y, x, z);

        var mesh = new Mesh { name = "Lifted" };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        _target = new GameObject("Lifted");
        _target.AddComponent<MeshFilter>().sharedMesh = mesh;
        _target.AddComponent<MeshRenderer>();
        return _target;
    }

    /// <summary>Where the geometry's own up ends up pointing once the object is placed.</summary>
    private static float ResidualTilt(GameObject go, Quaternion bakedRotation) =>
        Vector3.Angle(go.transform.rotation * bakedRotation * Vector3.up, Vector3.up);

    [Test]
    public void RemovesTiltBakedIntoTheGeometry()
    {
        Quaternion baked = Quaternion.Euler(6f, 0f, 4f);
        GameObject go = BuildBox(new Vector3(1.5f, 0.8f, 0.6f), baked);

        MeshAlignment.Level(go);

        Assert.Less(ResidualTilt(go, baked), 0.5f, "the geometry's own up should end up vertical");
    }

    [Test]
    public void RemovesTiltOnBothAxesAcrossItsWorkingRange()
    {
        foreach (float tilt in new[] { 2f, 5f, 12f, 20f })
        {
            Quaternion baked = Quaternion.Euler(tilt, 0f, 0f);
            GameObject go = BuildBox(new Vector3(1.2f, 0.9f, 0.7f), baked);

            MeshAlignment.Level(go);

            Assert.Less(ResidualTilt(go, baked), 1f, $"failed to level a {tilt} degree pitch");
            TearDown();
        }
    }

    [Test]
    public void SquaresAThreeQuarterYawToTheAxes()
    {
        Quaternion baked = Quaternion.Euler(0f, 34f, 0f);
        GameObject go = BuildBox(new Vector3(1.5f, 0.8f, 0.6f), baked);

        MeshAlignment.Level(go);

        // The box is 3.0 x 1.6 x 1.2; squared up, its world AABB matches those dimensions.
        Bounds bounds = go.GetComponent<MeshRenderer>().bounds;
        Assert.AreEqual(3.0f, bounds.size.x, 0.15f, "footprint is not square-on in x");
        Assert.AreEqual(1.2f, bounds.size.z, 0.15f, "footprint is not square-on in z");
    }

    /// <summary>
    /// The failure that a full-quadrant search caused on a real mesh: it chose 63 degrees
    /// where the intended correction was -27, standing the asset side-on to its proxy.
    /// </summary>
    [Test]
    public void DoesNotTurnTheAssetAQuarterTurn()
    {
        Quaternion baked = Quaternion.Euler(0f, 34f, 0f);
        GameObject go = BuildBox(new Vector3(1.5f, 0.8f, 0.6f), baked);

        Quaternion correction = MeshAlignment.Level(go);

        // The long axis must still run along x, not have swapped into z.
        Vector3 longAxis = correction * baked * Vector3.right;
        Assert.Greater(Mathf.Abs(longAxis.x), Mathf.Abs(longAxis.z),
            "the asset was turned a quarter turn instead of being squared up");
    }

    [Test]
    public void HandlesTiltAndYawTogether()
    {
        Quaternion baked = Quaternion.Euler(7f, 31f, 5f);
        GameObject go = BuildBox(new Vector3(1.5f, 0.8f, 0.6f), baked);

        MeshAlignment.Level(go);

        Assert.Less(ResidualTilt(go, baked), 1f);
        Bounds bounds = go.GetComponent<MeshRenderer>().bounds;
        Assert.AreEqual(1.6f, bounds.size.y, 0.15f, "height should be recovered once level");
    }

    [Test]
    public void LeavesAnAlreadyLevelMeshAlone()
    {
        GameObject go = BuildBox(new Vector3(1.5f, 0.8f, 0.6f), Quaternion.identity);
        Vector3 before = go.GetComponent<MeshRenderer>().bounds.size;

        Quaternion correction = MeshAlignment.Level(go);

        Assert.Less(Quaternion.Angle(correction, Quaternion.identity), 1f,
            "a level mesh should not be rotated");
        Vector3 after = go.GetComponent<MeshRenderer>().bounds.size;
        Assert.AreEqual(before.x, after.x, 0.05f);
        Assert.AreEqual(before.z, after.z, 0.05f);
    }

    /// <summary>
    /// Two parallel walls fix no up axis. The solver has to decline rather than return an
    /// arbitrary perpendicular, which would tilt geometry that was never crooked.
    /// </summary>
    [Test]
    public void DeclinesWhenTheWallsAreAllParallel()
    {
        var vertices = new System.Collections.Generic.List<Vector3>();
        var triangles = new System.Collections.Generic.List<int>();
        for (int plane = 0; plane < 2; plane++)
        {
            int start = vertices.Count;
            float z = plane == 0 ? -1f : 1f;
            for (int i = 0; i <= 4; i++)
            {
                for (int j = 0; j <= 4; j++)
                    vertices.Add(new Vector3(i * 0.5f - 1f, j * 0.5f - 1f, z));
            }

            for (int i = 0; i < 4; i++)
            {
                for (int j = 0; j < 4; j++)
                {
                    int a = start + i * 5 + j;
                    triangles.Add(a); triangles.Add(a + 1); triangles.Add(a + 6);
                    triangles.Add(a); triangles.Add(a + 6); triangles.Add(a + 5);
                }
            }
        }

        var mesh = new Mesh { name = "ParallelWalls" };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();

        _target = new GameObject("ParallelWalls");
        _target.AddComponent<MeshFilter>().sharedMesh = mesh;
        _target.AddComponent<MeshRenderer>();

        Quaternion correction = MeshAlignment.Level(_target);

        Assert.Less(Quaternion.Angle(correction, Quaternion.identity), 1f,
            "an undetermined up axis must produce no tilt correction");
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
