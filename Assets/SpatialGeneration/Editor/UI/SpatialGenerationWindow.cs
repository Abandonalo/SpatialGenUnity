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
            SceneIntent intent = SceneIntentBuilder.Build();
            GenerationControllerEditor.RegenerateFromIntent(intent);
        }

        if (GUILayout.Button("Cleanup GeneratedContent"))
        {
            GenerationControllerEditor.CleanupGeneratedContent();
        }


    }
}
