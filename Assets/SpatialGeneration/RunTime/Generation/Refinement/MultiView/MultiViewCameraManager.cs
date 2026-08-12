using System;
using UnityEngine;
using SpatialGeneration.Utils;

public enum ViewType
{
    Front = 0,
    Left = 1,
    Right = 2,
    /// <summary>Retained only so old serialized enum values are not reinterpreted.</summary>
    Top = 3,
    Back = 4
}

/// <summary>
/// The four canonical cameras used for refinement capture.
///
/// Their layout is fixed relative to <see cref="rigTarget"/> and <see cref="rigRotation"/>:
/// per-view images must stay identically projected and identically sized, otherwise the
/// server-side inpaints drift apart and the reconstruction loses cross-view agreement.
/// </summary>
[ExecuteAlways]
public class MultiViewCameraManager : MonoBehaviour
{
    public static readonly ViewType[] AllViews =
        { ViewType.Front, ViewType.Back, ViewType.Left, ViewType.Right };

    [Header("Cameras (created by Apply Layout)")]
    public Camera frontCamera;
    public Camera backCamera;
    public Camera leftCamera;
    public Camera rightCamera;
    [HideInInspector]
    public Camera topCamera;

    [Header("Rig")]
    [Tooltip("World point every camera looks at.")]
    public Vector3 rigTarget = Vector3.zero;

    [Tooltip("Orientation of the rig. Front looks down the rig's -X axis.")]
    public Quaternion rigRotation = Quaternion.identity;

    [Tooltip("Distance from rigTarget to each camera.")]
    public float rigDistance = 4f;

    [Tooltip("Half-height of the orthographic frustum.")]
    public float orthographicSize = 2f;

    public Vector2Int captureResolution = new(512, 512);
    public float nearClip = 0.01f;
    public float farClip = 1000f;

    public Camera GetCamera(ViewType type) => type switch
    {
        ViewType.Left => leftCamera,
        ViewType.Right => rightCamera,
        ViewType.Back => backCamera,
        ViewType.Top => backCamera != null ? backCamera : topCamera,
        _ => frontCamera
    };

    /// <summary>
    /// Creates any missing cameras and places all four around <see cref="rigTarget"/>.
    /// Front sits on the rig's +X looking back along -X, so a proxy's local +X face is
    /// what "front" means to the user.
    /// </summary>
    [ContextMenu("Apply Layout")]
    public void ApplyLayout()
    {
        MigrateTopCameraToBack();

        frontCamera = EnsureCamera(frontCamera, "MultiView_Front");
        backCamera = EnsureCamera(backCamera, "MultiView_Back");
        leftCamera = EnsureCamera(leftCamera, "MultiView_Left");
        rightCamera = EnsureCamera(rightCamera, "MultiView_Right");

        Place(frontCamera, new Vector3(rigDistance, 0f, 0f), Quaternion.LookRotation(Vector3.left, Vector3.up));
        Place(backCamera, new Vector3(-rigDistance, 0f, 0f), Quaternion.LookRotation(Vector3.right, Vector3.up));
        Place(leftCamera, new Vector3(0f, 0f, -rigDistance), Quaternion.LookRotation(Vector3.forward, Vector3.up));
        Place(rightCamera, new Vector3(0f, 0f, rigDistance), Quaternion.LookRotation(Vector3.back, Vector3.up));
    }

    /// <summary>Reuses the old fourth camera so existing scene rigs migrate without duplication.</summary>
    private void MigrateTopCameraToBack()
    {
        if (backCamera != null || topCamera == null)
            return;

        backCamera = topCamera;
        topCamera = null;
        backCamera.gameObject.name = "MultiView_Back";
    }

    /// <summary>Fails loudly if a caller has desynchronised the rig behind our back.</summary>
    public void ValidateCanonicalConsistency()
    {
        foreach (ViewType view in AllViews)
        {
            Camera camera = GetCamera(view);
            if (camera == null)
                throw new InvalidOperationException($"MultiViewCameraManager: the {view} camera is missing.");
            if (!camera.orthographic)
                throw new InvalidOperationException($"MultiViewCameraManager: the {view} camera must be orthographic.");
            if (!Mathf.Approximately(camera.orthographicSize, frontCamera.orthographicSize))
                throw new InvalidOperationException(
                    $"MultiViewCameraManager: {view} orthographicSize ({camera.orthographicSize}) " +
                    $"does not match Front ({frontCamera.orthographicSize}).");
        }
    }

    private void Place(Camera camera, Vector3 rigSpaceOffset, Quaternion rigSpaceRotation)
    {
        if (camera == null)
            return;

        camera.transform.SetPositionAndRotation(
            rigTarget + rigRotation * rigSpaceOffset,
            rigRotation * rigSpaceRotation);

        camera.orthographic = true;
        camera.orthographicSize = Mathf.Max(0.01f, orthographicSize);
        camera.nearClipPlane = Mathf.Max(0.001f, nearClip);
        camera.farClipPlane = Mathf.Max(camera.nearClipPlane + 1f, farClip);
        camera.allowHDR = false;
        camera.allowMSAA = false;
        camera.enabled = false;
    }

    /// <summary>
    /// The camera for <paramref name="childName"/>, creating the object or the component if
    /// either is missing.
    ///
    /// A rig object that outlived its Camera is a real state — a hierarchy carried over from
    /// an earlier session, or a component removed by hand — so the object and the component
    /// are checked separately rather than assumed to exist together.
    /// </summary>
    private Camera EnsureCamera(Camera existing, string childName)
    {
        if (existing != null)
            return existing;

        Transform child = transform.Find(childName);
        GameObject go = child != null ? child.gameObject : new GameObject(childName);
        go.transform.SetParent(transform, worldPositionStays: false);

        Camera camera = ComponentUtils.GetOrAdd<Camera>(go);
        camera.enabled = false;
        return camera;
    }
}
