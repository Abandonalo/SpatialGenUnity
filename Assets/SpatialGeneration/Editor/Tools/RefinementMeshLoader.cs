using System;
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

            if (!GenerationControllerEditor.TryInstantiateMeshOutput(
                    ctx.meshAbsolutePath,
                    root,
                    out GameObject meshObject,
                    forceVertexColorMaterials: true))
                return false;

            meshObject.name = $"Refined_Mesh_{(string.IsNullOrWhiteSpace(ctx.requestId) ? "0" : ctx.requestId)}";

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
                targetRotation = ResolveOriginalGeneratedMeshRotation(root);
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
                FitObjectToTargetSizeNonUniform(meshObject, targetSize);
                GenerationControllerEditor.AlignObjectBoundsCenterToTarget(meshObject, targetCenter);
            }

            // Since the refined mesh now replaces the whole scene, hide the
            // original Generated_Mesh* instead of showing both stacked. The
            // originals are kept in the hierarchy (deactivated) so the user
            // can restore them by re-enabling if needed.
            if (usedSceneBounds)
                DeactivateOriginalGeneratedMeshes(root);

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
    /// Scales <paramref name="target"/> per-axis so its combined renderer
    /// bounds match <paramref name="targetSize"/> exactly on every axis.
    /// TripoSR returns a near-unit-cube mesh regardless of the source
    /// image's true proportions, so any uniform scale either fits-inside
    /// (much smaller than the original scene) or covers (overshoots two
    /// axes). Per-axis scaling is the only way to match the original
    /// scene's bounding box on all three axes simultaneously, at the cost
    /// of slight non-uniform deformation - which is acceptable here
    /// because the refined mesh is meant to *replace* the original
    /// generated scene, not preserve TripoSR's internal proportions.
    /// </summary>
    private static void FitObjectToTargetSizeNonUniform(GameObject target, Vector3 targetSize)
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
        Vector3 axisScale = new(safeTgt.x / safeCur.x, safeTgt.y / safeCur.y, safeTgt.z / safeCur.z);
        target.transform.localScale = Vector3.Scale(target.transform.localScale, axisScale);
    }

    // Combined renderer bounds of the original Generated_Mesh* under
    // <paramref name="root"/>. Walks each renderer up to the direct child
    // of <paramref name="root"/> and only includes descendants of nodes
    // named "Generated_Mesh*". Filtering on the renderer's immediate
    // GameObject name (the previous approach) didn't work because
    // imported GLBs have renderers on grandchildren with internal glTF
    // node names like "Mesh_001", so "Refined_*" never matched and prior
    // refined-mesh renderers leaked into the bounds.
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
            Transform rootChild = FindRootChild(root.transform, r.transform);
            if (rootChild == null) continue;
            string n = rootChild.gameObject.name ?? string.Empty;
            if (!n.StartsWith("Generated_Mesh", StringComparison.Ordinal)) continue;
            if (!has) { b = r.bounds; has = true; }
            else b.Encapsulate(r.bounds);
        }
        return has ? b : new Bounds(Vector3.zero, Vector3.zero);
    }

    private static Quaternion ResolveOriginalGeneratedMeshRotation(GameObject root)
    {
        if (root == null)
            return Quaternion.identity;

        GeneratedMeshMetadata[] metas = root.GetComponentsInChildren<GeneratedMeshMetadata>(true);
        for (int i = 0; i < metas.Length; i++)
        {
            GeneratedMeshMetadata meta = metas[i];
            if (meta == null)
                continue;

            Transform rootChild = FindRootChild(root.transform, meta.transform);
            if (rootChild == null)
                continue;

            string n = rootChild.gameObject.name ?? string.Empty;
            if (!n.StartsWith("Generated_Mesh", StringComparison.Ordinal))
                continue;

            return GenerationControllerEditor.GetGeneratedMeshRotationForProxy(meta.proxyRotation);
        }

        return Quaternion.identity;
    }

    // Walks up from <paramref name="descendant"/> until it hits a direct
    // child of <paramref name="root"/>. Returns null if the descendant is
    // not under root.
    private static Transform FindRootChild(Transform root, Transform descendant)
    {
        Transform t = descendant;
        while (t != null && t.parent != root)
        {
            t = t.parent;
        }
        return t;
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
