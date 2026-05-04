using System;
using System.Globalization;
using System.IO;
using UnityEngine;

[ExecuteAlways]
public class RegionSelectionManager : MonoBehaviour
{
    private const string GeneratedContentRootName = "GeneratedContent";

    [SerializeField] private RegionSelection currentSelection;
    [SerializeField] private bool isSelecting;

    public RegionSelection CurrentSelection => currentSelection;
    public bool IsSelecting => isSelecting;

    public void BeginSelection()
    {
        isSelecting = true;

        if (currentSelection != null)
            return;

        if (TryInitializeFromGeneratedMeshBounds())
            return;
        if (TryInitializeFromSceneGeometry(0.4f))
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

    /// <summary>
    /// Fits the selection to renderer bounds under <c>Generated_Mesh*</c> children of
    /// <c>GeneratedContent</c> (same filtering as refinement mesh replacement).
    /// Uses a smaller default coverage than scene-wide init so inpaint masks target
    /// a sub-region instead of the full asset silhouette.
    /// </summary>
    public bool TryInitializeFromGeneratedMeshBounds(float coverage = 0.25f)
    {
        if (!TryGetPrimaryGeneratedContentMeshBounds(out Bounds meshBounds))
            return false;

        ApplyBoundsSelection(meshBounds, coverage);
        return true;
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

    /// <summary>
    /// World AABB combining active <see cref="GeneratedContentRootName"/> mesh subtrees.
    /// </summary>
    public static bool TryGetGeneratedContentMeshBounds(out Bounds bounds)
    {
        return TryGetPrimaryGeneratedContentMeshBounds(out bounds);
    }

    /// <summary>
    /// For refinement masks only: if the selection is axis-aligned, clamp its extents to
    /// <paramref name="meshWorldAabb"/> so slack outside the generated asset is not inpainted.
    /// <para />
    /// When the selection is <b>rotated</b>, the selection is returned unchanged: replacing it with the
    /// world-axis-aligned hull of an OBB (the old behavior) inflates thin roof slabs to a box that
    /// covers most of the mesh and produces a full-object mask.
    /// </summary>
    public static RegionSelection BuildMaskSelectionClippedToGeneratedMesh(
        RegionSelection source,
        Bounds meshWorldAabb)
    {
        if (source == null)
            return null;

        if (meshWorldAabb.size.sqrMagnitude < 1e-8f)
            return CloneSelection(source);

        if (Quaternion.Angle(source.rotation, Quaternion.identity) < 0.05f)
        {
            Vector3 omin = source.center - source.size * 0.5f;
            Vector3 omax = source.center + source.size * 0.5f;
            Vector3 nmin = Vector3.Max(omin, meshWorldAabb.min);
            Vector3 nmax = Vector3.Min(omax, meshWorldAabb.max);
            if (nmin.x >= nmax.x || nmin.y >= nmax.y || nmin.z >= nmax.z)
                return CloneSelection(source);

            return new RegionSelection
            {
                selectionId = source.selectionId,
                center = (nmin + nmax) * 0.5f,
                size = ClampSize(nmax - nmin),
                rotation = Quaternion.identity
            };
        }

        // Rotated OBB: do NOT replace with the world-axis-aligned bounds of the OBB (BuildWorldBounds).
        // The hull AABB of a thin, tilted roof slab can inflate to the full house footprint and floor
        // height, which makes the mask shader paint the entire silhouette white — exactly the bug users
        // see in mask.png. Air outside the mesh still draws black (no geometry). Preserve orientation/size.
        return CloneSelection(source);
    }

    /// <summary>
    /// True when the selection's world AABB is a large fraction of the mesh AABB in extent
    /// (inpaint mask will trace most of the silhouette). Combines (1) at least two edges ≥
    /// <paramref name="axisRatio"/>, (2) volume ≥ <paramref name="volumeSpanMin"/>, and
    /// (3) every edge ≥ <paramref name="minAxisSpanMin"/> so slabs (e.g. roof: big X/Z, small Y)
    /// are not flagged when volume barely clears the bar (e.g. 0.226 vs 0.22).
    /// </summary>
    public static bool SelectionWorldAabbSpansMostOfMesh(
        RegionSelection selection,
        Bounds meshWorldAabb,
        float axisRatio = 0.9f,
        float volumeSpanMin = 0.22f,
        float minAxisSpanMin = 0.42f)
    {
        if (selection == null || meshWorldAabb.size.sqrMagnitude < 1e-8f)
            return false;

        Bounds w = BuildWorldBounds(selection);
        float mx = Mathf.Max(meshWorldAabb.size.x, 1e-6f);
        float my = Mathf.Max(meshWorldAabb.size.y, 1e-6f);
        float mz = Mathf.Max(meshWorldAabb.size.z, 1e-6f);
        float rx = w.size.x / mx;
        float ry = w.size.y / my;
        float rz = w.size.z / mz;
        int big = (rx >= axisRatio ? 1 : 0) + (ry >= axisRatio ? 1 : 0) + (rz >= axisRatio ? 1 : 0);

        float volMesh = mx * my * mz;
        float volSel = w.size.x * w.size.y * w.size.z;
        float volumeRatio = volMesh > 1e-18f ? volSel / volMesh : 0f;
        float minR = Mathf.Min(rx, Mathf.Min(ry, rz));

        bool spansMost = big >= 2
            && volumeRatio >= volumeSpanMin
            && minR >= minAxisSpanMin;

        // #region agent log
#if UNITY_EDITOR
        try
        {
            const string path = "/Users/alo/SpatialGenUnity/.cursor/debug-58c452.log";
            string ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            File.AppendAllText(
                path,
                "{\"sessionId\":\"58c452\",\"runId\":\"spanHeuristic\",\"hypothesisId\":\"H_VOL\",\"location\":\"RegionSelectionManager.SelectionWorldAabbSpansMostOfMesh\",\"message\":\"axis vs volume span check\",\"data\":{"
                    + "\"rx\":" + rx.ToString(CultureInfo.InvariantCulture)
                    + ",\"ry\":" + ry.ToString(CultureInfo.InvariantCulture)
                    + ",\"rz\":" + rz.ToString(CultureInfo.InvariantCulture)
                    + ",\"minR\":" + minR.ToString(CultureInfo.InvariantCulture)
                    + ",\"bigAxes\":" + big.ToString(CultureInfo.InvariantCulture)
                    + ",\"volumeRatio\":" + volumeRatio.ToString(CultureInfo.InvariantCulture)
                    + ",\"volumeSpanMin\":" + volumeSpanMin.ToString(CultureInfo.InvariantCulture)
                    + ",\"minAxisSpanMin\":" + minAxisSpanMin.ToString(CultureInfo.InvariantCulture)
                    + ",\"spansMost\":" + (spansMost ? "true" : "false")
                    + "},\"timestamp\":" + ts + "}\n");
        }
        catch
        {
        }
#endif
        // #endregion

        return spansMost;
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

    /// <summary>
    /// Active <c>Generated_Mesh*</c> subtrees first, then active <c>Refined_Mesh*</c>
    /// after originals are deactivated (post-refinement).
    /// </summary>
    private static bool TryGetPrimaryGeneratedContentMeshBounds(out Bounds bounds)
    {
        bounds = default;
        GameObject root = GameObject.Find(GeneratedContentRootName);
        if (root == null)
            return false;

        Renderer[] rs = root.GetComponentsInChildren<Renderer>(true);

        if (AccumulateSubtreeBounds(rs, root.transform, IsGeneratedMeshSubtreeRoot, out Bounds fromGen))
            bounds = fromGen;
        else if (AccumulateSubtreeBounds(rs, root.transform, IsRefinedMeshSubtreeRoot, out Bounds fromRef))
            bounds = fromRef;
        else
            return false;

        return bounds.size.sqrMagnitude > 1e-6f;
    }

    private static bool IsGeneratedMeshSubtreeRoot(Transform rootChild)
    {
        string n = rootChild.gameObject.name ?? string.Empty;
        return n.StartsWith("Generated_Mesh", StringComparison.Ordinal);
    }

    private static bool IsRefinedMeshSubtreeRoot(Transform rootChild)
    {
        string n = rootChild.gameObject.name ?? string.Empty;
        return n.StartsWith("Refined_Mesh", StringComparison.Ordinal);
    }

    private static bool AccumulateSubtreeBounds(
        Renderer[] renderers,
        Transform generatedRoot,
        Func<Transform, bool> subtreeRootPredicate,
        out Bounds combined)
    {
        combined = default;
        bool has = false;
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null || !r.gameObject.activeInHierarchy)
                continue;

            Transform rootChild = FindDirectChildOfGeneratedRoot(generatedRoot, r.transform);
            if (rootChild == null || !subtreeRootPredicate(rootChild))
                continue;

            if (!has)
            {
                combined = r.bounds;
                has = true;
            }
            else
                combined.Encapsulate(r.bounds);
        }

        return has;
    }

    private static Transform FindDirectChildOfGeneratedRoot(Transform generatedRoot, Transform descendant)
    {
        Transform t = descendant;
        while (t != null && t.parent != generatedRoot)
            t = t.parent;
        return t;
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
