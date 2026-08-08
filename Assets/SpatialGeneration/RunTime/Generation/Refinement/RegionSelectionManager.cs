using System;
using UnityEngine;

/// <summary>An oriented box the user drew in the scene to scope a local edit.</summary>
[Serializable]
public class RegionSelection
{
    public string selectionId;
    public Vector3 center;
    public Vector3 size;
    public Quaternion rotation = Quaternion.identity;

    public RegionSelection Clone() => new()
    {
        selectionId = selectionId,
        center = center,
        size = size,
        rotation = rotation
    };

    /// <summary>World-space corners of the oriented box.</summary>
    public Vector3[] GetWorldCorners()
    {
        Vector3 h = ClampSize(size) * 0.5f;
        Vector3[] local =
        {
            new(-h.x, -h.y, -h.z), new(h.x, -h.y, -h.z),
            new(-h.x, h.y, -h.z), new(h.x, h.y, -h.z),
            new(-h.x, -h.y, h.z), new(h.x, -h.y, h.z),
            new(-h.x, h.y, h.z), new(h.x, h.y, h.z)
        };

        var world = new Vector3[local.Length];
        for (int i = 0; i < local.Length; i++)
            world[i] = center + rotation * local[i];

        return world;
    }

    /// <summary>Axis-aligned hull of the oriented box.</summary>
    public Bounds GetWorldBounds()
    {
        Vector3[] corners = GetWorldCorners();
        var bounds = new Bounds(corners[0], Vector3.zero);
        for (int i = 1; i < corners.Length; i++)
            bounds.Encapsulate(corners[i]);
        return bounds;
    }

    /// <summary>Maps world space into the box's local frame, where the box is an AABB of ±size/2.</summary>
    public Matrix4x4 WorldToLocal() => Matrix4x4.TRS(center, rotation, Vector3.one).inverse;

    /// <summary>Axis-aligned bounds, in viewport UV, of the corners in front of <paramref name="camera"/>.</summary>
    public bool TryGetViewportUvBounds(Camera camera, out Vector4 minMaxXy)
    {
        minMaxXy = default;
        if (camera == null)
            return false;

        bool any = false;
        float minX = 1f, minY = 1f, maxX = 0f, maxY = 0f;

        foreach (Vector3 corner in GetWorldCorners())
        {
            Vector3 viewport = camera.WorldToViewportPoint(corner);
            if (viewport.z <= 0f)
                continue;

            any = true;
            minX = Mathf.Min(minX, viewport.x);
            minY = Mathf.Min(minY, viewport.y);
            maxX = Mathf.Max(maxX, viewport.x);
            maxY = Mathf.Max(maxY, viewport.y);
        }

        if (!any)
            return false;

        minX = Mathf.Clamp01(minX);
        minY = Mathf.Clamp01(minY);
        maxX = Mathf.Clamp01(maxX);
        maxY = Mathf.Clamp01(maxY);
        if (maxX <= minX || maxY <= minY)
            return false;

        minMaxXy = new Vector4(minX, minY, maxX, maxY);
        return true;
    }

    public static Vector3 ClampSize(Vector3 size) => new(
        Mathf.Max(0.01f, Mathf.Abs(size.x)),
        Mathf.Max(0.01f, Mathf.Abs(size.y)),
        Mathf.Max(0.01f, Mathf.Abs(size.z)));
}

/// <summary>
/// Holds the region the user is editing and seeds sensible defaults from whatever
/// geometry is already in the scene.
/// </summary>
[ExecuteAlways]
public class RegionSelectionManager : MonoBehaviour
{
    public const string GeneratedContentRootName = "GeneratedContent";

    [SerializeField] private RegionSelection currentSelection;

    public RegionSelection CurrentSelection => currentSelection;

    public void SetSelection(RegionSelection selection)
    {
        if (selection == null)
        {
            currentSelection = null;
            return;
        }

        currentSelection = selection.Clone();
        currentSelection.size = RegionSelection.ClampSize(currentSelection.size);
        if (string.IsNullOrWhiteSpace(currentSelection.selectionId))
            currentSelection.selectionId = Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// Picks a default region: a fraction of the generated mesh if one exists, otherwise a
    /// fraction of the scene, otherwise a unit box in front of the camera.
    /// </summary>
    public void ResetToDefault()
    {
        if (TryGetGeneratedContentBounds(out Bounds generated))
        {
            ApplyBounds(generated, 0.25f);
            return;
        }

        if (TryGetSceneGeometryBounds(out Bounds scene))
        {
            ApplyBounds(scene, 0.4f);
            return;
        }

        Camera camera = Camera.main;
        Vector3 origin = camera != null
            ? camera.transform.position + camera.transform.forward * 3f
            : transform.position;

        SetSelection(new RegionSelection
        {
            selectionId = Guid.NewGuid().ToString("N"),
            center = origin,
            size = Vector3.one,
            rotation = camera != null ? camera.transform.rotation : Quaternion.identity
        });
    }

    /// <summary>Centers the selection on <paramref name="bounds"/> at the given coverage fraction.</summary>
    public void ApplyBounds(Bounds bounds, float coverage)
    {
        SetSelection(new RegionSelection
        {
            selectionId = currentSelection?.selectionId ?? Guid.NewGuid().ToString("N"),
            center = bounds.center,
            size = bounds.size * Mathf.Clamp(coverage, 0.01f, 1f),
            rotation = Quaternion.identity
        });
    }

    /// <summary>
    /// Combined renderer bounds of the generated asset: <c>Generated_Mesh*</c> subtrees while
    /// they are active, otherwise the <c>Refined_Mesh*</c> that replaced them.
    /// </summary>
    public static bool TryGetGeneratedContentBounds(out Bounds bounds)
    {
        bounds = default;
        GameObject root = GameObject.Find(GeneratedContentRootName);
        if (root == null)
            return false;

        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        if (!AccumulateSubtreeBounds(renderers, root.transform, "Generated_Mesh", out bounds) &&
            !AccumulateSubtreeBounds(renderers, root.transform, "Refined_Mesh", out bounds))
        {
            return false;
        }

        return bounds.size.sqrMagnitude > 1e-6f;
    }

    /// <summary>
    /// True when the selection covers so much of the asset that the inpaint mask would trace
    /// the whole silhouette. Requires a large span on at least two axes *and* a large volume
    /// share, so thin slabs (a roof: wide in X/Z, thin in Y) are not flagged.
    /// </summary>
    public static bool SelectionSpansMostOfMesh(RegionSelection selection, Bounds meshBounds)
    {
        const float axisRatio = 0.9f;
        const float minVolumeShare = 0.22f;
        const float minAxisShare = 0.42f;

        if (selection == null || meshBounds.size.sqrMagnitude < 1e-8f)
            return false;

        Vector3 selectionSize = selection.GetWorldBounds().size;
        Vector3 meshSize = new(
            Mathf.Max(meshBounds.size.x, 1e-6f),
            Mathf.Max(meshBounds.size.y, 1e-6f),
            Mathf.Max(meshBounds.size.z, 1e-6f));

        Vector3 ratio = new(
            selectionSize.x / meshSize.x,
            selectionSize.y / meshSize.y,
            selectionSize.z / meshSize.z);

        int wideAxes = (ratio.x >= axisRatio ? 1 : 0) + (ratio.y >= axisRatio ? 1 : 0) + (ratio.z >= axisRatio ? 1 : 0);
        float volumeShare = (selectionSize.x * selectionSize.y * selectionSize.z) /
                            (meshSize.x * meshSize.y * meshSize.z);
        float smallestAxisShare = Mathf.Min(ratio.x, Mathf.Min(ratio.y, ratio.z));

        return wideAxes >= 2 && volumeShare >= minVolumeShare && smallestAxisShare >= minAxisShare;
    }

    private bool TryGetSceneGeometryBounds(out Bounds bounds)
    {
        bounds = default;
        bool has = false;

        foreach (Renderer renderer in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            if (!IsCandidateGeometry(renderer))
                continue;

            if (!has)
            {
                bounds = renderer.bounds;
                has = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        return has;
    }

    private bool IsCandidateGeometry(Renderer renderer)
    {
        if (renderer == null || !renderer.enabled)
            return false;

        GameObject go = renderer.gameObject;
        if (go == gameObject || go.transform.IsChildOf(transform))
            return false;
        if ((go.hideFlags & HideFlags.HideInHierarchy) != 0)
            return false;
        if (go.GetComponentInParent<SpatialProxy>() != null)
            return false;

        return renderer.bounds.size.sqrMagnitude > 1e-6f;
    }

    private static bool AccumulateSubtreeBounds(
        Renderer[] renderers, Transform root, string subtreeNamePrefix, out Bounds combined)
    {
        combined = default;
        bool has = false;

        foreach (Renderer renderer in renderers)
        {
            if (renderer == null || !renderer.gameObject.activeInHierarchy)
                continue;

            // Imported GLBs put renderers on grandchildren with glTF node names, so the
            // subtree has to be identified by walking up to the direct child of the root.
            Transform subtreeRoot = FindDirectChild(root, renderer.transform);
            if (subtreeRoot == null || !subtreeRoot.name.StartsWith(subtreeNamePrefix, StringComparison.Ordinal))
                continue;

            if (!has)
            {
                combined = renderer.bounds;
                has = true;
            }
            else
            {
                combined.Encapsulate(renderer.bounds);
            }
        }

        return has;
    }

    public static Transform FindDirectChild(Transform root, Transform descendant)
    {
        Transform current = descendant;
        while (current != null && current.parent != root)
            current = current.parent;
        return current;
    }

    private void OnDrawGizmos()
    {
        if (currentSelection == null)
            return;

        Matrix4x4 previousMatrix = Gizmos.matrix;
        Color previousColor = Gizmos.color;

        Gizmos.matrix = Matrix4x4.TRS(currentSelection.center, currentSelection.rotation, Vector3.one);
        Gizmos.color = new Color(0.2f, 1f, 0.7f, 0.95f);
        Gizmos.DrawWireCube(Vector3.zero, currentSelection.size);

        Gizmos.matrix = previousMatrix;
        Gizmos.color = previousColor;
    }
}
