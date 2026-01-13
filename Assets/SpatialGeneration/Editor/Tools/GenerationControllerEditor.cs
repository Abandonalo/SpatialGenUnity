using UnityEditor;
using UnityEngine;

public static class GenerationControllerEditor
{
    private const string GeneratedRootName = "GeneratedContent";

    public static void RegenerateFromIntent(SceneIntent intent)
    {
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Regenerate (Mock)");

        // Ensure root exists (Undo-aware)
        GameObject root = GameObject.Find(GeneratedRootName);
        if (root == null)
        {
            root = new GameObject(GeneratedRootName);
            Undo.RegisterCreatedObjectUndo(root, "Create GeneratedContent Root");
        }

        // Cleanup previous generated children (Undo-aware)
        ClearChildrenUndo(root);

        // Generate and apply (Undo-aware)
        var backend = new MockGenerationBackend();
        GenerationResult result = backend.Generate(intent);

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

        // Option A: remove children only (keep root)
        ClearChildrenUndo(root);

        // Option B (optional): delete root too
        // Undo.DestroyObjectImmediate(root);

        Undo.CollapseUndoOperations(group);
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
        // Unity cylinder is 2 units tall in local space, so halve Y to match desired height
        return type == PrimitiveType.Cylinder
            ? new Vector3(desiredBounds.x, desiredBounds.y * 0.5f, desiredBounds.z)
            : desiredBounds;
    }
}
