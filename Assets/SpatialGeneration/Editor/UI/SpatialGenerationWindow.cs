using UnityEditor;
using UnityEngine;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

public class SpatialGenerationWindow : EditorWindow
{
    private const string GlobalStylePromptPrefsKey = "SpatialGenerationWindow.GlobalStylePrompt";
    private const string GlobalNegativeStylePromptPrefsKey = "SpatialGenerationWindow.GlobalNegativeStylePrompt";
    private static readonly HttpClient Http = new();
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
}
