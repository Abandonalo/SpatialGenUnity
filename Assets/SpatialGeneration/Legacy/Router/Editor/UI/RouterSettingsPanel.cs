using System.Threading.Tasks;
using SpatialGeneration.Generation.Backends;
using SpatialGeneration.UI;
using UnityEditor;
using UnityEngine;

namespace SpatialGeneration.Generation.Authoring
{
    /// <summary>Router, ComfyUI and Colab settings in the Researcher Tools window.</summary>
    [InitializeOnLoad]
    internal static class RouterSettingsPanel
    {
        private static readonly GUIContent GenerationModelLabel = new(
            "Asset Generation Model",
            "Creates a complete 3D asset for a spatial proxy.");
        private static readonly GUIContent RefinementLifterLabel = new(
            "Region Refinement Lifter",
            "Reconstructs only the selected refinement region. Hunyuan uses four views; " +
            "TripoSR lifts the refined Front view.");

        static RouterSettingsPanel()
        {
            SpatialGenerationResearcherWindow.RouterSettingsGUI = DrawRouterSettings;
        }

        private static void DrawRouterSettings(BackendSettings settings)
        {
            EditorGUI.BeginChangeCheck();
            BackendPreset preset = (BackendPreset)EditorGUILayout.EnumPopup("Preset", settings.backendPreset);
            string routerUrl = preset == BackendPreset.Colab
                ? EditorGUILayout.TextField("Colab Router URL", settings.colabRouterUrl)
                : EditorGUILayout.TextField("Local Router URL", settings.localRouterUrl);
            GenerationModel model =
                (GenerationModel)EditorGUILayout.EnumPopup(GenerationModelLabel, settings.generationModel);
            RefinementLifter refinementLifter =
                (RefinementLifter)EditorGUILayout.EnumPopup(RefinementLifterLabel, settings.refinementLifter);

            if (EditorGUI.EndChangeCheck())
            {
                settings.backendPreset = preset;
                settings.generationModel = model;
                settings.refinementLifter = refinementLifter;
                if (preset == BackendPreset.Colab)
                    settings.colabRouterUrl = BackendSettings.NormalizeOrigin(routerUrl);
                else
                    settings.localRouterUrl = BackendSettings.NormalizeOrigin(routerUrl);
                SpatialGenerationResearcherWindow.Persist(settings);
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
                SpatialGenerationResearcherWindow.Persist(settings);
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
                SpatialGenerationResearcherWindow.Persist(settings);
            }

            if (GUILayout.Button("Open Notebook in Colab"))
                Application.OpenURL(settings.colabNotebookUrl);

            EditorGUILayout.HelpBox(
                "Open the notebook, pick a GPU runtime, and run every cell until the zrok share is serving " +
                "the router. Then check backend health here before generating.",
                MessageType.Info);
        }

        private static async Task CheckHealthAsync(BackendSettings settings, bool showDialog)
        {
            RouterClient.BackendHealth health = await RouterClient.CheckHealthAsync(settings);
            if (health.IsReady)
            {
                Debug.Log($"Spatial Generation: backend ready at {settings.RouterBaseUrl} (router + ComfyUI).");
                if (settings.refinementLifter != RefinementLifter.TripoSR &&
                    !health.HunyuanRefinementAvailable)
                {
                    Debug.LogWarning(
                        "Spatial Generation: Hunyuan3D-2mv refinement is unavailable. " +
                        (string.IsNullOrWhiteSpace(health.HunyuanRefinementDetail)
                            ? "Run ./tools/setup_hunyuan2mv.sh and restart ComfyUI."
                            : health.HunyuanRefinementDetail));
                }
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
    }
}
