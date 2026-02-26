using UnityEditor;
using UnityEngine;
using System;
using System.IO;

public class SpatialGenerationWindow : EditorWindow
{
    [MenuItem("Tools/Spatial Generation")]
    public static void Open()
    {
        var window = GetWindow<SpatialGenerationWindow>();
        window.titleContent = new GUIContent("Spatial Generation");
        window.Show();
    }

    private void OnGUI()
    {
        GUILayout.Label("Spatial Generation", EditorStyles.boldLabel);

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

            // If you're using the Undoable controller:
            GenerationControllerEditor.RegenerateFromIntent(intent);

            // Also log a clean generate event (optional)
            InteractionLogger.Log(new InteractionEvent
            {
                type = "generate",
                extra = $"proxies={intent.spatialProxies.Count}, intent_json={snapshotPath}"
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
}
