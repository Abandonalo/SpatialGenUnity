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
    private const string DefaultColabNotebookPath = "notebooks/Colab_ComfyUI.ipynb";
    private const string DefaultColabNotebookUrl = "https://colab.research.google.com/github/Abandonalo/SpatialGenUnity/blob/main/notebooks/Colab_ComfyUI.ipynb";
    private static readonly HttpClient Http = new();
    private string _globalStylePrompt = string.Empty;
    private string _globalNegativeStylePrompt = string.Empty;
    private string _localRefinementPrompt = string.Empty;
    private string[] _workflowTemplateOptions = Array.Empty<string>();

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
            var intent = SpatialGeneration.Generation.Intent.SceneIntentBuilder.Build();
            string snapshotJson = SpatialGeneration.Generation.Intent.IntentJson.SerializeSceneIntent(intent);
            string snapshotPath = WriteSceneIntentSnapshot(snapshotJson);

            string combinedPrompt = ComposePrompt(string.Empty, _globalStylePrompt);
            string combinedNegativePrompt = ComposePrompt(string.Empty, _globalNegativeStylePrompt);

            // If you're using the Undoable controller:
            GenerationControllerEditor.RegenerateFromIntent(intent, combinedPrompt, combinedNegativePrompt);

            // Also log a clean generate event (optional)
            InteractionLogger.Log(new InteractionEvent
            {
                type = "generate",
                extra = $"proxies={intent.Proxies.Count}, intent_json={snapshotPath}, style_prompt={_globalStylePrompt}, negative_style_prompt={_globalNegativeStylePrompt}"
            });

            Debug.Log($"Spatial Generation: SceneIntent snapshot saved to {snapshotPath}");
        }

        DrawRefinementTools();

        if (GUILayout.Button("Check Backend Health"))
        {
            _ = CheckBackendHealthAsync(false);
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
        BackendPreset selectedPreset = (BackendPreset)EditorGUILayout.EnumPopup(
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

        BackendPreset activePreset = GetCurrentPreset(settings);
        string configuredBaseUrl = activePreset == BackendPreset.Colab
            ? GetConfiguredColabBaseUrl(settings)
            : GetConfiguredLocalBaseUrl(settings);
        string endpointLabel = activePreset == BackendPreset.Colab ? "Colab Base URL" : "Local Base URL";

        EditorGUI.BeginChangeCheck();
        string updatedBaseUrl = EditorGUILayout.TextField(endpointLabel, configuredBaseUrl);
        if (EditorGUI.EndChangeCheck())
        {
            ApplyEndpointOverride(settings, activePreset, updatedBaseUrl);
            PersistBackendSettings(settings);
        }

        if (activePreset == BackendPreset.LocalComfyApi)
        {
            EditorGUI.BeginChangeCheck();
            bool updatedAutoStart = EditorGUILayout.Toggle("Auto Start Local ComfyUI", settings.comfyAutoStart);
            if (EditorGUI.EndChangeCheck())
            {
                settings.comfyAutoStart = updatedAutoStart;
                PersistBackendSettings(settings);
            }
        }
        else
        {
            DrawColabNotebookControls(settings);
        }

        string workflowPath = string.IsNullOrWhiteSpace(settings.comfyWorkflowTemplatePath)
            ? "(none)"
            : settings.comfyWorkflowTemplatePath;
        string summary = activePreset == BackendPreset.Colab
            ? $"Using Colab FastAPI proxy at {settings.remoteUrl}\nNotebook: {GetConfiguredColabNotebookUrl(settings)}\nWorkflow template: {workflowPath}"
            : $"Using local ComfyUI API at {settings.comfyBaseUrl}\nAuto start: {(settings.comfyAutoStart ? "enabled" : "disabled")}\nWorkflow template: {workflowPath}";
        EditorGUILayout.HelpBox(summary, MessageType.None);
    }

    private static void DrawColabNotebookControls(BackendSettings settings)
    {
        EditorGUI.BeginChangeCheck();
        string updatedNotebookPath = EditorGUILayout.TextField("Colab Notebook", GetConfiguredColabNotebookPath(settings));
        if (EditorGUI.EndChangeCheck())
        {
            settings.colabNotebookPath = NormalizePath(updatedNotebookPath);
            PersistBackendSettings(settings);
        }

        EditorGUI.BeginChangeCheck();
        string updatedNotebookUrl = EditorGUILayout.TextField("Colab Web URL", GetConfiguredColabNotebookUrl(settings));
        if (EditorGUI.EndChangeCheck())
        {
            settings.colabNotebookUrl = NormalizeBaseUrl(updatedNotebookUrl, DefaultColabNotebookUrl);
            PersistBackendSettings(settings);
        }

        if (GUILayout.Button("Open Colab in Browser"))
        {
            Application.OpenURL(GetConfiguredColabNotebookUrl(settings));
        }

        if (GUILayout.Button("Open Local Notebook File"))
        {
            OpenLocalColabNotebook(settings);
        }

        if (GUILayout.Button("I'm Back - Check Colab Backend"))
        {
            _ = CheckBackendHealthAsync(true);
        }

        EditorGUILayout.HelpBox(
            "Flow: open Colab in the browser, choose a GPU runtime there, run all notebook cells until the reserved zrok share is serving the FastAPI proxy, then return to Unity and use \"I'm Back - Check Colab Backend\". When health passes, Generate and refinement can run from Unity.",
            MessageType.Info);
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

        RefinementController controller = FindFirstObjectByType<RefinementController>();
        EditorGUILayout.HelpBox(
            "Select a region in the Scene view, describe the local change, then send RGB/depth/mask inputs to the refinement backend.",
            MessageType.Info);

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

        RegionSelection cs = selectionManager?.CurrentSelection;
        string selectionSummary = cs == null
            ? "No active region selection."
            : $"Selection: center={cs.center}, size={cs.size}";
        if (cs != null
            && RegionSelectionManager.TryGetGeneratedContentMeshBounds(out Bounds gm)
            && RegionSelectionManager.SelectionWorldAabbSpansMostOfMesh(cs, gm))
        {
            EditorGUILayout.HelpBox(
                selectionSummary
                + " The cube encloses almost the entire generated mesh on multiple axes — "
                + "refinement masks will trace most of the object. Resize handles in Scene view "
                + "or Reset Selection for a tighter default centered on GeneratedContent.",
                MessageType.Warning);
        }
        else
            EditorGUILayout.HelpBox(selectionSummary, MessageType.None);

        if (GUILayout.Button("Reset Selection"))
        {
            Undo.RecordObject(selectionManager, "Reset Region Selection");
            if (!selectionManager.TryInitializeFromGeneratedMeshBounds())
            {
                if (!selectionManager.TryInitializeFromSceneGeometry(0.4f))
                    selectionManager.BeginSelection();
            }
            selectionManager.ConfirmSelection();
            EditorUtility.SetDirty(selectionManager);
            SceneView.RepaintAll();
        }

        bool refinementNeedsInteractiveSelection = selectionManager.CurrentSelection == null;
        using (new EditorGUI.DisabledScope(selectionManager == null || refinementNeedsInteractiveSelection || controller.IsRunning))
        {
            if (GUILayout.Button(controller.IsRunning ? "Refining..." : "Refine Selected Region"))
            {
                controller.RunMultiViewRefinement(_globalStylePrompt, _localRefinementPrompt);

                string selectionLogId = selectionManager.CurrentSelection?.selectionId ?? "unknown";
                InteractionLogger.Log(new InteractionEvent
                {
                    type = "refine_region",
                    extra = $"selection={selectionLogId}, global_prompt={_globalStylePrompt}, local_prompt={_localRefinementPrompt}"
                });
            }
        }

        if (controller.IsRunning)
        {
            if (GUILayout.Button("Clear refining status"))
            {
                Undo.RecordObject(controller, "Clear refining status");
                controller.ClearRefiningRunningState();
                EditorUtility.SetDirty(controller);
            }
            EditorGUILayout.HelpBox(
                "If refinement finished or was interrupted but the button still shows Refining..., clear the busy flag here.",
                MessageType.None);
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

    private static async Task CheckBackendHealthAsync(bool showDialog)
    {
        BackendSettings settings = BackendRegistry.Settings;
        string baseUrl = string.IsNullOrWhiteSpace(settings.comfyBaseUrl)
            ? settings.remoteUrl?.Replace("/generate", string.Empty)
            : settings.comfyBaseUrl;

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            string message = "Spatial Generation: No backend base URL is configured.";
            Debug.LogError(message);
            if (showDialog)
                EditorUtility.DisplayDialog("Colab Backend Not Ready", message, "OK");
            return;
        }

        string healthUrl = $"{baseUrl.TrimEnd('/')}/health";

        try
        {
            using HttpResponseMessage response = await Http.GetAsync(healthUrl);
            string body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                string message = $"Spatial Generation backend health OK: {healthUrl}\n{body}";
                Debug.Log(message);
                if (showDialog)
                {
                    EditorUtility.DisplayDialog(
                        "Colab Backend Ready",
                        "Colab is reachable from Unity. You can now use Generate or refinement from the Spatial Generation window.",
                        "OK");
                }
                return;
            }

            string failureMessage = $"Spatial Generation backend health failed: {(int)response.StatusCode} {response.ReasonPhrase}\n{healthUrl}\n{body}";
            Debug.LogError(failureMessage);
            if (showDialog)
            {
                EditorUtility.DisplayDialog(
                    "Colab Backend Not Ready",
                    $"Unity reached the Colab URL, but health failed.\n\n{healthUrl}\n\nKeep the Colab notebook running and wait for the FastAPI/zrok cells to finish, then try again.",
                    "OK");
            }
        }
        catch (Exception ex)
        {
            string failureMessage = $"Spatial Generation backend health request failed: {healthUrl}\n{ex.Message}";
            Debug.LogError(failureMessage);
            if (showDialog)
            {
                EditorUtility.DisplayDialog(
                    "Colab Backend Not Ready",
                    $"Unity could not reach the Colab backend yet.\n\n{healthUrl}\n\nRun the notebook in Colab until the FastAPI proxy and zrok share are active, then come back and check again.",
                    "OK");
            }
        }
    }

    private void RefreshWorkflowTemplateOptions()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string graphDirectory = Path.Combine(projectRoot, "tools", "graphs");
        var options = new List<string>();

        if (Directory.Exists(graphDirectory))
        {
            string[] files = Directory.GetFiles(graphDirectory, "*.json", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Length; i++)
            {
                string relativePath = MakeProjectRelativePath(projectRoot, files[i]);
                if (!string.IsNullOrWhiteSpace(relativePath))
                    options.Add(relativePath);
            }
        }

        if (options.Count == 0)
            options.Add("tools/graphs/generation.json");

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

    private static BackendPreset GetCurrentPreset(BackendSettings settings)
    {
        if (settings == null)
            return BackendPreset.LocalComfyApi;

        // Preserve older assets that predate the serialized preset field.
        if (settings.backendPreset == BackendPreset.LocalComfyApi &&
            !string.IsNullOrWhiteSpace(settings.comfyBaseUrl) &&
            !IsLocalUrl(settings.comfyBaseUrl))
        {
            return BackendPreset.Colab;
        }

        return settings.backendPreset;
    }

    private static void ApplyBackendPreset(BackendSettings settings, BackendPreset preset)
    {
        settings.backendKind = BackendKind.RemoteHttp;
        settings.backendPreset = preset;
        EnsureColabNotebookSettings(settings);

        switch (preset)
        {
            case BackendPreset.LocalComfyApi:
                // Local setup: ComfyUI on :8188, FastAPI router on :8001.
                // Refinement (/refine) is served only by the router, so
                // remoteUrl must point at the router's /generate, not be empty.
                settings.comfyBaseUrl = GetConfiguredLocalBaseUrl(settings);
                settings.remoteUrl = $"{DefaultLocalRouterBaseUrl.TrimEnd('/')}/generate";
                settings.comfyWsUrl = BuildWebSocketUrl(settings.comfyBaseUrl);
                break;

            case BackendPreset.Colab:
                // Colab setup: ComfyUI and FastAPI router both served from
                // the tunnelled base URL.
                settings.comfyBaseUrl = GetConfiguredColabBaseUrl(settings);
                settings.remoteUrl = $"{settings.comfyBaseUrl.TrimEnd('/')}/generate";
                settings.comfyWsUrl = string.Empty;
                settings.comfyAutoStart = false;
                break;
        }
    }

    private static void ApplyEndpointOverride(BackendSettings settings, BackendPreset preset, string baseUrl)
    {
        settings.backendPreset = preset;
        string normalizedBaseUrl = NormalizeBaseUrl(baseUrl, preset == BackendPreset.Colab ? DefaultColabBaseUrl : DefaultLocalComfyBaseUrl);
        settings.comfyBaseUrl = normalizedBaseUrl;

        if (preset == BackendPreset.Colab)
        {
            settings.remoteUrl = $"{normalizedBaseUrl.TrimEnd('/')}/generate";
            settings.comfyWsUrl = string.Empty;
            settings.comfyAutoStart = false;
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
        if (!string.IsNullOrWhiteSpace(settings?.remoteUrl) &&
            Uri.TryCreate(settings.remoteUrl, UriKind.Absolute, out Uri remoteUri) &&
            !IsLocalUrl($"{remoteUri.Scheme}://{remoteUri.Authority}"))
        {
            return $"{remoteUri.Scheme}://{remoteUri.Authority}";
        }

        if (!string.IsNullOrWhiteSpace(settings?.comfyBaseUrl) && !IsLocalUrl(settings.comfyBaseUrl))
            return NormalizeBaseUrl(settings.comfyBaseUrl, DefaultColabBaseUrl);

        return DefaultColabBaseUrl;
    }

    private static string GetConfiguredColabNotebookPath(BackendSettings settings)
    {
        EnsureColabNotebookSettings(settings);
        return settings.colabNotebookPath;
    }

    private static string GetConfiguredColabNotebookUrl(BackendSettings settings)
    {
        EnsureColabNotebookSettings(settings);
        return settings.colabNotebookUrl;
    }

    private static void EnsureColabNotebookSettings(BackendSettings settings)
    {
        if (settings == null)
            return;

        if (string.IsNullOrWhiteSpace(settings.colabNotebookPath))
            settings.colabNotebookPath = DefaultColabNotebookPath;
        if (string.IsNullOrWhiteSpace(settings.colabNotebookUrl))
            settings.colabNotebookUrl = DefaultColabNotebookUrl;
    }

    private static void OpenLocalColabNotebook(BackendSettings settings)
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string notebookPath = GetConfiguredColabNotebookPath(settings);
        string absolutePath = Path.IsPathRooted(notebookPath)
            ? notebookPath
            : Path.Combine(projectRoot, notebookPath);

        if (!File.Exists(absolutePath))
        {
            EditorUtility.DisplayDialog(
                "Colab Notebook Not Found",
                $"Could not find the configured Colab notebook:\n{absolutePath}",
                "OK");
            return;
        }

        EditorUtility.OpenWithDefaultApp(absolutePath);
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
        if (!selectionManager.TryInitializeFromGeneratedMeshBounds())
        {
            if (!selectionManager.TryInitializeFromSceneGeometry(0.4f))
                selectionManager.BeginSelection();
        }
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
