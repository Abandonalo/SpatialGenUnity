using System;
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
        _ = RegenerateFromIntentAsync(intent);
    }

    private static async Task RegenerateFromIntentAsync(SceneIntent intent)
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

            GenerationResult result = await GenerationPipeline.GenerateAsync(
                captureCamera,
                settings.captureWidth,
                settings.captureHeight,
                settings.prompt,
                settings.negativePrompt,
                settings.seed,
                settings.steps,
                settings.cfg,
                settings.sampler,
                SpatialGeneration.Generation.Intent.SceneStage.Creation);

            // 4) Apply result on main thread with Undo
            EditorUtility.DisplayProgressBar("Spatial Generation", "Applying result…", 0.9f);

            EditorApplication.delayCall += () =>
            {
                try
                {
                    ApplyResultUndoable(result, backend.Name);
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

    private static void ApplyResultUndoable(GenerationResult result, string backendName)
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
}
