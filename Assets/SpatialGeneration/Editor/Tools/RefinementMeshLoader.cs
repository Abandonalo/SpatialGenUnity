using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using SpatialGeneration.Generation;
using SpatialGeneration.Generation.Refinement;

/// <summary>
/// Editor-side bridge for <see cref="RefinementController.RefinedMeshReady"/>: splices a
/// refined region mesh into the scene.
///
/// The splice is what makes a local edit local. Rather than swapping the whole asset for a
/// fresh reconstruction, the original mesh is cut against the user's selection box: triangles
/// outside the box are carried over vertex-for-vertex, and only the box's contents are
/// replaced. Anything the user did not select is therefore byte-identical afterwards, which
/// is what makes "did this edit stay local?" a measurable question rather than a judgement call.
/// </summary>
[InitializeOnLoad]
public static class RefinementMeshLoader
{
    private const string PreservedPrefix = "Preserved_";
    private const string RegionChildName = "RefinedRegion";

    static RefinementMeshLoader()
    {
        RefinementController.RefinedMeshReady -= Apply;
        RefinementController.RefinedMeshReady += Apply;
    }

    private static bool Apply(RefinedMeshContext context)
    {
        if (context == null || context.Region == null || string.IsNullOrWhiteSpace(context.meshAbsolutePath))
            return false;

        try
        {
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Apply Region Refinement");

            GameObject root = GenerationRunner.EnsureGeneratedRoot();
            List<Transform> sources = CollectActiveGeneratedSubtrees(root.transform);
            if (sources.Count == 0)
            {
                Debug.LogWarning("Spatial Generation: nothing under GeneratedContent to refine.");
                return false;
            }

            var refined = new GameObject($"Refined_Mesh_{Sanitize(context.requestId)}");
            refined.transform.SetParent(root.transform, worldPositionStays: false);
            Undo.RegisterCreatedObjectUndo(refined, "Create Refined Mesh");

            if (!MeshImporter.TryInstantiate(
                    context.meshAbsolutePath, refined.transform, preferVertexColors: true,
                    out GameObject region, out string regionAssetPath))
            {
                Undo.DestroyObjectImmediate(refined);
                return false;
            }

            region.name = RegionChildName;
            PlaceInRegion(region, context.Region);

            int preservedTriangles = 0;
            int replacedTriangles = 0;
            foreach (Transform source in sources)
            {
                GameObject preserved = BuildPreservedCopy(source, context.Region, regionAssetPath, out int kept, out int removed);
                preservedTriangles += kept;
                replacedTriangles += removed;

                if (preserved == null)
                    continue;

                preserved.transform.SetParent(refined.transform, worldPositionStays: true);
            }

            foreach (Transform source in sources)
            {
                Undo.RecordObject(source.gameObject, "Hide Refined Source");
                source.gameObject.SetActive(false);
            }

            Undo.CollapseUndoOperations(group);

            Debug.Log(
                $"Spatial Generation: refinement '{context.requestId}' replaced {replacedTriangles} triangles " +
                $"inside the region and preserved {preservedTriangles} outside it.");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Spatial Generation: could not apply the refined mesh. {ex.Message}\n{ex.StackTrace}");
            return false;
        }
    }

    /// <summary>
    /// Clones <paramref name="source"/> and rebuilds each of its meshes without the triangles
    /// that fall inside <paramref name="region"/>. Transforms and materials are untouched, so
    /// the surviving geometry renders exactly as before.
    /// </summary>
    private static GameObject BuildPreservedCopy(
        Transform source,
        RegionSelection region,
        string meshAssetFolderSibling,
        out int keptTriangles,
        out int removedTriangles)
    {
        keptTriangles = 0;
        removedTriangles = 0;

        GameObject clone = UnityEngine.Object.Instantiate(source.gameObject);
        clone.name = $"{PreservedPrefix}{source.name}";
        clone.transform.SetPositionAndRotation(source.position, source.rotation);
        clone.transform.localScale = source.lossyScale;
        clone.SetActive(true);

        // Metadata describes the source asset, not this derived copy.
        foreach (GeneratedMeshMetadata metadata in clone.GetComponentsInChildren<GeneratedMeshMetadata>(true))
            UnityEngine.Object.DestroyImmediate(metadata);

        var emptied = new List<GameObject>();
        foreach (MeshFilter filter in clone.GetComponentsInChildren<MeshFilter>(true))
        {
            Mesh original = filter.sharedMesh;
            if (original == null)
                continue;

            Mesh outside = MeshRegionSplitter.BuildOutsideMesh(
                original, filter.transform.localToWorldMatrix, region, out int removed);

            removedTriangles += removed;

            if (removed == 0)
            {
                // Untouched by the region: keep the imported mesh as-is.
                keptTriangles += CountTriangles(original);
                continue;
            }

            if (outside == null)
            {
                // Entirely inside the region; the refined mesh takes over here.
                emptied.Add(filter.gameObject);
                continue;
            }

            filter.sharedMesh = MeshImporter.PersistMesh(outside, meshAssetFolderSibling, $"{original.name}_Preserved");
            keptTriangles += CountTriangles(filter.sharedMesh);
        }

        foreach (GameObject go in emptied)
        {
            if (go == clone)
            {
                UnityEngine.Object.DestroyImmediate(clone);
                return null;
            }

            UnityEngine.Object.DestroyImmediate(go);
        }

        return clone.GetComponentInChildren<Renderer>(true) != null
            ? clone
            : DestroyAndReturnNull(clone);
    }

    /// <summary>
    /// Seats the lifted mesh in the selection box: aligned to the box's rotation, scaled
    /// uniformly to fit inside it, and centred on it.
    ///
    /// The two axes across the reconstruction view are correct by construction because the
    /// backend cropped the refined image to the region's footprint. Depth along the view axis
    /// is the reconstruction's estimate, so it is fitted rather than trusted.
    /// </summary>
    private static void PlaceInRegion(GameObject region, RegionSelection selection)
    {
        Quaternion levelling = MeshAlignment.Level(region);
        region.transform.rotation = selection.rotation * levelling;

        // Always fill the region, regardless of the generation-side preference: the refined
        // mesh has to occupy exactly the volume that was cut out of the original, or the
        // splice leaves a visible gap where the old geometry used to be.
        MeshFitting.FitToVolume(
            region,
            RegionSelection.ClampSize(selection.size),
            selection.center,
            preserveProportions: false);
    }

    /// <summary>Direct children of GeneratedContent that currently show the asset.</summary>
    private static List<Transform> CollectActiveGeneratedSubtrees(Transform root)
    {
        var sources = new List<Transform>();
        for (int i = 0; i < root.childCount; i++)
        {
            Transform child = root.GetChild(i);
            if (child == null || !child.gameObject.activeSelf)
                continue;

            string name = child.name;
            bool isGenerated = name.StartsWith("Generated_Mesh", StringComparison.Ordinal);
            bool isRefined = name.StartsWith("Refined_Mesh", StringComparison.Ordinal);
            if (isGenerated || isRefined)
                sources.Add(child);
        }

        return sources;
    }

    private static int CountTriangles(Mesh mesh)
    {
        if (mesh == null)
            return 0;

        int count = 0;
        for (int i = 0; i < mesh.subMeshCount; i++)
            count += (int)(mesh.GetIndexCount(i) / 3);
        return count;
    }

    private static GameObject DestroyAndReturnNull(GameObject go)
    {
        UnityEngine.Object.DestroyImmediate(go);
        return null;
    }

    private static string Sanitize(string value) =>
        string.IsNullOrWhiteSpace(value) ? "0" : value.Trim().Replace(' ', '_');
}
