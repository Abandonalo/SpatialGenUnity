using NUnit.Framework;
using UnityEngine;

public class MultiViewCameraManagerTests
{
    private GameObject rig;
    private MultiViewCameraManager manager;

    [SetUp]
    public void SetUp()
    {
        rig = new GameObject("Rig");
        manager = rig.AddComponent<MultiViewCameraManager>();
    }

    [TearDown]
    public void TearDown() => Object.DestroyImmediate(rig);

    [Test]
    public void CardinalOrderPreservesSerializedValuesAndAddsBackLast()
    {
        CollectionAssert.AreEqual(
            new[] { ViewType.Front, ViewType.Back, ViewType.Left, ViewType.Right },
            MultiViewCameraManager.AllViews);
        Assert.AreEqual(0, (int)ViewType.Front);
        Assert.AreEqual(1, (int)ViewType.Left);
        Assert.AreEqual(2, (int)ViewType.Right);
        Assert.AreEqual(3, (int)ViewType.Top);
        Assert.AreEqual(4, (int)ViewType.Back);
    }

    [Test]
    public void ApplyLayoutMigratesTopCameraToBackWithoutCreatingADuplicate()
    {
        var old = new GameObject("MultiView_Top").AddComponent<Camera>();
        old.transform.SetParent(rig.transform);
        manager.topCamera = old;

        manager.ApplyLayout();

        Assert.AreSame(old, manager.backCamera);
        Assert.IsNull(manager.topCamera);
        Assert.AreEqual("MultiView_Back", old.gameObject.name);
        Assert.AreSame(old, manager.GetCamera(ViewType.Top));
        Assert.AreEqual(4, rig.GetComponentsInChildren<Camera>(true).Length);
    }

    [Test]
    public void CameraPositionsAndDirectionsAreDeterministicInRigSpace()
    {
        manager.rigTarget = new Vector3(3f, 4f, 5f);
        manager.rigRotation = Quaternion.Euler(0f, 35f, 0f);
        manager.rigDistance = 7f;
        manager.ApplyLayout();

        AssertVector(manager.rigTarget + manager.rigRotation * Vector3.right * 7f,
            manager.frontCamera.transform.position);
        AssertVector(manager.rigTarget + manager.rigRotation * Vector3.left * 7f,
            manager.backCamera.transform.position);
        AssertVector((manager.rigTarget - manager.frontCamera.transform.position).normalized,
            manager.frontCamera.transform.forward);
        AssertVector((manager.rigTarget - manager.backCamera.transform.position).normalized,
            manager.backCamera.transform.forward);
    }

    [Test]
    public void ViewCropMetadataIsIndependentPerView()
    {
        var front = new ViewData { viewType = "Front", cropMinX = 0.1f, cropMaxX = 0.6f };
        var back = new ViewData { viewType = "Back", cropMinX = 0.3f, cropMaxX = 0.9f };
        Assert.AreNotEqual(front.cropMinX, back.cropMinX);
        Assert.AreNotEqual(front.cropMaxX, back.cropMaxX);
    }

    private static void AssertVector(Vector3 expected, Vector3 actual) =>
        Assert.Less((expected - actual).sqrMagnitude, 1e-8f, $"Expected {expected}, got {actual}");
}
