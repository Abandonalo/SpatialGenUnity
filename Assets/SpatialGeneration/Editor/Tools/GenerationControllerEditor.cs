using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

public static class GenerationControllerEditor
{
    private const string GeneratedRootName = "GeneratedContent";
    private static bool _isGenerating;

    /// <summary>
    /// Keeps the same signature your UI already calls.
    /// Internally runs async and applies result on the main thread with Undo.
    /// </summary>
    public static void RegenerateFromIntent(SceneIntent intent)
    {
        _ = RegenerateFromIntentAsync(intent, null, null);
    }

    public static void RegenerateFromIntent(SceneIntent intent, string promptOverride)
    {
        _ = RegenerateFromIntentAsync(intent, promptOverride, null);
    }

    public static void RegenerateFromIntent(SceneIntent intent, string promptOverride, string negativePromptOverride)
    {
        _ = RegenerateFromIntentAsync(intent, promptOverride, negativePromptOverride);
    }

    private static async Task RegenerateFromIntentAsync(SceneIntent intent, string promptOverride, string negativePromptOverride)
    {
        if (_isGenerating)
        {
            Debug.LogWarning("Spatial Generation: A generation is already running.");
            return;
        }

        _isGenerating = true;

        try
        {
            BackendSettings settings = BackendRegistry.Settings;
            Camera captureCamera = ResolveCaptureCamera();
            if (captureCamera == null)
                throw new Exception("No capture camera found. Add a camera to the scene or tag one as MainCamera.");

            IGenerationBackend backend = BackendRegistry.Current;

            // Optional: log start
            InteractionLogger.Log(new InteractionEvent
            {
                type = "generate",
                extra = $"backend={backend.Name}, proxies={intent.spatialProxies.Count}"
            });

            EditorUtility.DisplayProgressBar("Spatial Generation", $"Generating via {backend.Name}…", 0.3f);

            string resolvedPrompt = string.IsNullOrWhiteSpace(promptOverride)
                ? string.Empty
                : promptOverride.Trim();
            string resolvedNegativePrompt = string.IsNullOrWhiteSpace(negativePromptOverride)
                ? string.Empty
                : negativePromptOverride.Trim();

            GenerationResult result = await GenerationPipeline.GenerateAsync(
                captureCamera,
                settings.captureWidth,
                settings.captureHeight,
                resolvedPrompt,
                resolvedNegativePrompt,
                settings.seed,
                settings.steps,
                settings.cfg,
                settings.sampler,
                ResolveSceneStage());

            // 4) Apply result on main thread with Undo
            EditorUtility.DisplayProgressBar("Spatial Generation", "Applying result…", 0.9f);

            EditorApplication.delayCall += () =>
            {
                try
                {
                    Debug.Log("Outputs:\n" + string.Join("\n", result.outputFiles ?? new System.Collections.Generic.List<string>()));
                    ApplyResultUndoable(result, backend.Name, intent, captureCamera);
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                    _isGenerating = false;
                }
            };
        }
        catch (Exception ex)
        {
            EditorUtility.ClearProgressBar();
            _isGenerating = false;
            Debug.LogError($"Spatial Generation failed: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static Camera ResolveCaptureCamera()
    {
        if (Camera.main != null)
            return Camera.main;

        Camera[] cameras = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
        if (cameras != null && cameras.Length > 0)
            return cameras[0];

        return null;
    }

    private static void ApplyResultUndoable(GenerationResult result, string backendName, SceneIntent intent, Camera captureCamera)
    {
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName($"Regenerate ({backendName})");

        // Ensure root exists (Undo-aware)
        GameObject root = GameObject.Find(GeneratedRootName);
        if (root == null)
        {
            root = new GameObject(GeneratedRootName);
            Undo.RegisterCreatedObjectUndo(root, "Create GeneratedContent Root");
        }

        // Cleanup previous generated children (Undo-aware)
        ClearChildrenUndo(root);

        if (result.outputFiles != null)
        {
            var meshPaths = result.outputFiles.FindAll(p =>
                !string.IsNullOrWhiteSpace(p) &&
                (p.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) ||
                 p.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) ||
                 p.EndsWith(".obj", StringComparison.OrdinalIgnoreCase)) &&
                File.Exists(p));
            int pngCount = result.outputFiles.FindAll(p =>
                !string.IsNullOrWhiteSpace(p) &&
                p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(p)).Count;

            if (meshPaths.Count > 0)
            {
                List<SpatialProxyIntent> occupyProxies = GetOccupyPlacementProxies(intent);
                int placementCount = occupyProxies.Count > 0 ? occupyProxies.Count : meshPaths.Count;
                bool createdAnyMesh = false;

                for (int i = 0; i < placementCount; i++)
                {
                    string meshPath = meshPaths[Mathf.Min(i, meshPaths.Count - 1)];
                    if (!TryInstantiateMeshOutput(meshPath, root, out GameObject meshObject))
                        continue;

                    if (occupyProxies.Count > 0)
                        PlaceGeneratedObjectAtProxy(meshObject, occupyProxies[i]);

                    meshObject.name = occupyProxies.Count > 0
                        ? $"Generated_Mesh_{(string.IsNullOrWhiteSpace(occupyProxies[i].proxy_id) ? i.ToString() : occupyProxies[i].proxy_id)}"
                        : $"Generated_Mesh_{i}";
                    Undo.RegisterCreatedObjectUndo(meshObject, "Create Generated Mesh");
                    createdAnyMesh = true;
                }

                if (createdAnyMesh)
                {
                    if (meshPaths.Count > occupyProxies.Count && occupyProxies.Count > 0)
                    {
                        Debug.LogWarning(
                            $"Spatial Generation: Received {meshPaths.Count} mesh outputs but only {occupyProxies.Count} occupy proxies. " +
                            "Extra mesh outputs were not placed.");
                    }

                    Undo.CollapseUndoOperations(group);
                    return;
                }

                Debug.LogWarning("Spatial Generation: Could not instantiate mesh outputs. Falling back to image/primitive output.");
            }

            string pngPath = result.outputFiles.Find(p =>
                !string.IsNullOrWhiteSpace(p) &&
                p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(p));

            if (!string.IsNullOrWhiteSpace(pngPath))
            {
                byte[] bytes = File.ReadAllBytes(pngPath);
                Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                tex.LoadImage(bytes);

                GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.name = "Generated_Image";
                quad.transform.SetParent(root.transform, false);
                PlacePreviewQuad(quad, captureCamera, intent);

                Shader shader = Shader.Find("Unlit/Texture");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Standard");
                Material mat = new Material(shader);
                mat.mainTexture = tex;
                quad.GetComponent<Renderer>().sharedMaterial = mat;

                Undo.RegisterCreatedObjectUndo(quad, "Create Generated Image Quad");
                Undo.CollapseUndoOperations(group);
                return;
            }
        }

        // Spawn new result (Undo-aware)
        foreach (var obj in result.objects)
        {
            GameObject go = GameObject.CreatePrimitive(obj.primitiveType);
            go.name = $"Generated_{obj.primitiveType}";
            go.transform.SetParent(root.transform, worldPositionStays: false);
            go.transform.position = obj.position;
            go.transform.localScale = AdjustScaleForPrimitive(obj.primitiveType, obj.size);

            Undo.RegisterCreatedObjectUndo(go, "Create Generated Primitive");
        }

        Undo.CollapseUndoOperations(group);
    }

    private static bool TryInstantiateMeshOutput(string absolutePath, GameObject root, out GameObject instance)
    {
        instance = null;
        string assetPath = StageFileIntoGeneratedAssets(absolutePath);
        if (string.IsNullOrWhiteSpace(assetPath))
            return false;

        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

        GameObject meshAsset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        if (meshAsset == null)
            meshAsset = AssetDatabase.LoadMainAssetAtPath(assetPath) as GameObject;
        UnityEngine.Object loadedAssetAfterImport = meshAsset != null ? meshAsset : AssetDatabase.LoadMainAssetAtPath(assetPath);
        if (meshAsset == null)
        {
            Debug.LogWarning(
                $"Spatial Generation: Imported '{assetPath}', but main asset type is '{loadedAssetAfterImport?.GetType().Name ?? "null"}'. " +
                "Install or configure a compatible importer for this mesh format.");
            return false;
        }

        UnityEngine.Object created = PrefabUtility.InstantiatePrefab(meshAsset);
        instance = created as GameObject;
        if (instance == null)
            return false;

        instance.name = "Generated_Mesh";
        instance.transform.SetParent(root.transform, false);
        instance.transform.localPosition = Vector3.zero;
        bool hasVertexColors = MeshHasVertexColors(instance);
        EnsureRenderableMaterials(instance, hasVertexColors, out _);
        return true;
    }

    private static string StageFileIntoGeneratedAssets(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) || !File.Exists(absolutePath))
            return null;

        const string generatedRoot = "Assets/SpatialGeneration/Generated";
        EnsureAssetFolder("Assets/SpatialGeneration");
        EnsureAssetFolder(generatedRoot);

        string sourceDir = Path.GetDirectoryName(absolutePath);
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            return null;
        string[] files = Directory.GetFiles(sourceDir);

        string folderName = $"run_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}";
        string destinationFolder = $"{generatedRoot}/{folderName}";
        AssetDatabase.CreateFolder(generatedRoot, folderName);

        for (int i = 0; i < files.Length; i++)
        {
            string sourceFile = files[i];
            string fileName = Path.GetFileName(sourceFile);
            string destinationPath = Path.Combine(destinationFolder, fileName).Replace("\\", "/");
            File.Copy(sourceFile, destinationPath, true);
        }

        return $"{destinationFolder}/{Path.GetFileName(absolutePath)}";
    }

    private static int EnsureRenderableMaterials(GameObject root, bool preferVertexColor, out string fallbackShaderName)
    {
        fallbackShaderName = string.Empty;
        if (root == null)
            return 0;

        Shader fallback = null;
        if (preferVertexColor)
            fallback = Shader.Find("SpatialGeneration/VertexColorUnlit");
        if (fallback == null) fallback = Shader.Find("Universal Render Pipeline/Lit");
        if (fallback == null) fallback = Shader.Find("Universal Render Pipeline/Simple Lit");
        if (fallback == null) fallback = Shader.Find("Standard");
        if (fallback == null) fallback = Shader.Find("Sprites/Default");
        if (fallback == null)
            return 0;

        fallbackShaderName = fallback.name;
        int replaced = 0;
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        for (int r = 0; r < renderers.Length; r++)
        {
            Renderer renderer = renderers[r];
            Material[] mats = renderer.sharedMaterials;
            bool dirty = false;
            for (int i = 0; i < mats.Length; i++)
            {
                Material mat = mats[i];
                bool shouldReplace = mat == null || mat.shader == null || !mat.shader.isSupported;
                if (!shouldReplace)
                    continue;

                var replacement = new Material(fallback)
                {
                    name = "Generated_FallbackMaterial"
                };
                mats[i] = replacement;
                replaced++;
                dirty = true;
            }

            if (dirty)
                renderer.sharedMaterials = mats;
        }

        return replaced;
    }

    private static void EnsureAssetFolder(string assetPath)
    {
        if (AssetDatabase.IsValidFolder(assetPath))
            return;

        string parent = Path.GetDirectoryName(assetPath)?.Replace("\\", "/");
        string name = Path.GetFileName(assetPath);
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(name))
            return;

        EnsureAssetFolder(parent);
        if (!AssetDatabase.IsValidFolder(assetPath))
            AssetDatabase.CreateFolder(parent, name);
    }

    private static void PlaceGeneratedObjectAtProxy(GameObject generatedObject, SpatialProxyIntent proxy)
    {
        if (generatedObject == null)
            return;
        if (proxy == null)
            return;

        generatedObject.transform.rotation = proxy.rotation;
        FitObjectToTargetSize(generatedObject, proxy.size);
        AlignObjectBoundsCenterToTarget(generatedObject, proxy.position);
    }

    private static void PlacePreviewQuad(GameObject quad, Camera captureCamera, SceneIntent intent)
    {
        if (quad == null)
            return;

        if (TryGetPrimaryOccupyProxy(intent, out SpatialProxyIntent proxy))
        {
            quad.transform.position = proxy.position;
            quad.transform.rotation = proxy.rotation;
            quad.transform.localScale = proxy.size;
            return;
        }

        if (captureCamera != null)
        {
            quad.transform.position = captureCamera.transform.position + captureCamera.transform.forward * 2f;
            quad.transform.rotation = Quaternion.LookRotation(-captureCamera.transform.forward, captureCamera.transform.up);
            quad.transform.localScale = Vector3.one;
            return;
        }

        quad.transform.localPosition = Vector3.zero;
    }

    private static bool TryGetPrimaryOccupyProxy(SceneIntent intent, out SpatialProxyIntent proxy)
    {
        proxy = null;
        List<SpatialProxyIntent> proxies = GetOccupyPlacementProxies(intent);
        if (proxies.Count == 0)
            return false;
        proxy = proxies[0];
        return true;
    }

    private static List<SpatialProxyIntent> GetOccupyPlacementProxies(SceneIntent intent)
    {
        var proxies = new List<SpatialProxyIntent>();
        if (intent?.spatialProxies == null || intent.spatialProxies.Count == 0)
            return proxies;

        for (int i = 0; i < intent.spatialProxies.Count; i++)
        {
            SpatialProxyIntent candidate = intent.spatialProxies[i];
            if (candidate == null)
                continue;

            // Placement targets are explicit occupy proxies only.
            if (candidate.role == SpatialProxyRole.Occupy)
                proxies.Add(candidate);
        }

        return proxies;
    }

    private static void FitObjectToTargetSize(GameObject generatedObject, Vector3 targetSize)
    {
        Renderer[] renderers = generatedObject.GetComponentsInChildren<Renderer>();
        if (renderers == null || renderers.Length == 0)
            return;

        Bounds combined = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            combined.Encapsulate(renderers[i].bounds);

        const float min = 1e-4f;
        Vector3 currentSize = combined.size;
        Vector3 safeCurrent = new(
            Mathf.Max(min, currentSize.x),
            Mathf.Max(min, currentSize.y),
            Mathf.Max(min, currentSize.z));
        Vector3 safeTarget = new(
            Mathf.Max(min, targetSize.x),
            Mathf.Max(min, targetSize.y),
            Mathf.Max(min, targetSize.z));

        // Preserve generated mesh proportions: scale uniformly to avoid axis stretching.
        Vector3 axisRatios = new(
            safeTarget.x / safeCurrent.x,
            safeTarget.y / safeCurrent.y,
            safeTarget.z / safeCurrent.z);
        float uniformScale = Mathf.Max(axisRatios.x, Mathf.Max(axisRatios.y, axisRatios.z));
        Vector3 multiplier = new(uniformScale, uniformScale, uniformScale);
        generatedObject.transform.localScale = Vector3.Scale(generatedObject.transform.localScale, multiplier);
    }

    private static void AlignObjectBoundsCenterToTarget(GameObject generatedObject, Vector3 targetCenter)
    {
        if (generatedObject == null)
            return;

        Renderer[] renderers = generatedObject.GetComponentsInChildren<Renderer>();
        if (renderers == null || renderers.Length == 0)
        {
            generatedObject.transform.position = targetCenter;
            return;
        }

        Bounds combined = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            combined.Encapsulate(renderers[i].bounds);

        Vector3 offset = targetCenter - combined.center;
        generatedObject.transform.position += offset;
    }

    private static bool MeshHasVertexColors(GameObject root)
    {
        if (root == null)
            return false;
        MeshFilter[] meshFilters = root.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < meshFilters.Length; i++)
        {
            Mesh mesh = meshFilters[i].sharedMesh;
            if (mesh == null)
                continue;
            if (mesh.colors != null && mesh.colors.Length > 0)
                return true;
        }

        return false;
    }

    private static SpatialGeneration.Generation.Intent.SceneStage ResolveSceneStage()
    {
        GameObject root = GameObject.Find(GeneratedRootName);
        bool hasGeneratedContent = root != null && root.transform.childCount > 0;
        return hasGeneratedContent
            ? SpatialGeneration.Generation.Intent.SceneStage.Refinement
            : SpatialGeneration.Generation.Intent.SceneStage.Creation;
    }

    public static void CleanupGeneratedContent()
    {
        GameObject root = GameObject.Find(GeneratedRootName);
        if (root == null) return;

        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Cleanup GeneratedContent");

        ClearChildrenUndo(root);

        Undo.CollapseUndoOperations(group);

        InteractionLogger.Log(new InteractionEvent { type = "cleanup" });
    }

    private static void ClearChildrenUndo(GameObject root)
    {
        for (int i = root.transform.childCount - 1; i >= 0; i--)
        {
            var child = root.transform.GetChild(i).gameObject;
            Undo.DestroyObjectImmediate(child);
        }
    }

    private static Vector3 AdjustScaleForPrimitive(PrimitiveType type, Vector3 desiredBounds)
    {
        // Unity cylinder mesh is 2 units tall in local space, so halve Y to match desired height
        return type == PrimitiveType.Cylinder
            ? new Vector3(desiredBounds.x, desiredBounds.y * 0.5f, desiredBounds.z)
            : desiredBounds;
    }

    private static string EscapeJson(string value)
    {
        return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
