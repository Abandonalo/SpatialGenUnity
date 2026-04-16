using System;
using UnityEngine;

[ExecuteAlways]
public class RegionSelectionManager : MonoBehaviour
{
    [SerializeField] private RegionSelection currentSelection;
    [SerializeField] private bool isSelecting;

    public RegionSelection CurrentSelection => currentSelection;
    public bool IsSelecting => isSelecting;

    public void BeginSelection()
    {
        isSelecting = true;

        if (currentSelection != null)
            return;

        if (TryInitializeFromSceneGeometry(0.7f))
            return;

        Vector3 origin = transform.position;
        Quaternion rotation = Quaternion.identity;

        Camera sceneCamera = Camera.main;
        if (sceneCamera != null)
        {
            origin = sceneCamera.transform.position + sceneCamera.transform.forward * 3f;
            rotation = sceneCamera.transform.rotation;
        }

        currentSelection = new RegionSelection
        {
            selectionId = Guid.NewGuid().ToString("N"),
            center = origin,
            size = Vector3.one,
            rotation = rotation
        };
    }

    public void UpdateSelection(Vector3 start, Vector3 end)
    {
        Vector3 min = Vector3.Min(start, end);
        Vector3 max = Vector3.Max(start, end);

        currentSelection ??= new RegionSelection();
        currentSelection.selectionId = string.IsNullOrWhiteSpace(currentSelection.selectionId)
            ? Guid.NewGuid().ToString("N")
            : currentSelection.selectionId;
        currentSelection.center = (min + max) * 0.5f;
        currentSelection.size = ClampSize(max - min);
        currentSelection.rotation = Quaternion.identity;
        isSelecting = true;
    }

    public void ConfirmSelection()
    {
        if (currentSelection == null)
            return;

        currentSelection.size = ClampSize(currentSelection.size);
        if (string.IsNullOrWhiteSpace(currentSelection.selectionId))
            currentSelection.selectionId = Guid.NewGuid().ToString("N");

        isSelecting = false;
    }

    public bool TryInitializeFromSceneGeometry(float coverage = 0.7f)
    {
        if (!TryGetSceneGeometryBounds(out Bounds geometryBounds))
            return false;

        ApplyBoundsSelection(geometryBounds, coverage);
        return true;
    }

    public void ApplyBoundsSelection(Bounds geometryBounds, float coverage = 0.7f)
    {
        currentSelection = new RegionSelection
        {
            selectionId = currentSelection != null && !string.IsNullOrWhiteSpace(currentSelection.selectionId)
                ? currentSelection.selectionId
                : Guid.NewGuid().ToString("N"),
            center = geometryBounds.center,
            size = ClampSize(geometryBounds.size * Mathf.Clamp(coverage, 0.01f, 1f)),
            rotation = Quaternion.identity
        };
    }

    public void ClearSelection()
    {
        currentSelection = null;
        isSelecting = false;
    }

    public Bounds GetWorldBounds()
    {
        if (currentSelection == null)
            return new Bounds(transform.position, Vector3.zero);

        return BuildWorldBounds(currentSelection);
    }

    public void SetSelection(RegionSelection selection)
    {
        if (selection == null)
        {
            ClearSelection();
            return;
        }

        currentSelection = CloneSelection(selection);
        currentSelection.size = ClampSize(currentSelection.size);
        if (string.IsNullOrWhiteSpace(currentSelection.selectionId))
            currentSelection.selectionId = Guid.NewGuid().ToString("N");
    }

    public Vector3[] GetWorldCorners()
    {
        return currentSelection == null
            ? Array.Empty<Vector3>()
            : GetWorldCorners(currentSelection);
    }

    private void OnDrawGizmos()
    {
        if (currentSelection == null)
            return;

        Matrix4x4 previous = Gizmos.matrix;
        Color previousColor = Gizmos.color;

        Gizmos.matrix = Matrix4x4.TRS(currentSelection.center, currentSelection.rotation, Vector3.one);
        Gizmos.color = isSelecting
            ? new Color(1f, 0.8f, 0.2f, 0.95f)
            : new Color(0.2f, 1f, 0.7f, 0.95f);
        Gizmos.DrawWireCube(Vector3.zero, currentSelection.size);

        Gizmos.matrix = previous;
        Gizmos.color = previousColor;
    }

    public static Bounds BuildWorldBounds(RegionSelection selection)
    {
        Vector3[] corners = GetWorldCorners(selection);
        if (corners.Length == 0)
            return new Bounds(Vector3.zero, Vector3.zero);

        Bounds bounds = new Bounds(corners[0], Vector3.zero);
        for (int i = 1; i < corners.Length; i++)
            bounds.Encapsulate(corners[i]);

        return bounds;
    }

    public static Vector3[] GetWorldCorners(RegionSelection selection)
    {
        if (selection == null)
            return Array.Empty<Vector3>();

        Vector3 halfExtents = ClampSize(selection.size) * 0.5f;
        Vector3[] localCorners =
        {
            new Vector3(-halfExtents.x, -halfExtents.y, -halfExtents.z),
            new Vector3(halfExtents.x, -halfExtents.y, -halfExtents.z),
            new Vector3(-halfExtents.x, halfExtents.y, -halfExtents.z),
            new Vector3(halfExtents.x, halfExtents.y, -halfExtents.z),
            new Vector3(-halfExtents.x, -halfExtents.y, halfExtents.z),
            new Vector3(halfExtents.x, -halfExtents.y, halfExtents.z),
            new Vector3(-halfExtents.x, halfExtents.y, halfExtents.z),
            new Vector3(halfExtents.x, halfExtents.y, halfExtents.z)
        };

        Vector3[] worldCorners = new Vector3[localCorners.Length];
        for (int i = 0; i < localCorners.Length; i++)
            worldCorners[i] = selection.center + selection.rotation * localCorners[i];

        return worldCorners;
    }

    private static RegionSelection CloneSelection(RegionSelection selection)
    {
        return new RegionSelection
        {
            selectionId = selection.selectionId,
            center = selection.center,
            size = selection.size,
            rotation = selection.rotation
        };
    }

    private static Vector3 ClampSize(Vector3 size)
    {
        return new Vector3(
            Mathf.Max(0.01f, Mathf.Abs(size.x)),
            Mathf.Max(0.01f, Mathf.Abs(size.y)),
            Mathf.Max(0.01f, Mathf.Abs(size.z)));
    }

    private bool TryGetSceneGeometryBounds(out Bounds bounds)
    {
        bounds = default;
        Renderer[] renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.None);
        bool hasBounds = false;

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (!IsCandidateGeometry(renderer))
                continue;

            if (!hasBounds)
            {
                bounds = renderer.bounds;
                hasBounds = true;
                continue;
            }

            bounds.Encapsulate(renderer.bounds);
        }

        return hasBounds;
    }

    private bool IsCandidateGeometry(Renderer renderer)
    {
        if (renderer == null || !renderer.enabled)
            return false;

        GameObject go = renderer.gameObject;
        if (go == null)
            return false;
        if (go == gameObject || go.transform.IsChildOf(transform))
            return false;
        if ((go.hideFlags & HideFlags.HideInHierarchy) != 0)
            return false;
        if (go.GetComponentInParent<RegionSelectionManager>() != null)
            return false;
        if (go.GetComponentInParent<SpatialProxy>() != null)
            return false;

        Bounds candidateBounds = renderer.bounds;
        if (candidateBounds.size.sqrMagnitude <= 1e-6f)
            return false;

        return true;
    }
}

[Serializable]
public class RegionSelection
{
    public string selectionId;
    public Vector3 center;
    public Vector3 size;
    public Quaternion rotation = Quaternion.identity;

    public Bounds ToBounds()
    {
        return new Bounds(center, size);
    }
}
