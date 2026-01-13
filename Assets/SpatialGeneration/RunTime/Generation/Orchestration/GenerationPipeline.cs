using UnityEngine;

public static class GenerationPipeline
{
    private static IGenerationBackend backend = new MockGenerationBackend();
    private const string GeneratedRootName = "GeneratedContent";

    public static void Run(SceneIntent intent)
    {
        GenerationResult result = backend.Generate(intent);
        ApplyResultToScene(result);
    }

    private static void ApplyResultToScene(GenerationResult result)
    {
        // Find or create parent
        GameObject root = GameObject.Find(GeneratedRootName);
        if (root == null)
            root = new GameObject(GeneratedRootName);

        // Clear previous generated content
        for (int i = root.transform.childCount - 1; i >= 0; i--)
        {
            Object.DestroyImmediate(root.transform.GetChild(i).gameObject);
        }

        // Create new primitives
        foreach (var obj in result.objects)
        {
            GameObject go = GameObject.CreatePrimitive(obj.primitiveType);
            go.transform.SetParent(root.transform, worldPositionStays: true);
            go.transform.position = obj.position;

            go.transform.localScale = AdjustScaleForPrimitive(
                obj.primitiveType,
                obj.size
            );

            go.name = $"Generated_{obj.primitiveType}";
        }
    }

    private static Vector3 AdjustScaleForPrimitive(
        PrimitiveType type,
        Vector3 desiredBounds
    )
    {
        // Unity cylinder mesh is 2 units tall by default
        return type == PrimitiveType.Cylinder
            ? new Vector3(
                desiredBounds.x,
                desiredBounds.y * 0.5f,
                desiredBounds.z
              )
            : desiredBounds;
    }
}
