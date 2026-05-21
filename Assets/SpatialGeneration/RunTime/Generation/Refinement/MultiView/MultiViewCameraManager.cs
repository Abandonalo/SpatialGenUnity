using System.Collections.Generic;
using UnityEngine;

public enum ViewType
{
    Front = 0,
    Left = 1,
    Right = 2,
    Top = 3
}

// MultiViewCameraManager owns the canonical camera rig used by the
// multi-view refinement pipeline. The cameras are treated as fixed -
// same position, rotation, resolution and projection across every
// refinement iteration - so that per-view latents stay spatially
// aligned between runs and can be fused deterministically.
[ExecuteAlways]
public class MultiViewCameraManager : MonoBehaviour
{
    [Header("Canonical Cameras")]
    [Tooltip("Camera on +X of rigTarget, looking at -X in rig space. Align RegionSelection rotation so local +X is the asset front.")]
    public Camera frontCamera;
    public Camera leftCamera;
    public Camera rightCamera;
    public Camera topCamera;

    [Header("Rig Defaults (applied on Auto Setup)")]
    [Tooltip("World-space point the canonical cameras look at.")]
    public Vector3 rigTarget = Vector3.zero;
    [Tooltip("Distance from rigTarget to each camera along its view axis.")]
    public float rigDistance = 4f;
    [Tooltip("Height of the Top camera above rigTarget (if using auto setup).")]
    public float topHeight = 4f;
    [Tooltip("If true, use orthographic projection for every canonical camera.")]
    public bool orthographic = true;
    [Tooltip("Orthographic size (half-height) when orthographic is true.")]
    public float orthographicSize = 2f;
    [Tooltip("Vertical FOV when orthographic is false.")]
    public float fieldOfView = 40f;
    [Tooltip("Capture resolution shared by every canonical camera.")]
    public Vector2Int captureResolution = new Vector2Int(512, 512);
    [Tooltip("Near clip plane applied to every canonical camera.")]
    public float nearClip = 0.01f;
    [Tooltip("Far clip plane applied to every canonical camera.")]
    public float farClip = 1000f;

    public Camera GetCamera(ViewType type)
    {
        switch (type)
        {
            case ViewType.Front: return frontCamera;
            case ViewType.Left: return leftCamera;
            case ViewType.Right: return rightCamera;
            case ViewType.Top: return topCamera;
            default: return frontCamera;
        }
    }

    public List<ViewType> GetAllViews()
    {
        return new List<ViewType>
        {
            ViewType.Front,
            ViewType.Left,
            ViewType.Right,
            ViewType.Top
        };
    }

    public bool HasAllCameras()
    {
        return frontCamera != null
               && leftCamera != null
               && rightCamera != null
               && topCamera != null;
    }

    // Validate that every canonical camera uses the same projection,
    // aspect and resolution. The multi-view router and downstream
    // inpainter rely on per-view images being identically sized and
    // identically projected so the mask/depth/RGB triplets align.
    public void ValidateCanonicalConsistency()
    {
        if (!HasAllCameras())
            throw new System.InvalidOperationException(
                "MultiViewCameraManager: Front/Left/Right/Top cameras must all be assigned.");

        Camera reference = frontCamera;
        foreach (ViewType view in GetAllViews())
        {
            Camera cam = GetCamera(view);
            if (cam.orthographic != reference.orthographic)
            {
                throw new System.InvalidOperationException(
                    $"MultiViewCameraManager: {view} camera projection does not match the front camera.");
            }

            if (cam.orthographic)
            {
                if (!Mathf.Approximately(cam.orthographicSize, reference.orthographicSize))
                {
                    throw new System.InvalidOperationException(
                        $"MultiViewCameraManager: {view} orthographicSize ({cam.orthographicSize}) " +
                        $"does not match front ({reference.orthographicSize}).");
                }
            }
            else
            {
                if (!Mathf.Approximately(cam.fieldOfView, reference.fieldOfView))
                {
                    throw new System.InvalidOperationException(
                        $"MultiViewCameraManager: {view} fieldOfView ({cam.fieldOfView}) " +
                        $"does not match front ({reference.fieldOfView}).");
                }
            }
        }
    }

    // Build the four cameras automatically based on rigTarget/rigDistance.
    // Useful when the user does not want to hand-place them. Existing
    // cameras are reused if already assigned; only missing ones are
    // instantiated as children of this transform.
    [ContextMenu("Auto Setup Canonical Cameras")]
    public void AutoSetupCameras()
    {
        frontCamera = EnsureCamera(frontCamera, "MultiView_Front");
        leftCamera = EnsureCamera(leftCamera, "MultiView_Left");
        rightCamera = EnsureCamera(rightCamera, "MultiView_Right");
        topCamera = EnsureCamera(topCamera, "MultiView_Top");

        // Horizontal views are placed relative to rigTarget so local +X is the
        // object's front axis:
        //   Front: camera on +X, looking -X (sees a +X-facing façade).
        //   Left:  camera on -Z, looking +Z.
        //   Right: camera on +Z, looking -Z.
        ConfigureCamera(frontCamera, rigTarget + new Vector3(rigDistance, 0f, 0f), Quaternion.LookRotation(Vector3.left, Vector3.up));
        ConfigureCamera(leftCamera, rigTarget + new Vector3(0f, 0f, -rigDistance), Quaternion.LookRotation(Vector3.forward, Vector3.up));
        ConfigureCamera(rightCamera, rigTarget + new Vector3(0f, 0f, rigDistance), Quaternion.LookRotation(Vector3.back, Vector3.up));
        ConfigureCamera(topCamera, rigTarget + new Vector3(0f, topHeight, 0f), Quaternion.LookRotation(Vector3.down, Vector3.forward));
    }

    private Camera EnsureCamera(Camera existing, string childName)
    {
        if (existing != null)
            return existing;

        Transform child = transform.Find(childName);
        GameObject go = child != null ? child.gameObject : new GameObject(childName);
        go.transform.SetParent(transform, false);
        Camera cam = go.GetComponent<Camera>();
        if (cam == null)
            cam = go.AddComponent<Camera>();
        cam.enabled = false;
        return cam;
    }

    private void ConfigureCamera(Camera cam, Vector3 worldPosition, Quaternion worldRotation)
    {
        if (cam == null)
            return;

        cam.transform.position = worldPosition;
        cam.transform.rotation = worldRotation;
        cam.orthographic = orthographic;
        cam.orthographicSize = Mathf.Max(0.01f, orthographicSize);
        cam.fieldOfView = Mathf.Clamp(fieldOfView, 1f, 179f);
        cam.nearClipPlane = Mathf.Max(0.001f, nearClip);
        cam.farClipPlane = Mathf.Max(cam.nearClipPlane + 1f, farClip);
        cam.allowHDR = false;
        cam.allowMSAA = false;
        cam.enabled = false;
    }
}
