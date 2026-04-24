using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor-only bridge between <see cref="RefinementController"/> (Runtime asmdef)
/// and <see cref="GenerationControllerEditor.TryInstantiateMeshOutput"/>. The
/// controller cannot call into Editor assemblies directly (circular reference),
/// so it raises a static event that this loader subscribes to.
/// </summary>
[InitializeOnLoad]
public static class RefinementMeshLoader
{
    private const string GeneratedRootName = "GeneratedContent";

    static RefinementMeshLoader()
    {
        RefinementController.RefinedMeshReady -= Handle;
        RefinementController.RefinedMeshReady += Handle;
    }

    private static bool Handle(RefinedMeshContext ctx)
    {
        if (ctx == null || string.IsNullOrWhiteSpace(ctx.meshAbsolutePath))
            return false;

        try
        {
            GameObject root = GameObject.Find(GeneratedRootName);
            if (root == null)
            {
                root = new GameObject(GeneratedRootName);
                Undo.RegisterCreatedObjectUndo(root, "Create GeneratedContent Root");
            }

            ClearPreviousRefinementArtifacts(root);

            if (!GenerationControllerEditor.TryInstantiateMeshOutput(ctx.meshAbsolutePath, root, out GameObject meshObject))
                return false;

            meshObject.name = $"Refined_Mesh_{(string.IsNullOrWhiteSpace(ctx.requestId) ? "0" : ctx.requestId)}";

            // #region agent log
            Bounds rawBounds = MeasureBounds(meshObject);
            // #endregion

            // Multi-view refinement lifts the WHOLE generated scene into a new
            // mesh (only the selection region's pixels were changed inside the
            // inpaint step). Align the refined mesh to the full scene bounds
            // of the original Generated_Mesh* so the result visually replaces
            // the original instead of being squeezed into the selection.
            Bounds sceneBounds = ComputeOriginalSceneBounds(root);
            Vector3 targetSize;
            Vector3 targetCenter;
            Quaternion targetRotation;
            bool usedSceneBounds;
            if (sceneBounds.size.sqrMagnitude > 1e-6f)
            {
                targetSize = sceneBounds.size;
                targetCenter = sceneBounds.center;
                targetRotation = Quaternion.identity;
                usedSceneBounds = true;
            }
            else
            {
                targetSize = ctx.selectionSize;
                targetCenter = ctx.selectionCenter;
                targetRotation = ctx.selectionRotation;
                usedSceneBounds = false;
            }

            bool hasTarget = targetSize.sqrMagnitude > 1e-6f;
            if (hasTarget)
            {
                meshObject.transform.rotation = targetRotation;
                FitObjectInsideTargetSize(meshObject, targetSize);
                GenerationControllerEditor.AlignObjectBoundsCenterToTarget(meshObject, targetCenter);
            }

            // Since the refined mesh now replaces the whole scene, hide the
            // original Generated_Mesh* instead of showing both stacked. The
            // originals are kept in the hierarchy (deactivated) so the user
            // can restore them by re-enabling if needed.
            if (usedSceneBounds)
                DeactivateOriginalGeneratedMeshes(root);

            // #region agent log
            Bounds finalBounds = MeasureBounds(meshObject);
            DbgLogMeshPlacement(ctx, rawBounds, targetSize, finalBounds, sceneBounds, usedSceneBounds);
            // #endregion

            Undo.RegisterCreatedObjectUndo(meshObject, "Create Refined Mesh");

            Debug.Log($"Spatial Generation: Loaded refined mesh for request '{ctx.requestId}' (aligned={hasTarget}).");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Spatial Generation: RefinementMeshLoader failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Uniformly scales <paramref name="target"/> so that its combined
    /// renderer bounds fit inside <paramref name="targetSize"/> (contain, not
    /// cover). Uses the MIN axis ratio so the refined geometry stays within
    /// the selection region instead of overshooting along its tallest axis
    /// (the bug the logs caught on the previous run).
    /// </summary>
    private static void FitObjectInsideTargetSize(GameObject target, Vector3 targetSize)
    {
        if (target == null)
            return;
        Renderer[] renderers = target.GetComponentsInChildren<Renderer>(true);
        if (renderers == null || renderers.Length == 0)
            return;
        Bounds combined = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            combined.Encapsulate(renderers[i].bounds);

        const float min = 1e-4f;
        Vector3 cur = combined.size;
        Vector3 safeCur = new(Mathf.Max(min, cur.x), Mathf.Max(min, cur.y), Mathf.Max(min, cur.z));
        Vector3 safeTgt = new(Mathf.Max(min, targetSize.x), Mathf.Max(min, targetSize.y), Mathf.Max(min, targetSize.z));
        float ratio = Mathf.Min(safeTgt.x / safeCur.x, Mathf.Min(safeTgt.y / safeCur.y, safeTgt.z / safeCur.z));
        target.transform.localScale *= ratio;
    }

    // Combined renderer bounds of the original Generated_Mesh* under
    // <paramref name="root"/>, ignoring prior Refined_* artifacts. Used as
    // the placement target for the new refined mesh so the whole scene is
    // replaced instead of a selection-sized patch.
    private static Bounds ComputeOriginalSceneBounds(GameObject root)
    {
        if (root == null) return new Bounds(Vector3.zero, Vector3.zero);
        Renderer[] rs = root.GetComponentsInChildren<Renderer>(true);
        Bounds b = new Bounds();
        bool has = false;
        for (int i = 0; i < rs.Length; i++)
        {
            Renderer r = rs[i];
            if (r == null) continue;
            if (!r.gameObject.activeInHierarchy) continue;
            string n = r.gameObject.name ?? string.Empty;
            if (n.StartsWith("Refined_", StringComparison.Ordinal)) continue;
            if (!has) { b = r.bounds; has = true; }
            else b.Encapsulate(r.bounds);
        }
        return has ? b : new Bounds(Vector3.zero, Vector3.zero);
    }

    // Disable the original Generated_Mesh*/Generated_Image so the refined
    // whole-scene mesh is visible on its own. Deactivation is reversible -
    // the user can re-enable the originals in the hierarchy if they want.
    private static void DeactivateOriginalGeneratedMeshes(GameObject root)
    {
        if (root == null) return;
        for (int i = 0; i < root.transform.childCount; i++)
        {
            Transform child = root.transform.GetChild(i);
            if (child == null) continue;
            string n = child.name ?? string.Empty;
            if (n.StartsWith("Refined_", StringComparison.Ordinal)) continue;
            if (n.StartsWith("Generated_", StringComparison.Ordinal))
            {
                Undo.RecordObject(child.gameObject, "Deactivate Original Generated Mesh");
                child.gameObject.SetActive(false);
            }
        }
    }

    // #region agent log
    private const string DbgLogPath = "/Users/alo/SpatialGenUnity/.cursor/debug-f3f4e4.log";
    private static Bounds MeasureBounds(GameObject go)
    {
        if (go == null) return new Bounds(Vector3.zero, Vector3.zero);
        Renderer[] rs = go.GetComponentsInChildren<Renderer>(true);
        if (rs == null || rs.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
        Bounds b = rs[0].bounds;
        for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
        return b;
    }
    private static void DbgLogMeshPlacement(RefinedMeshContext ctx, Bounds raw, Vector3 targetSize, Bounds finalB, Bounds sceneB, bool usedScene)
    {
        try
        {
            string line = "{\"sessionId\":\"f3f4e4\",\"runId\":\"post-fix\",\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() +
                ",\"hypothesisId\":\"H2\",\"location\":\"RefinementMeshLoader.cs:Handle\"" +
                ",\"message\":\"refined mesh placement\"" +
                ",\"data\":{\"requestId\":\"" + (ctx.requestId ?? "") + "\"" +
                ",\"rawCenter\":{\"x\":" + raw.center.x.ToString("0.###") + ",\"y\":" + raw.center.y.ToString("0.###") + ",\"z\":" + raw.center.z.ToString("0.###") + "}" +
                ",\"rawSize\":{\"x\":" + raw.size.x.ToString("0.###") + ",\"y\":" + raw.size.y.ToString("0.###") + ",\"z\":" + raw.size.z.ToString("0.###") + "}" +
                ",\"targetSize\":{\"x\":" + targetSize.x.ToString("0.###") + ",\"y\":" + targetSize.y.ToString("0.###") + ",\"z\":" + targetSize.z.ToString("0.###") + "}" +
                ",\"finalSize\":{\"x\":" + finalB.size.x.ToString("0.###") + ",\"y\":" + finalB.size.y.ToString("0.###") + ",\"z\":" + finalB.size.z.ToString("0.###") + "}" +
                ",\"sceneSize\":{\"x\":" + sceneB.size.x.ToString("0.###") + ",\"y\":" + sceneB.size.y.ToString("0.###") + ",\"z\":" + sceneB.size.z.ToString("0.###") + "}" +
                ",\"usedSceneBounds\":" + (usedScene ? "true" : "false") +
                ",\"selSize\":{\"x\":" + ctx.selectionSize.x.ToString("0.###") + ",\"y\":" + ctx.selectionSize.y.ToString("0.###") + ",\"z\":" + ctx.selectionSize.z.ToString("0.###") + "}" +
                "}}\n";
            File.AppendAllText(DbgLogPath, line);
        }
        catch { }
    }
    // #endregion

    private static void ClearPreviousRefinementArtifacts(GameObject root)
    {
        if (root == null)
            return;

        // Remove only artifacts produced by previous refinement runs. The
        // originally generated scene ("Generated_Mesh*", "Generated_Image")
        // is left intact so the refined mesh augments the scene instead of
        // replacing everything the user has produced so far.
        for (int i = root.transform.childCount - 1; i >= 0; i--)
        {
            Transform child = root.transform.GetChild(i);
            if (child == null)
                continue;
            string n = child.name ?? string.Empty;
            if (n.StartsWith("Refined_Mesh", StringComparison.Ordinal)
                || n == "RefinementPreview")
            {
                Undo.DestroyObjectImmediate(child.gameObject);
            }
        }
    }
}
