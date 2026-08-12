using NUnit.Framework;
using UnityEngine;
using SpatialGeneration.Generation;

/// <summary>
/// A reconstruction arrives in the source image's camera frame. These pin the correction
/// that brings it back to level and square.
///
/// Two things here mirror reality rather than convenience. The tilt is baked into the
/// vertices, because that is where it lives: TripoSR emits tilted geometry into an object
/// whose transform is identity. And the mesh hangs off a child of the root, as glTFast
/// imports it, because the correction is applied below the root -- the root's scale is
/// spoken for by the proxy fit.
/// </summary>
public class MeshAlignmentTests
{
    private GameObject _root;
    private Transform _geometry;

    [TearDown]
    public void TearDown()
    {
        if (_root == null)
            return;

        foreach (MeshFilter filter in _root.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter.sharedMesh != null)
                Object.DestroyImmediate(filter.sharedMesh);
        }

        Object.DestroyImmediate(_root);
        _root = null;
        _geometry = null;
    }

    /// <summary>
    /// A root whose child carries a subdivided box, tilted by <paramref name="bakedRotation"/>.
    /// Subdivided because the walls must carry enough triangles to fit an axis to -- a bare
    /// 12-triangle cube has only eight, and is declined by design.
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

        Vector3 corner = -halfExtents;
        Vector3 x = Vector3.right * halfExtents.x * 2f;
        Vector3 y = Vector3.up * halfExtents.y * 2f;
        Vector3 z = Vector3.forward * halfExtents.z * 2f;

        Face(corner, x, y);
        Face(corner + z, x, y);
        Face(corner, y, z);
        Face(corner + x, y, z);
        Face(corner, x, z);
        Face(corner + y, x, z);

        var mesh = new Mesh { name = "Lifted" };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        _root = new GameObject("Lifted");
        var child = new GameObject("geometry_0");
        child.transform.SetParent(_root.transform, worldPositionStays: false);
        child.AddComponent<MeshFilter>().sharedMesh = mesh;
        child.AddComponent<MeshRenderer>();
        _geometry = child.transform;
        return _root;
    }

    /// <summary>Where the geometry's own up actually points, through the whole transform chain.</summary>
    private float ResidualTilt(Quaternion bakedRotation) =>
        Vector3.Angle(_geometry.localToWorldMatrix.MultiplyVector(bakedRotation * Vector3.up), Vector3.up);

    [Test]
    public void RemovesTiltBakedIntoTheGeometry()
    {
        Quaternion baked = Quaternion.Euler(6f, 0f, 4f);
        MeshAlignment.Level(BuildBox(new Vector3(1.5f, 0.8f, 0.6f), baked));

        Assert.Less(ResidualTilt(baked), 0.5f, "the geometry's own up should end up vertical");
    }

    [Test]
    public void RemovesTiltAcrossItsWorkingRange()
    {
        foreach (float tilt in new[] { 2f, 5f, 12f, 20f })
        {
            Quaternion baked = Quaternion.Euler(tilt, 0f, 0f);
            MeshAlignment.Level(BuildBox(new Vector3(1.2f, 0.9f, 0.7f), baked));

            Assert.Less(ResidualTilt(baked), 1f, $"failed to level a {tilt} degree pitch");
            TearDown();
        }
    }

    /// <summary>
    /// The regression that made this visible in the editor. Levelling used to live in the
    /// root's rotation, so the proxy fill scale -- written to the root's localScale, which
    /// applies underneath that rotation -- stretched geometry that was still tilted and
    /// tipped the asset back over by up to 6.4 degrees.
    /// </summary>
    [Test]
    public void SurvivesTheNonUniformScaleThatFillsTheProxy()
    {
        Quaternion baked = Quaternion.Euler(7f, 0f, 0f);
        GameObject root = BuildBox(new Vector3(1.5f, 0.8f, 0.6f), baked);

        Quaternion correction = MeshAlignment.Level(root);
        root.transform.rotation = Quaternion.Euler(0f, 180f, 0f);   // GetMeshRotationForProxy
        root.transform.localScale = new Vector3(2f, 1f, 1.6f);      // FitToVolume, fill mode

        Assert.Less(ResidualTilt(baked), 0.5f,
            "a non-uniform fill scale must not reintroduce tilt");
    }

    [Test]
    public void SquaresAThreeQuarterYawToTheAxes()
    {
        Quaternion baked = Quaternion.Euler(0f, 34f, 0f);
        GameObject root = BuildBox(new Vector3(1.5f, 0.8f, 0.6f), baked);

        Quaternion correction = MeshAlignment.Level(root);

        // The box is 3.0 x 1.6 x 1.2; squared up, its world AABB matches those dimensions.
        Bounds bounds = GeometryBounds();
        Assert.AreEqual(3.0f, bounds.size.x, 0.15f,
            $"footprint is not square-on in x; correction={correction.eulerAngles}");
        Assert.AreEqual(1.2f, bounds.size.z, 0.15f, "footprint is not square-on in z");
    }

    /// <summary>
    /// The failure a full-quadrant search caused on a real mesh: it chose 63 degrees where
    /// the intended correction was -27, standing the asset side-on to its proxy.
    /// </summary>
    [Test]
    public void DoesNotTurnTheAssetAQuarterTurn()
    {
        Quaternion baked = Quaternion.Euler(0f, 34f, 0f);
        MeshAlignment.Level(BuildBox(new Vector3(1.5f, 0.8f, 0.6f), baked));

        Vector3 longAxis = _geometry.localRotation * baked * Vector3.right;
        Assert.Greater(Mathf.Abs(longAxis.x), Mathf.Abs(longAxis.z),
            "the asset was turned a quarter turn instead of being squared up");
    }

    [Test]
    public void HandlesTiltAndYawTogether()
    {
        Quaternion baked = Quaternion.Euler(7f, 31f, 5f);
        Quaternion correction = MeshAlignment.Level(BuildBox(new Vector3(1.5f, 0.8f, 0.6f), baked));

        Assert.Less(ResidualTilt(baked), 1f);
        Bounds bounds = GeometryBounds();
        Assert.AreEqual(1.6f, bounds.size.y, 0.15f,
            $"height should be recovered once level; correction={correction.eulerAngles}");
    }

    [Test]
    public void LeavesAnAlreadyLevelMeshAlone()
    {
        GameObject root = BuildBox(new Vector3(1.5f, 0.8f, 0.6f), Quaternion.identity);
        Vector3 before = GeometryBounds().size;

        Quaternion correction = MeshAlignment.Level(root);

        Assert.Less(Quaternion.Angle(correction, Quaternion.identity), 1f,
            "a level mesh should not be rotated");
        Vector3 after = GeometryBounds().size;
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

        _root = new GameObject("ParallelWalls");
        var child = new GameObject("geometry_0");
        child.transform.SetParent(_root.transform, worldPositionStays: false);
        child.AddComponent<MeshFilter>().sharedMesh = mesh;
        child.AddComponent<MeshRenderer>();
        _geometry = child.transform;

        Quaternion correction = MeshAlignment.Level(_root);

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

    private Bounds GeometryBounds()
    {
        Mesh mesh = _geometry.GetComponent<MeshFilter>().sharedMesh;
        Vector3[] vertices = mesh.vertices;
        var bounds = new Bounds(_geometry.TransformPoint(vertices[0]), Vector3.zero);
        for (int i = 1; i < vertices.Length; i++)
            bounds.Encapsulate(_geometry.TransformPoint(vertices[i]));
        return bounds;
    }
}
