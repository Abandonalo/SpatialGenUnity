using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

public class SpatialGenerationWindow : EditorWindow
{
    private const string GlobalStylePromptPrefsKey = "SpatialGenerationWindow.GlobalStylePrompt";
    private const string GlobalNegativeStylePromptPrefsKey = "SpatialGenerationWindow.GlobalNegativeStylePrompt";
    private const string LocalRefinementPromptPrefsKey = "SpatialGenerationWindow.LocalRefinementPrompt";
    private const string DefaultLocalComfyBaseUrl = "http://127.0.0.1:8188";
    private const string DefaultLocalRouterBaseUrl = "http://127.0.0.1:8001";
    private const string DefaultColabBaseUrl = "https://comfyuitunnel.share.zrok.io";
    private static readonly HttpClient Http = new();
    private string _globalStylePrompt = string.Empty;
    private string _globalNegativeStylePrompt = string.Empty;
    private string _localRefinementPrompt = string.Empty;
    private string[] _workflowTemplateOptions = Array.Empty<string>();

    private enum BackendConnectionPreset
    {
        LocalComfyApi,
        Colab
    }

    [MenuItem("Tools/Spatial Generation")]
    public static void Open()
    {
        var window = GetWindow<SpatialGenerationWindow>();
        window.titleContent = new GUIContent("Spatial Generation");
        window.Show();
    }

    private void OnEnable()
    {
        _globalStylePrompt = EditorPrefs.GetString(GlobalStylePromptPrefsKey, string.Empty);
        _globalNegativeStylePrompt = EditorPrefs.GetString(GlobalNegativeStylePromptPrefsKey, string.Empty);
        _localRefinementPrompt = EditorPrefs.GetString(LocalRefinementPromptPrefsKey, string.Empty);
        RefreshWorkflowTemplateOptions();
    }

    private void OnGUI()
    {
        GUILayout.Label("Spatial Generation", EditorStyles.boldLabel);

        DrawBackendConfiguration();

        GUILayout.Space(6);
        GUILayout.Label("Global Style Prompt", EditorStyles.label);
        EditorGUI.BeginChangeCheck();
        string updatedStylePrompt = EditorGUILayout.TextArea(_globalStylePrompt, GUILayout.MinHeight(52f));
        if (EditorGUI.EndChangeCheck())
        {
            _globalStylePrompt = updatedStylePrompt;
            EditorPrefs.SetString(GlobalStylePromptPrefsKey, _globalStylePrompt);
        }

        EditorGUILayout.HelpBox(
            "Applied to every generated asset so all per-proxy results share a unified style.",
            MessageType.Info);

        GUILayout.Space(4);
        GUILayout.Label("Global Negative Style Prompt", EditorStyles.label);
        EditorGUI.BeginChangeCheck();
        string updatedNegativeStylePrompt = EditorGUILayout.TextArea(_globalNegativeStylePrompt, GUILayout.MinHeight(52f));
        if (EditorGUI.EndChangeCheck())
        {
            _globalNegativeStylePrompt = updatedNegativeStylePrompt;
            EditorPrefs.SetString(GlobalNegativeStylePromptPrefsKey, _globalNegativeStylePrompt);
        }

        EditorGUILayout.HelpBox(
            "Applied to every generated asset as shared style exclusions.",
            MessageType.None);

        if (GUILayout.Button("Add Spatial Proxy"))
        {
            SpatialProxyFactory.CreateProxy();
        }

        if (GUILayout.Button("Generate"))
        {
            var snapshotIntent = SpatialGeneration.Generation.Intent.SceneIntentBuilder.Build();
            string snapshotJson = SpatialGeneration.Generation.Intent.IntentJson.SerializeSceneIntent(snapshotIntent);
            string snapshotPath = WriteSceneIntentSnapshot(snapshotJson);

            var intent = SceneIntentBuilder.Build();
            string combinedPrompt = ComposePrompt(string.Empty, _globalStylePrompt);
            string combinedNegativePrompt = ComposePrompt(string.Empty, _globalNegativeStylePrompt);

            // If you're using the Undoable controller:
            GenerationControllerEditor.RegenerateFromIntent(intent, combinedPrompt, combinedNegativePrompt);

            // Also log a clean generate event (optional)
            InteractionLogger.Log(new InteractionEvent
            {
                type = "generate",
                extra = $"proxies={intent.spatialProxies.Count}, intent_json={snapshotPath}, style_prompt={_globalStylePrompt}, negative_style_prompt={_globalNegativeStylePrompt}"
            });

            Debug.Log($"Spatial Generation: SceneIntent snapshot saved to {snapshotPath}");
        }

        DrawRefinementTools();

        if (GUILayout.Button("Check Backend Health"))
        {
            _ = CheckBackendHealthAsync();
        }

        if (GUILayout.Button("Cleanup GeneratedContent"))
        {
            GenerationControllerEditor.CleanupGeneratedContent();

            InteractionLogger.Log(new InteractionEvent
            {
                type = "cleanup"
            });
        }

        GUILayout.Space(8);

        if (GUILayout.Button("Open Interaction Log Folder"))
        {
            // calls the menu method inside the logger
            InteractionLogger.RevealLogFolder();
        }
    }

    private void DrawBackendConfiguration()
    {
        BackendSettings settings = BackendRegistry.Settings;
        if (settings == null)
            return;

        GUILayout.Space(4);
        GUILayout.Label("Backend Configuration", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();
        BackendConnectionPreset selectedPreset = (BackendConnectionPreset)EditorGUILayout.EnumPopup(
            "Backend Preset",
            GetCurrentPreset(settings));
        if (EditorGUI.EndChangeCheck())
        {
            ApplyBackendPreset(settings, selectedPreset);
            PersistBackendSettings(settings);
        }

        RefreshWorkflowTemplateOptions();
        int selectedWorkflowIndex = GetSelectedWorkflowIndex(settings.comfyWorkflowTemplatePath);
        EditorGUI.BeginChangeCheck();
        int updatedWorkflowIndex = EditorGUILayout.Popup("Workflow Template", selectedWorkflowIndex, _workflowTemplateOptions);
        if (EditorGUI.EndChangeCheck() && updatedWorkflowIndex >= 0 && updatedWorkflowIndex < _workflowTemplateOptions.Length)
        {
            settings.comfyWorkflowTemplatePath = _workflowTemplateOptions[updatedWorkflowIndex];
            PersistBackendSettings(settings);
        }

        BackendConnectionPreset activePreset = GetCurrentPreset(settings);
        string configuredBaseUrl = activePreset == BackendConnectionPreset.Colab
            ? GetConfiguredColabBaseUrl(settings)
            : GetConfiguredLocalBaseUrl(settings);
        string endpointLabel = activePreset == BackendConnectionPreset.Colab ? "Colab Base URL" : "Local Base URL";

        EditorGUI.BeginChangeCheck();
        string updatedBaseUrl = EditorGUILayout.TextField(endpointLabel, configuredBaseUrl);
        if (EditorGUI.EndChangeCheck())
        {
            ApplyEndpointOverride(settings, activePreset, updatedBaseUrl);
            PersistBackendSettings(settings);
        }

        if (activePreset == BackendConnectionPreset.LocalComfyApi)
        {
            EditorGUI.BeginChangeCheck();
            bool updatedAutoStart = EditorGUILayout.Toggle("Auto Start Local ComfyUI", settings.comfyAutoStart);
            if (EditorGUI.EndChangeCheck())
            {
                settings.comfyAutoStart = updatedAutoStart;
                PersistBackendSettings(settings);
            }
        }

        string workflowPath = string.IsNullOrWhiteSpace(settings.comfyWorkflowTemplatePath)
            ? "(none)"
            : settings.comfyWorkflowTemplatePath;
        string summary = activePreset == BackendConnectionPreset.Colab
            ? $"Using Colab FastAPI proxy at {settings.remoteUrl}\nWorkflow template: {workflowPath}"
            : $"Using local ComfyUI API at {settings.comfyBaseUrl}\nAuto start: {(settings.comfyAutoStart ? "enabled" : "disabled")}\nWorkflow template: {workflowPath}";
        EditorGUILayout.HelpBox(summary, MessageType.None);
    }

    private void DrawRefinementTools()
    {
        GUILayout.Space(8);
        GUILayout.Label("Local Region Refinement", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();
        string updatedLocalPrompt = EditorGUILayout.TextArea(_localRefinementPrompt, GUILayout.MinHeight(52f));
        if (EditorGUI.EndChangeCheck())
        {
            _localRefinementPrompt = updatedLocalPrompt;
            EditorPrefs.SetString(LocalRefinementPromptPrefsKey, _localRefinementPrompt);
        }

        EditorGUILayout.HelpBox(
            "Select a region in the Scene view, describe the local change, then send RGB/depth/mask inputs to the refinement backend.",
            MessageType.Info);

        RefinementController controller = FindFirstObjectByType<RefinementController>();
        if (controller == null)
        {
            if (GUILayout.Button("Setup Refinement Rig"))
            {
                controller = EnsureRefinementRig();
                Selection.activeGameObject = controller != null ? controller.gameObject : null;
            }

            EditorGUILayout.HelpBox(
                "Create the refinement rig to enable region selection and local refinement requests.",
                MessageType.None);
            return;
        }

        RegionSelectionManager selectionManager = controller.selectionManager != null
            ? controller.selectionManager
            : controller.GetComponent<RegionSelectionManager>();
        RegionMaskRenderer maskRenderer = controller.maskRenderer != null
            ? controller.maskRenderer
            : controller.GetComponent<RegionMaskRenderer>();
        controller.selectionManager = selectionManager;
        controller.maskRenderer = maskRenderer;

        if (selectionManager == null || maskRenderer == null)
        {
            EditorGUILayout.HelpBox(
                "The refinement rig is missing required components. Recreate the rig or reassign the references.",
                MessageType.Error);
            return;
        }

        string selectionSummary = selectionManager?.CurrentSelection == null
            ? "No active region selection."
            : $"Selection: center={selectionManager.CurrentSelection.center}, size={selectionManager.CurrentSelection.size}";
        EditorGUILayout.HelpBox(selectionSummary, MessageType.None);

        if (GUILayout.Button("Reset Selection"))
        {
            Undo.RecordObject(selectionManager, "Reset Region Selection");
            if (!selectionManager.TryInitializeFromSceneGeometry(0.7f))
                selectionManager.BeginSelection();
            selectionManager.ConfirmSelection();
            EditorUtility.SetDirty(selectionManager);
            SceneView.RepaintAll();
        }

        using (new EditorGUI.DisabledScope(selectionManager == null || selectionManager.CurrentSelection == null || controller.IsRunning))
        {
            if (GUILayout.Button(controller.IsRunning ? "Refining..." : "Refine Selected Region"))
            {
                controller.RunMultiViewRefinement(_globalStylePrompt, _localRefinementPrompt);

                InteractionLogger.Log(new InteractionEvent
                {
                    type = "refine_region",
                    extra = $"selection={selectionManager.CurrentSelection.selectionId}, global_prompt={_globalStylePrompt}, local_prompt={_localRefinementPrompt}"
                });
            }
        }
    }

    private static string WriteSceneIntentSnapshot(string json)
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string logDir = Path.Combine(projectRoot, "Logs", "SpatialGenerationLogs");
        Directory.CreateDirectory(logDir);

        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        string filePath = Path.Combine(logDir, $"scene_intent_{timestamp}.json");
        File.WriteAllText(filePath, json);
        return filePath;
    }

    private static string ComposePrompt(string basePrompt, string globalStylePrompt)
    {
        string trimmedBasePrompt = string.IsNullOrWhiteSpace(basePrompt) ? string.Empty : basePrompt.Trim();
        string trimmedStylePrompt = string.IsNullOrWhiteSpace(globalStylePrompt) ? string.Empty : globalStylePrompt.Trim();

        if (string.IsNullOrWhiteSpace(trimmedStylePrompt))
            return trimmedBasePrompt;
        if (string.IsNullOrWhiteSpace(trimmedBasePrompt))
            return trimmedStylePrompt;

        return $"{trimmedBasePrompt}, {trimmedStylePrompt}";
    }

    private static async Task CheckBackendHealthAsync()
    {
        BackendSettings settings = BackendRegistry.Settings;
        string baseUrl = string.IsNullOrWhiteSpace(settings.comfyBaseUrl)
            ? settings.remoteUrl?.Replace("/generate", string.Empty)
            : settings.comfyBaseUrl;

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            Debug.LogError("Spatial Generation: No backend base URL is configured.");
            return;
        }

        string healthUrl = $"{baseUrl.TrimEnd('/')}/health";

        try
        {
            using HttpResponseMessage response = await Http.GetAsync(healthUrl);
            string body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                Debug.Log($"Spatial Generation backend health OK: {healthUrl}\n{body}");
                return;
            }

            Debug.LogError($"Spatial Generation backend health failed: {(int)response.StatusCode} {response.ReasonPhrase}\n{healthUrl}\n{body}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Spatial Generation backend health request failed: {healthUrl}\n{ex.Message}");
        }
    }

    private void RefreshWorkflowTemplateOptions()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string handoffDirectory = Path.Combine(projectRoot, "SpatialGenHandoff");
        var options = new List<string>();

        if (Directory.Exists(handoffDirectory))
        {
            string[] files = Directory.GetFiles(handoffDirectory, "*.json", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Length; i++)
            {
                string relativePath = MakeProjectRelativePath(projectRoot, files[i]);
                if (!string.IsNullOrWhiteSpace(relativePath))
                    options.Add(relativePath);
            }
        }

        if (options.Count == 0)
            options.Add("SpatialGenHandoff/generation.json");

        _workflowTemplateOptions = options.ToArray();
    }

    private int GetSelectedWorkflowIndex(string currentWorkflowPath)
    {
        if (_workflowTemplateOptions == null || _workflowTemplateOptions.Length == 0)
            return 0;

        string normalizedCurrent = NormalizePath(currentWorkflowPath);
        for (int i = 0; i < _workflowTemplateOptions.Length; i++)
        {
            if (NormalizePath(_workflowTemplateOptions[i]) == normalizedCurrent)
                return i;
        }

        return 0;
    }

    private static BackendConnectionPreset GetCurrentPreset(BackendSettings settings)
    {
        // Distinguish by whether comfyBaseUrl points at a local host, rather than
        // by whether remoteUrl is empty. Both presets now populate remoteUrl
        // (refinement requires the FastAPI router, which serves /refine).
        return IsLocalUrl(settings?.comfyBaseUrl)
            ? BackendConnectionPreset.LocalComfyApi
            : BackendConnectionPreset.Colab;
    }

    private static void ApplyBackendPreset(BackendSettings settings, BackendConnectionPreset preset)
    {
        settings.backendKind = BackendKind.RemoteHttp;

        switch (preset)
        {
            case BackendConnectionPreset.LocalComfyApi:
                // Local setup: ComfyUI on :8188, FastAPI router on :8001.
                // Refinement (/refine) is served only by the router, so
                // remoteUrl must point at the router's /generate, not be empty.
                settings.comfyBaseUrl = GetConfiguredLocalBaseUrl(settings);
                settings.remoteUrl = $"{DefaultLocalRouterBaseUrl.TrimEnd('/')}/generate";
                settings.comfyWsUrl = BuildWebSocketUrl(settings.comfyBaseUrl);
                break;

            case BackendConnectionPreset.Colab:
                // Colab setup: ComfyUI and FastAPI router both served from
                // the tunnelled base URL.
                settings.comfyBaseUrl = GetConfiguredColabBaseUrl(settings);
                settings.remoteUrl = $"{settings.comfyBaseUrl.TrimEnd('/')}/generate";
                settings.comfyWsUrl = string.Empty;
                break;
        }
    }

    private static void ApplyEndpointOverride(BackendSettings settings, BackendConnectionPreset preset, string baseUrl)
    {
        string normalizedBaseUrl = NormalizeBaseUrl(baseUrl, preset == BackendConnectionPreset.Colab ? DefaultColabBaseUrl : DefaultLocalComfyBaseUrl);
        settings.comfyBaseUrl = normalizedBaseUrl;

        if (preset == BackendConnectionPreset.Colab)
        {
            settings.remoteUrl = $"{normalizedBaseUrl.TrimEnd('/')}/generate";
            settings.comfyWsUrl = string.Empty;
            return;
        }

        // Local: route /refine (and /generate) through the local FastAPI router
        // on port 8001. ComfyUI itself stays on whatever the user configured
        // (typically 127.0.0.1:8188) and is reached via the router.
        settings.remoteUrl = $"{DefaultLocalRouterBaseUrl.TrimEnd('/')}/generate";
        settings.comfyWsUrl = BuildWebSocketUrl(normalizedBaseUrl);
    }

    private static string GetConfiguredLocalBaseUrl(BackendSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings?.comfyBaseUrl) && IsLocalUrl(settings.comfyBaseUrl))
            return NormalizeBaseUrl(settings.comfyBaseUrl, DefaultLocalComfyBaseUrl);

        return DefaultLocalComfyBaseUrl;
    }

    private static string GetConfiguredColabBaseUrl(BackendSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings?.remoteUrl) && Uri.TryCreate(settings.remoteUrl, UriKind.Absolute, out Uri remoteUri))
            return $"{remoteUri.Scheme}://{remoteUri.Authority}";

        if (!string.IsNullOrWhiteSpace(settings?.comfyBaseUrl) && !IsLocalUrl(settings.comfyBaseUrl))
            return NormalizeBaseUrl(settings.comfyBaseUrl, DefaultColabBaseUrl);

        return DefaultColabBaseUrl;
    }

    private static string NormalizeBaseUrl(string baseUrl, string fallback)
    {
        string normalized = string.IsNullOrWhiteSpace(baseUrl) ? fallback : baseUrl.Trim();
        return normalized.TrimEnd('/');
    }

    private static string BuildWebSocketUrl(string httpUrl)
    {
        if (string.IsNullOrWhiteSpace(httpUrl))
            return string.Empty;

        if (!Uri.TryCreate(httpUrl, UriKind.Absolute, out Uri uri))
            return string.Empty;

        string scheme = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        return $"{scheme}://{uri.Authority}/ws";
    }

    private static bool IsLocalUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.IndexOf("127.0.0.1", StringComparison.OrdinalIgnoreCase) >= 0 ||
               value.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void PersistBackendSettings(BackendSettings settings)
    {
        EditorUtility.SetDirty(settings);
        AssetDatabase.SaveAssets();
        BackendRegistry.Reload();
    }

    private static RefinementController EnsureRefinementRig()
    {
        RefinementController existing = FindFirstObjectByType<RefinementController>();
        if (existing != null)
            return existing;

        GameObject root = new GameObject("SpatialGenerationRefinement");
        Undo.RegisterCreatedObjectUndo(root, "Create Refinement Rig");

        RegionSelectionManager selectionManager = root.AddComponent<RegionSelectionManager>();
        RegionMaskRenderer maskRenderer = root.AddComponent<RegionMaskRenderer>();
        RefinementController controller = root.AddComponent<RefinementController>();

        maskRenderer.renderCamera = Camera.main != null ? Camera.main : FindFirstObjectByType<Camera>();
        controller.selectionManager = selectionManager;
        controller.maskRenderer = maskRenderer;
        selectionManager.TryInitializeFromSceneGeometry(0.7f);
        selectionManager.ConfirmSelection();

        EditorUtility.SetDirty(root);
        EditorSceneManager.MarkSceneDirty(root.scene);
        return controller;
    }

    private static string MakeProjectRelativePath(string projectRoot, string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(absolutePath))
            return string.Empty;

        string relativePath = absolutePath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase)
            ? absolutePath.Substring(projectRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : absolutePath;
        return NormalizePath(relativePath);
    }

    private static string NormalizePath(string path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Replace('\\', '/');
    }
}
