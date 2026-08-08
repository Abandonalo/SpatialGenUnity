using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using SpatialGeneration.Generation;
using SpatialGeneration.Generation.Intent;

/// <summary>
/// Editor entry point for Generate: runs <see cref="GenerationPipeline"/> and materialises the
/// results in the scene as a single undoable operation.
/// </summary>
public static class GenerationRunner
{
    public const string GeneratedRootName = "GeneratedContent";

    /// <summary>
    /// TripoSR and Hunyuan return front-view meshes whose visible face points down local -X,
    /// while a spatial proxy defines its front as local +X. This corrects for that.
    /// </summary>
    private static readonly Quaternion MeshFrontToProxyFront = Quaternion.FromToRotation(Vector3.left, Vector3.right);

    private static bool _isRunning;

    public static void Generate(string prompt, string negativePrompt) => _ = GenerateAsync(prompt, negativePrompt);

    private static async Task GenerateAsync(string prompt, string negativePrompt)
    {
        if (_isRunning)
        {
            Debug.LogWarning("Spatial Generation: a generation is already running.");
            return;
        }

        _isRunning = true;
        try
        {
            IGenerationBackend backend = BackendRegistry.Current;
            EditorUtility.DisplayProgressBar("Spatial Generation", $"Generating via {backend.Name}…", 0.3f);

            GenerationResult result = await GenerationPipeline.GenerateAsync(
                prompt, negativePrompt, ResolveSceneStage());

            EditorUtility.DisplayProgressBar("Spatial Generation", "Applying result…", 0.9f);

            // Scene mutation and Undo registration have to happen on the main thread.
            EditorApplication.delayCall += () =>
            {
                try
                {
                    ApplyResult(result, backend.Name);
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                    _isRunning = false;
                }
            };
        }
        catch (Exception ex)
        {
            EditorUtility.ClearProgressBar();
            _isRunning = false;
            Debug.LogError($"Spatial Generation failed: {ex.Message}\n{ex.StackTrace}");
            EditorUtility.DisplayDialog("Spatial Generation Failed", ex.Message, "OK");
        }
    }

    private static void ApplyResult(GenerationResult result, string backendName)
    {
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName($"Generate ({backendName})");

        GameObject root = EnsureGeneratedRoot();
        ClearChildren(root);

        var proxiesById = new Dictionary<string, SpatialProxy>(StringComparer.Ordinal);
        foreach (SpatialProxy proxy in UnityEngine.Object.FindObjectsByType<SpatialProxy>(FindObjectsSortMode.None))
        {
            if (proxy != null && !string.IsNullOrWhiteSpace(proxy.ProxyId))
                proxiesById[proxy.ProxyId] = proxy;
        }

        foreach (AssetGenerationResult asset in result.Assets)
        {
            proxiesById.TryGetValue(asset.ProxyId ?? string.Empty, out SpatialProxy proxy);
            GameObject placed = asset.HasMesh
                ? InstantiateMesh(asset, root.transform)
                : InstantiateFallbackPrimitive(asset, root.transform);

            if (placed == null)
                continue;

            placed.name = $"Generated_Mesh_{Sanitize(asset.ProxyId)}";
            if (proxy != null)
                PlaceAtProxy(placed, proxy);

            AttachMetadata(placed, asset, proxy);
            Undo.RegisterCreatedObjectUndo(placed, "Create Generated Mesh");
        }

        Undo.CollapseUndoOperations(group);
    }

    private static GameObject InstantiateMesh(AssetGenerationResult asset, Transform parent)
    {
        if (MeshImporter.TryInstantiate(asset.MeshPath, parent, preferVertexColors: false, out GameObject instance, out string assetPath))
        {
            asset.MeshPath = assetPath;
            return instance;
        }

        Debug.LogWarning(
            $"Spatial Generation: could not import '{asset.MeshPath}' for proxy '{asset.ProxyId}'. " +
            "Placing the proxy primitive instead.");
        asset.FallbackPrimitive ??= PrimitiveType.Cube;
        return InstantiateFallbackPrimitive(asset, parent);
    }

    private static GameObject InstantiateFallbackPrimitive(AssetGenerationResult asset, Transform parent)
    {
        GameObject primitive = GameObject.CreatePrimitive(asset.FallbackPrimitive ?? PrimitiveType.Cube);
        primitive.transform.SetParent(parent, worldPositionStays: false);
        return primitive;
    }

    private static void PlaceAtProxy(GameObject generated, SpatialProxy proxy)
    {
        generated.transform.rotation = GetMeshRotationForProxy(proxy.transform.rotation);
        MeshFitting.FitToVolume(
            generated,
            Vector3.Scale(proxy.size, proxy.transform.lossyScale),
            proxy.transform.position,
            BackendRegistry.Settings.preserveAssetProportions);
    }

    public static Quaternion GetMeshRotationForProxy(Quaternion proxyRotation) => proxyRotation * MeshFrontToProxyFront;

    private static void AttachMetadata(GameObject target, AssetGenerationResult asset, SpatialProxy proxy)
    {
        GeneratedMeshMetadata metadata = target.GetComponent<GeneratedMeshMetadata>()
                                         ?? Undo.AddComponent<GeneratedMeshMetadata>(target);

        metadata.meshPath = MeshImporter.ToProjectRelativePath(asset.MeshPath);
        metadata.proxyId = asset.ProxyId ?? string.Empty;

        if (proxy == null)
            return;

        metadata.proxyPosition = proxy.transform.position;
        metadata.proxyRotation = proxy.transform.rotation;
        metadata.proxySize = Vector3.Scale(proxy.size, proxy.transform.lossyScale);
    }

    public static GameObject EnsureGeneratedRoot()
    {
        GameObject root = GameObject.Find(GeneratedRootName);
        if (root != null)
            return root;

        root = new GameObject(GeneratedRootName);
        Undo.RegisterCreatedObjectUndo(root, "Create GeneratedContent Root");
        return root;
    }

    public static void CleanupGeneratedContent()
    {
        GameObject root = GameObject.Find(GeneratedRootName);
        if (root == null)
            return;

        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Cleanup GeneratedContent");
        ClearChildren(root);
        Undo.CollapseUndoOperations(group);

        InteractionLogger.Log(new InteractionEvent { type = "cleanup" });
    }

    private static void ClearChildren(GameObject root)
    {
        for (int i = root.transform.childCount - 1; i >= 0; i--)
            Undo.DestroyObjectImmediate(root.transform.GetChild(i).gameObject);
    }


    private static SceneStage ResolveSceneStage()
    {
        GameObject root = GameObject.Find(GeneratedRootName);
        return root != null && root.transform.childCount > 0 ? SceneStage.Refinement : SceneStage.Creation;
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "0";

        char[] chars = value.Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_' && chars[i] != '-')
                chars[i] = '_';
        }

        return new string(chars).Trim('_');
    }
}
