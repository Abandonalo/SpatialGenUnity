using UnityEditor;
using UnityEngine;

public class SpatialGenerationWindow : EditorWindow
{
    [MenuItem("Tools/Spatial Generation")]
    public static void Open()
    {
        GetWindow<SpatialGenerationWindow>("Spatial Generation");
    }

    private void OnGUI()
    {
        GUILayout.Label("Spatial Generation", EditorStyles.boldLabel);

        if (GUILayout.Button("Add Spatial Proxy"))
        {
            SpatialProxyFactory.CreateProxy();
        }

        if (GUILayout.Button("Log Scene Intent"))
        {
            SceneIntent intent = SceneIntentBuilder.Build();
            Debug.Log(intent.ToJson());
        }

        if (GUILayout.Button("Generate (Mock, Undoable)"))
        {
            InteractionLogger.Log(new InteractionEvent
            {
                type = "generate",
                proxy_id = "",
                extra = $"proxies={SceneIntentBuilder.Build().spatialProxies.Count}"
            });

            SceneIntent intent = SceneIntentBuilder.Build();
            GenerationControllerEditor.RegenerateFromIntent(intent);
        }

        if (GUILayout.Button("Cleanup GeneratedContent"))
        {
            InteractionLogger.Log(new InteractionEvent
            {
                type = "cleanup",
                proxy_id = "",
                extra = $"proxies={SceneIntentBuilder.Build().spatialProxies.Count}"
            });

            GenerationControllerEditor.CleanupGeneratedContent();
        }


    }
}
