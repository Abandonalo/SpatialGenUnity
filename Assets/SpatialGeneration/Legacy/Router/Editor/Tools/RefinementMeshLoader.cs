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
    private const float ReplacementOverscan = 1.02f;
    private const float TransitionDepthRatio = 0.05f;

    static RefinementMeshLoader()
    {
        RefinementController.RefinedMeshReady -= Apply;
        RefinementController.RefinedMeshReady += Apply;
    }

    private static bool Apply(RefinedMeshContext context)
    {
        if (context == null || context.Region == null || string.IsNullOrWhiteSpace(context.meshAbsolutePath))
            return false;

        GameObject refined = null;
        RefinementApplyTransaction transaction = null;
        int group = -1;
        try
        {
            Undo.IncrementCurrentGroup();
            group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Apply Region Refinement");

            GameObject root = GenerationRunner.EnsureGeneratedRoot();
            List<Transform> sources = CollectActiveGeneratedSubtrees(root.transform);
            if (sources.Count == 0)
            {
                Debug.LogWarning("Spatial Generation: nothing under GeneratedContent to refine.");
                return false;
            }

            refined = new GameObject($"Refined_Mesh_{Sanitize(context.requestId)}");
            refined.transform.SetParent(root.transform, worldPositionStays: false);
            Undo.RegisterCreatedObjectUndo(refined, "Create Refined Mesh");
            transaction = new RefinementApplyTransaction(sources, refined, group);

            if (!MeshImporter.TryInstantiate(
                    context.meshAbsolutePath, refined.transform, preferVertexColors: true,
                    out GameObject region, out string regionAssetPath))
                throw new InvalidOperationException(
                    $"Unity could not import the replacement mesh at '{context.meshAbsolutePath}'.");

            region.name = RegionChildName;

            int preservedTriangles = 0;
            int replacedTriangles = 0;
            bool sourceHasOpenBoundary = false;
            var sourceLoops = new List<RegionBoundaryLoop>();
            var removedGeometry = new SelectionSpaceBounds(context.Region);
            foreach (Transform source in sources)
            {
                GameObject preserved = BuildPreservedCopy(
                    source, context.Region, regionAssetPath,
                    out int kept, out int removed, out bool openBoundary,
                    sourceLoops, removedGeometry);
                preservedTriangles += kept;
                replacedTriangles += removed;
                sourceHasOpenBoundary |= openBoundary;

                if (preserved == null)
                    continue;

                preserved.transform.SetParent(refined.transform, worldPositionStays: true);
            }

            if (replacedTriangles == 0)
                throw new InvalidOperationException("The selection does not intersect any source surface.");
            if (sourceHasOpenBoundary || sourceLoops.Count == 0)
                throw new InvalidOperationException(
                    "The exact source cut did not form closed boundary loops; the original was left unchanged.");
            if (!removedGeometry.IsValid)
                throw new InvalidOperationException(
                    "The selected source geometry could not be measured; the original was left unchanged.");

            Vector3 fittedSourceSize = PlaceLikeRemovedGeometry(
                region, context.Region, removedGeometry, context.LifterUsed);

            var seamFaces = new HashSet<RegionBoundaryFace>();
            foreach (RegionBoundaryLoop loop in sourceLoops)
                seamFaces.UnionWith(loop.faces);
            RegionSelection innerRegion = BuildTransitionRegion(
                context.Region, removedGeometry.Size, seamFaces);
            var replacementLoops = new List<RegionBoundaryLoop>();
            if (!ClipReplacement(
                    region, innerRegion, seamFaces, regionAssetPath, replacementLoops, out string clipError))
                throw new InvalidOperationException(clipError);

            int colouredVertices = MultiViewVertexColorProjector.Apply(region, context.Views, regionAssetPath);

            if (!MeshSeamWelder.TryBuildTransition(
                    sourceLoops, replacementLoops, context.Region, refined.transform,
                    out Mesh transitionMesh, out string weldError))
                throw new InvalidOperationException($"Boundary welding failed: {weldError}");

            GameObject transition = CreateTransitionObject(refined.transform, transitionMesh, regionAssetPath);
            colouredVertices += MultiViewVertexColorProjector.Apply(transition, context.Views, regionAssetPath);

            if (!TryCombineComposite(refined, regionAssetPath, out string combineError))
                throw new InvalidOperationException($"Composite validation failed: {combineError}");

            transaction.Commit();

            Debug.Log(
                $"Spatial Generation: refinement '{context.requestId}' replaced {replacedTriangles} triangles " +
                $"inside the region, preserved {preservedTriangles} outside it, and projected " +
                $"{colouredVertices} vertices from four views (lifter: {context.LifterUsed}). " +
                $"The replacement was fitted to source geometry {fittedSourceSize:F3}, not " +
                $"selection box {RegionSelection.ClampSize(context.Region.size):F3}.");
            return true;
        }
        catch (Exception ex)
        {
            if (transaction != null)
                transaction.Rollback();
            else if (group >= 0)
                Undo.RevertAllDownToGroup(group);
            else if (refined != null)
                UnityEngine.Object.DestroyImmediate(refined);
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
        out int removedTriangles,
        out bool hasOpenBoundary,
        List<RegionBoundaryLoop> boundaryLoops,
        SelectionSpaceBounds removedGeometry)
    {
        keptTriangles = 0;
        removedTriangles = 0;
        hasOpenBoundary = false;

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

            MeshCutResult cut = MeshRegionSplitter.Cut(
                original, filter.transform.localToWorldMatrix, region);
            int removed = cut.removedTriangles;

            removedTriangles += removed;
            hasOpenBoundary |= cut.hasOpenBoundary;
            boundaryLoops.AddRange(cut.boundaryLoops);

            if (cut.insideMesh != null)
            {
                removedGeometry?.Encapsulate(cut.insideMesh, filter.transform.localToWorldMatrix);
                UnityEngine.Object.DestroyImmediate(cut.insideMesh);
            }

            if (removed == 0)
            {
                // Untouched by the region: keep the imported mesh as-is.
                keptTriangles += CountTriangles(original);
                continue;
            }

            if (cut.outsideMesh == null)
            {
                // Entirely inside the region; the refined mesh takes over here.
                emptied.Add(filter.gameObject);
                continue;
            }

            filter.sharedMesh = MeshImporter.PersistMesh(
                cut.outsideMesh, meshAssetFolderSibling, $"{original.name}_Preserved");
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
    /// Seats the lifted mesh on the source geometry removed by the exact OBB cut.
    ///
    /// Reconstruction models return a normalised object with arbitrary dimensions. All three
    /// axes are therefore fitted to the removed source bounds; the selection itself determines
    /// only where clipping is allowed.
    /// </summary>
    private static Vector3 PlaceLikeRemovedGeometry(
        GameObject region,
        RegionSelection selection,
        SelectionSpaceBounds removedGeometry,
        string lifterUsed)
    {
        // Hunyuan's GLB is emitted in canonical glTF Y-up coordinates. Re-running the
        // normal-cluster levelling heuristic on a sloped roof can tilt an already-correct
        // reconstruction, so it remains only for the legacy single-view TripoSR fallback.
        if (!string.Equals(lifterUsed, "hunyuan3d_2mv", StringComparison.OrdinalIgnoreCase))
            MeshAlignment.Level(region);
        region.transform.rotation = selection.rotation;

        // The OBB is an editing boundary, not a target asset size. Fitting to it made the
        // result grow whenever the user left padding around the selected surface. The exact
        // inside fragments describe both the original dimensions and its centre; two percent
        // overscan is retained only so clipping can form a transition just inside the cut.
        Vector3 targetSize = RegionSelection.ClampSize(removedGeometry.Size) * ReplacementOverscan;
        MeshFitting.FitToVolume(
            region,
            targetSize,
            removedGeometry.WorldCenter,
            preserveProportions: false);
        return targetSize;
    }

    private static RegionSelection BuildTransitionRegion(
        RegionSelection selection,
        Vector3 removedSize,
        ISet<RegionBoundaryFace> seamFaces)
    {
        Vector3 selectionSize = RegionSelection.ClampSize(selection.size);
        Vector3 half = selectionSize * 0.5f;
        Vector3 measured = RegionSelection.ClampSize(removedSize);
        Vector3 inset = new(
            Mathf.Min(selectionSize.x * TransitionDepthRatio, measured.x * TransitionDepthRatio),
            Mathf.Min(selectionSize.y * TransitionDepthRatio, measured.y * TransitionDepthRatio),
            Mathf.Min(selectionSize.z * TransitionDepthRatio, measured.z * TransitionDepthRatio));
        Vector3 min = -half;
        Vector3 max = half;

        if (seamFaces.Contains(RegionBoundaryFace.NegativeX)) min.x += inset.x;
        if (seamFaces.Contains(RegionBoundaryFace.PositiveX)) max.x -= inset.x;
        if (seamFaces.Contains(RegionBoundaryFace.NegativeY)) min.y += inset.y;
        if (seamFaces.Contains(RegionBoundaryFace.PositiveY)) max.y -= inset.y;
        if (seamFaces.Contains(RegionBoundaryFace.NegativeZ)) min.z += inset.z;
        if (seamFaces.Contains(RegionBoundaryFace.PositiveZ)) max.z -= inset.z;

        Vector3 localCenter = (min + max) * 0.5f;
        RegionSelection transition = selection.Clone();
        transition.center = selection.center + selection.rotation * localCenter;
        transition.size = RegionSelection.ClampSize(max - min);
        return transition;
    }

    private static bool ClipReplacement(
        GameObject replacement,
        RegionSelection innerRegion,
        IReadOnlyCollection<RegionBoundaryFace> seamFaces,
        string siblingAssetPath,
        List<RegionBoundaryLoop> boundaryLoops,
        out string error)
    {
        error = string.Empty;
        bool hasGeometry = false;
        foreach (MeshFilter filter in replacement.GetComponentsInChildren<MeshFilter>(true))
        {
            Mesh source = filter.sharedMesh;
            if (source == null)
                continue;

            MeshCutResult cut = MeshRegionSplitter.Cut(
                source, filter.transform.localToWorldMatrix, innerRegion, seamFaces);
            if (cut.hasOpenBoundary)
            {
                error = $"Replacement mesh '{source.name}' produced an open clipped boundary.";
                return false;
            }
            boundaryLoops.AddRange(cut.boundaryLoops);
            if (cut.insideMesh == null)
            {
                error = $"Replacement mesh '{source.name}' has no geometry inside the transition band.";
                return false;
            }

            filter.sharedMesh = MeshImporter.PersistMesh(
                cut.insideMesh, siblingAssetPath, $"{source.name}_ClippedReplacement");
            hasGeometry = true;
            if (cut.outsideMesh != null)
                UnityEngine.Object.DestroyImmediate(cut.outsideMesh);
        }

        if (!hasGeometry || boundaryLoops.Count == 0)
        {
            error = "The replacement did not cross the inset OBB, so no weldable boundary was produced.";
            return false;
        }
        return true;
    }

    private static GameObject CreateTransitionObject(
        Transform parent,
        Mesh mesh,
        string siblingAssetPath)
    {
        var transition = new GameObject("WeldedTransition");
        transition.transform.SetParent(parent, worldPositionStays: false);
        transition.AddComponent<MeshFilter>().sharedMesh = MeshImporter.PersistMesh(
            mesh, siblingAssetPath, mesh.name);

        Shader shader = Shader.Find("SpatialGeneration/VertexColorUnlit");
        if (shader == null)
            throw new InvalidOperationException("The vertex-colour seam shader is unavailable.");
        transition.AddComponent<MeshRenderer>().sharedMaterial =
            new Material(shader) { name = "Refinement_TransitionVertexColor" };
        return transition;
    }

    private static bool TryCombineComposite(
        GameObject refined,
        string siblingAssetPath,
        out string error)
    {
        error = string.Empty;
        if (!ValidateSeamIncidence(refined, out error))
            return false;

        var combines = new List<CombineInstance>();
        var materials = new List<Material>();
        var temporaryMeshes = new List<Mesh>();
        Matrix4x4 worldToLocal = refined.transform.worldToLocalMatrix;

        foreach (MeshFilter filter in refined.GetComponentsInChildren<MeshFilter>(true))
        {
            Mesh mesh = filter.sharedMesh;
            if (mesh == null)
                continue;
            Mesh combineMesh = mesh;
            if (mesh.colors.Length != mesh.vertexCount)
            {
                combineMesh = UnityEngine.Object.Instantiate(mesh);
                var white = new Color[combineMesh.vertexCount];
                for (int i = 0; i < white.Length; i++) white[i] = Color.white;
                combineMesh.colors = white;
                temporaryMeshes.Add(combineMesh);
            }
            Renderer renderer = filter.GetComponent<Renderer>();
            Material[] rendererMaterials = renderer != null ? renderer.sharedMaterials : Array.Empty<Material>();
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                combines.Add(new CombineInstance
                {
                    mesh = combineMesh,
                    subMeshIndex = subMesh,
                    transform = worldToLocal * filter.transform.localToWorldMatrix
                });
                materials.Add(rendererMaterials.Length == 0
                    ? null
                    : rendererMaterials[Mathf.Min(subMesh, rendererMaterials.Length - 1)]);
            }
        }

        if (combines.Count == 0)
        {
            foreach (Mesh temporary in temporaryMeshes)
                UnityEngine.Object.DestroyImmediate(temporary);
            error = "There is no preserved, replacement or transition geometry to combine.";
            return false;
        }

        var compositeMesh = new Mesh
        {
            name = "Refinement_Composite",
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
        };
        try
        {
            compositeMesh.CombineMeshes(combines.ToArray(), mergeSubMeshes: false, useMatrices: true);
        }
        finally
        {
            foreach (Mesh temporary in temporaryMeshes)
                UnityEngine.Object.DestroyImmediate(temporary);
        }
        if (!ValidateFiniteMesh(compositeMesh, out error))
        {
            UnityEngine.Object.DestroyImmediate(compositeMesh);
            return false;
        }

        GameObject[] oldChildren = new GameObject[refined.transform.childCount];
        for (int i = 0; i < oldChildren.Length; i++)
            oldChildren[i] = refined.transform.GetChild(i).gameObject;

        var composite = new GameObject("CompositeMesh");
        composite.transform.SetParent(refined.transform, false);
        composite.AddComponent<MeshFilter>().sharedMesh = MeshImporter.PersistMesh(
            compositeMesh, siblingAssetPath, compositeMesh.name);
        composite.AddComponent<MeshRenderer>().sharedMaterials = materials.ToArray();

        foreach (GameObject child in oldChildren)
            UnityEngine.Object.DestroyImmediate(child);
        return true;
    }

    private static bool ValidateSeamIncidence(GameObject refined, out string error)
    {
        error = string.Empty;
        var allEdges = new Dictionary<PositionEdge, int>();
        var seamEdges = new Dictionary<PositionEdge, int>();
        var surfaceVertices = new HashSet<PositionPoint>();
        int overConnectedEdges = 0;
        int tJunctionEdges = 0;

        foreach (MeshFilter filter in refined.GetComponentsInChildren<MeshFilter>(true))
        {
            Mesh mesh = filter.sharedMesh;
            if (mesh == null)
                continue;
            Matrix4x4 toComposite = refined.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
            Vector3[] vertices = mesh.vertices;
            bool isSeam = string.Equals(filter.gameObject.name, "WeldedTransition", StringComparison.Ordinal);
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                int[] triangles = mesh.GetTriangles(subMesh);
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    PositionPoint a = new(toComposite.MultiplyPoint3x4(vertices[triangles[i]]));
                    PositionPoint b = new(toComposite.MultiplyPoint3x4(vertices[triangles[i + 1]]));
                    PositionPoint c = new(toComposite.MultiplyPoint3x4(vertices[triangles[i + 2]]));
                    Count(allEdges, new PositionEdge(a, b));
                    Count(allEdges, new PositionEdge(b, c));
                    Count(allEdges, new PositionEdge(c, a));
                    if (!isSeam)
                    {
                        surfaceVertices.Add(a);
                        surfaceVertices.Add(b);
                        surfaceVertices.Add(c);
                        continue;
                    }
                    Count(seamEdges, new PositionEdge(a, b));
                    Count(seamEdges, new PositionEdge(b, c));
                    Count(seamEdges, new PositionEdge(c, a));
                }
            }
        }

        int introducedBoundaryEdges = 0;
        foreach (KeyValuePair<PositionEdge, int> edge in seamEdges)
        {
            if (edge.Value != 1)
                continue;
            introducedBoundaryEdges++;
            if (!allEdges.TryGetValue(edge.Key, out int incidence) || incidence < 2)
            {
                // Dense reconstructed meshes often subdivide the source side differently
                // from the transition strip. The seam edge is then a geometric T-junction:
                // both endpoints are on the surface, but there is no identical index edge.
                // Accept that representation only within eight 0.1 mm quantization cells; a
                // genuinely open/gapped seam still fails this endpoint test.
                if (!HasNearbyPoint(surfaceVertices, edge.Key.A, 8) ||
                    !HasNearbyPoint(surfaceVertices, edge.Key.B, 8))
                {
                    error = $"A welded boundary edge has {incidence} incident triangles and " +
                            "does not coincide with the reconstructed surface.";
                    return false;
                }
                tJunctionEdges++;
                continue;
            }
            if (incidence > 2)
                overConnectedEdges++;
        }
        if (introducedBoundaryEdges == 0)
        {
            error = "The transition strip has no identifiable source/replacement boundary edges.";
            return false;
        }
        if (overConnectedEdges > 0)
        {
            Debug.LogWarning(
                $"Spatial Generation: {overConnectedEdges} welded edge(s) inherit extra incident " +
                "triangles from non-manifold reconstruction geometry. The seam is closed, but " +
                "the imported lifter mesh should be remeshed for strict manifold output.");
        }
        if (tJunctionEdges > 0)
        {
            Debug.LogWarning(
                $"Spatial Generation: {tJunctionEdges} welded edge(s) meet a differently " +
                "subdivided reconstruction edge as geometric T-junctions (within 0.8 mm). " +
                "No open seam was introduced.");
        }
        return true;
    }

    private static bool HasNearbyPoint(
        HashSet<PositionPoint> points,
        PositionPoint target,
        int radius)
    {
        for (int x = -radius; x <= radius; x++)
        for (int y = -radius; y <= radius; y++)
        for (int z = -radius; z <= radius; z++)
            if (points.Contains(target.Offset(x, y, z)))
                return true;
        return false;
    }

    private static void Count(Dictionary<PositionEdge, int> values, PositionEdge key) =>
        values[key] = values.TryGetValue(key, out int count) ? count + 1 : 1;

    private readonly struct PositionPoint : IEquatable<PositionPoint>, IComparable<PositionPoint>
    {
        // 0.1 mm bins absorb importer/clipping round-off while remaining far below the
        // geometric tolerance used to repair generated-mesh boundary loops.
        private const double Quantization = 10000.0;
        private readonly long x, y, z;

        public PositionPoint(Vector3 value)
        {
            x = (long)Math.Round(value.x * Quantization);
            y = (long)Math.Round(value.y * Quantization);
            z = (long)Math.Round(value.z * Quantization);
        }

        public int CompareTo(PositionPoint other)
        {
            int result = x.CompareTo(other.x);
            if (result != 0) return result;
            result = y.CompareTo(other.y);
            return result != 0 ? result : z.CompareTo(other.z);
        }

        public PositionPoint Offset(int dx, int dy, int dz) => new(x + dx, y + dy, z + dz);

        private PositionPoint(long x, long y, long z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public bool Equals(PositionPoint other) => x == other.x && y == other.y && z == other.z;
        public override bool Equals(object obj) => obj is PositionPoint other && Equals(other);
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = x.GetHashCode();
                hash = (hash * 397) ^ y.GetHashCode();
                return (hash * 397) ^ z.GetHashCode();
            }
        }
    }

    private readonly struct PositionEdge : IEquatable<PositionEdge>
    {
        private readonly PositionPoint a, b;

        public PositionEdge(PositionPoint first, PositionPoint second)
        {
            if (first.CompareTo(second) <= 0)
            {
                a = first;
                b = second;
            }
            else
            {
                a = second;
                b = first;
            }
        }

        public bool Equals(PositionEdge other) => a.Equals(other.a) && b.Equals(other.b);
        public PositionPoint A => a;
        public PositionPoint B => b;
        public override bool Equals(object obj) => obj is PositionEdge other && Equals(other);
        public override int GetHashCode()
        {
            unchecked { return (a.GetHashCode() * 397) ^ b.GetHashCode(); }
        }
    }

    private static bool ValidateFiniteMesh(Mesh mesh, out string error)
    {
        error = string.Empty;
        foreach (Vector3 vertex in mesh.vertices)
        {
            if (float.IsNaN(vertex.x) || float.IsInfinity(vertex.x) ||
                float.IsNaN(vertex.y) || float.IsInfinity(vertex.y) ||
                float.IsNaN(vertex.z) || float.IsInfinity(vertex.z))
            {
                error = "The combined mesh contains non-finite coordinates.";
                return false;
            }
        }
        if (CountTriangles(mesh) == 0)
        {
            error = "The combined mesh contains no triangles.";
            return false;
        }
        if (mesh.colors.Length != mesh.vertexCount)
        {
            error = "The combined mesh lost its projected vertex colours.";
            return false;
        }
        return true;
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
