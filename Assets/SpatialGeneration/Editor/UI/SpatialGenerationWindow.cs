using UnityEditor;
using UnityEngine;
using System;
using System.IO;

public class SpatialGenerationWindow : EditorWindow
{
    private const string GlobalStylePromptPrefsKey = "SpatialGenerationWindow.GlobalStylePrompt";
    private const string GlobalNegativeStylePromptPrefsKey = "SpatialGenerationWindow.GlobalNegativeStylePrompt";
    private string _globalStylePrompt = string.Empty;
    private string _globalNegativeStylePrompt = string.Empty;

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
    }

    private void OnGUI()
    {
        GUILayout.Label("Spatial Generation", EditorStyles.boldLabel);

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
            string combinedPrompt = ComposePrompt(BackendRegistry.Settings?.prompt, _globalStylePrompt);
            string combinedNegativePrompt = ComposePrompt(BackendRegistry.Settings?.negativePrompt, _globalNegativeStylePrompt);

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
}
