using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SpatialGeneration.Generation.Backends;

/// <summary>
/// The single authoring surface: configure the backend, place proxies, generate, then refine
/// a region. Prompts persist in EditorPrefs so they survive domain reloads during a session.
/// </summary>
public class SpatialGenerationWindow : EditorWindow
{
    private const string StylePromptKey = "SpatialGeneration.StylePrompt";
    private const string NegativePromptKey = "SpatialGeneration.NegativePrompt";
    private const string RefinementPromptKey = "SpatialGeneration.RefinementPrompt";

    private string _stylePrompt = string.Empty;
    private string _negativePrompt = string.Empty;
    private string _refinementPrompt = string.Empty;
    private Vector2 _scroll;

    [MenuItem("Tools/Spatial Generation")]
    public static void Open()
    {
        SpatialGenerationWindow window = GetWindow<SpatialGenerationWindow>();
        window.titleContent = new GUIContent("Spatial Generation");
        window.Show();
    }

    private void OnEnable()
    {
        _stylePrompt = EditorPrefs.GetString(StylePromptKey, string.Empty);
        _negativePrompt = EditorPrefs.GetString(NegativePromptKey, string.Empty);
        _refinementPrompt = EditorPrefs.GetString(RefinementPromptKey, string.Empty);
    }

    private void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        DrawBackendSection();
        EditorGUILayout.Space(10);
        DrawCreationSection();
        EditorGUILayout.Space(10);
        DrawRefinementSection();
        EditorGUILayout.Space(10);
        DrawUtilitiesSection();

        EditorGUILayout.EndScrollView();
    }

    private void DrawBackendSection()
    {
        BackendSettings settings = BackendRegistry.Settings;
        if (settings == null)
            return;

        GUILayout.Label("Backend", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();
        BackendPreset preset = (BackendPreset)EditorGUILayout.EnumPopup("Preset", settings.backendPreset);
        string routerUrl = preset == BackendPreset.Colab
            ? EditorGUILayout.TextField("Colab Router URL", settings.colabRouterUrl)
            : EditorGUILayout.TextField("Local Router URL", settings.localRouterUrl);
        GenerationModel model = (GenerationModel)EditorGUILayout.EnumPopup("3D Model", settings.generationModel);

        if (EditorGUI.EndChangeCheck())
        {
            settings.backendPreset = preset;
            settings.generationModel = model;
            if (preset == BackendPreset.Colab)
                settings.colabRouterUrl = BackendSettings.NormalizeOrigin(routerUrl);
            else
                settings.localRouterUrl = BackendSettings.NormalizeOrigin(routerUrl);
            Persist(settings);
        }

        if (preset == BackendPreset.Colab)
            DrawColabControls(settings);
        else
            DrawLocalControls(settings);

        if (GUILayout.Button("Check Backend Health"))
            _ = CheckHealthAsync(settings, showDialog: preset == BackendPreset.Colab);

        EditorGUILayout.HelpBox($"Requests go to {settings.RouterBaseUrl}", MessageType.None);
    }

    private static void DrawLocalControls(BackendSettings settings)
    {
        EditorGUI.BeginChangeCheck();
        bool autoStart = EditorGUILayout.Toggle("Auto Start ComfyUI", settings.autoStartComfy);
        if (EditorGUI.EndChangeCheck())
        {
            settings.autoStartComfy = autoStart;
            Persist(settings);
        }

        EditorGUILayout.HelpBox(
            autoStart
                ? "Generate starts ComfyUI itself if it is not already running. The router is " +
                  "separate: start it with ./tools/start_backend.sh."
                : "ComfyUI must already be running. Enable Auto Start to have Generate launch it.",
            MessageType.None);
    }

    private static void DrawColabControls(BackendSettings settings)
    {
        EditorGUI.BeginChangeCheck();
        string notebookUrl = EditorGUILayout.TextField("Notebook URL", settings.colabNotebookUrl);
        if (EditorGUI.EndChangeCheck())
        {
            settings.colabNotebookUrl = notebookUrl;
            Persist(settings);
        }

        if (GUILayout.Button("Open Notebook in Colab"))
            Application.OpenURL(settings.colabNotebookUrl);

        EditorGUILayout.HelpBox(
            "Open the notebook, pick a GPU runtime, and run every cell until the zrok share is serving " +
            "the router. Then check backend health here before generating.",
            MessageType.Info);
    }

    private void DrawCreationSection()
    {
        GUILayout.Label("Creation", EditorStyles.boldLabel);

        _stylePrompt = DrawPersistentTextArea("Style prompt", _stylePrompt, StylePromptKey);
        EditorGUILayout.HelpBox("Appended to every proxy's own prompt so all assets share one style.", MessageType.None);

        _negativePrompt = DrawPersistentTextArea("Negative prompt", _negativePrompt, NegativePromptKey);

        if (GUILayout.Button("Add Spatial Proxy"))
            SpatialProxyFactory.CreateProxy();

        if (!GUILayout.Button("Generate"))
            return;

        GenerationRunner.Generate(_stylePrompt, _negativePrompt);
        InteractionLogger.Log(new InteractionEvent
        {
            type = "generate",
            extra = $"style_prompt={_stylePrompt}, negative_prompt={_negativePrompt}"
        });
    }

    private void DrawRefinementSection()
    {
        GUILayout.Label("Local Region Refinement", EditorStyles.boldLabel);

        RefinementController controller = FindFirstObjectByType<RefinementController>();
        if (controller == null)
        {
            EditorGUILayout.HelpBox(
                "Create the refinement rig to select a region and run local edits.", MessageType.Info);
            if (GUILayout.Button("Setup Refinement Rig"))
                Selection.activeGameObject = CreateRefinementRig().gameObject;
            return;
        }

        RegionSelectionManager selectionManager = controller.selectionManager != null
            ? controller.selectionManager
            : controller.GetComponent<RegionSelectionManager>();

        if (selectionManager == null)
        {
            EditorGUILayout.HelpBox("The rig is missing its RegionSelectionManager. Recreate it.", MessageType.Error);
            return;
        }

        controller.selectionManager = selectionManager;
        DrawSelectionSummary(selectionManager);

        if (GUILayout.Button("Reset Selection"))
        {
            Undo.RecordObject(selectionManager, "Reset Region Selection");
            selectionManager.ResetToDefault();
            EditorUtility.SetDirty(selectionManager);
            SceneView.RepaintAll();
        }

        _refinementPrompt = DrawPersistentTextArea("What should change here?", _refinementPrompt, RefinementPromptKey);

        bool canRefine = selectionManager.CurrentSelection != null
                         && !controller.IsRunning
                         && !string.IsNullOrWhiteSpace(_refinementPrompt);

        using (new EditorGUI.DisabledScope(!canRefine))
        {
            if (GUILayout.Button(controller.IsRunning ? "Refining…" : "Refine Selected Region"))
            {
                controller.RunRefinement(_refinementPrompt);
                InteractionLogger.Log(new InteractionEvent
                {
                    type = "refine_region",
                    extra = $"selection={selectionManager.CurrentSelection?.selectionId}, prompt={_refinementPrompt}"
                });
            }
        }

        if (!controller.IsRunning)
            return;

        if (GUILayout.Button("Clear busy flag"))
        {
            Undo.RecordObject(controller, "Clear refining status");
            controller.ClearRunningState();
            EditorUtility.SetDirty(controller);
        }

        EditorGUILayout.HelpBox(
            "Use this if a run was interrupted and the button is still stuck on Refining.", MessageType.None);
    }

    private static void DrawSelectionSummary(RegionSelectionManager selectionManager)
    {
        RegionSelection selection = selectionManager.CurrentSelection;
        if (selection == null)
        {
            EditorGUILayout.HelpBox("No region selected. Use Reset Selection, then drag the handles in the Scene view.",
                MessageType.Info);
            return;
        }

        string summary = $"Region: center {selection.center}, size {selection.size}";

        if (RegionSelectionManager.TryGetGeneratedContentBounds(out Bounds meshBounds) &&
            RegionSelectionManager.SelectionSpansMostOfMesh(selection, meshBounds))
        {
            EditorGUILayout.HelpBox(
                $"{summary}\n\nThe box covers most of the asset on several axes, so the edit will not be local — " +
                "nearly the whole mesh gets replaced. Shrink it, or use Reset Selection for a tighter default.",
                MessageType.Warning);
            return;
        }

        EditorGUILayout.HelpBox(summary, MessageType.None);
    }

    private void DrawUtilitiesSection()
    {
        GUILayout.Label("Utilities", EditorStyles.boldLabel);

        if (GUILayout.Button("Cleanup GeneratedContent"))
            GenerationRunner.CleanupGeneratedContent();

        if (GUILayout.Button("Open Interaction Log Folder"))
            InteractionLogger.RevealLogFolder();
    }

    private static string DrawPersistentTextArea(string label, string value, string prefsKey)
    {
        GUILayout.Label(label, EditorStyles.label);
        EditorGUI.BeginChangeCheck();
        string updated = EditorGUILayout.TextArea(value, GUILayout.MinHeight(48f));
        if (EditorGUI.EndChangeCheck())
            EditorPrefs.SetString(prefsKey, updated);
        return updated;
    }

    private static async Task CheckHealthAsync(BackendSettings settings, bool showDialog)
    {
        RouterClient.BackendHealth health = await RouterClient.CheckHealthAsync(settings);
        if (health.IsReady)
        {
            Debug.Log($"Spatial Generation: backend ready at {settings.RouterBaseUrl} (router + ComfyUI).");
            if (showDialog)
                EditorUtility.DisplayDialog(
                    "Backend Ready", "The router and ComfyUI are both up. You can generate and refine.", "OK");
            return;
        }

        string problem = RouterClient.DescribeProblem(settings, health);
        Debug.LogError($"Spatial Generation: {problem}");
        if (showDialog)
            EditorUtility.DisplayDialog("Backend Not Ready", problem, "OK");
    }

    private static RefinementController CreateRefinementRig()
    {
        RefinementController existing = FindFirstObjectByType<RefinementController>();
        if (existing != null)
            return existing;

        var root = new GameObject("SpatialGenerationRefinement");
        Undo.RegisterCreatedObjectUndo(root, "Create Refinement Rig");

        RegionSelectionManager selectionManager = root.AddComponent<RegionSelectionManager>();
        RefinementController controller = root.AddComponent<RefinementController>();
        controller.selectionManager = selectionManager;
        selectionManager.ResetToDefault();

        EditorUtility.SetDirty(root);
        EditorSceneManager.MarkSceneDirty(root.scene);
        return controller;
    }

    private static void Persist(BackendSettings settings)
    {
        EditorUtility.SetDirty(settings);
        AssetDatabase.SaveAssets();
        BackendRegistry.Reload();
    }
}
