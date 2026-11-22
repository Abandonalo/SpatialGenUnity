using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Brings backend output files into the project and instantiates them.
///
/// Backend meshes land outside <c>Assets/</c>, so they have to be staged in before Unity will
/// import them. Each run gets its own folder to avoid name collisions between runs.
/// </summary>
public static class MeshImporter
{
    private const string GeneratedRoot = "Assets/SpatialGeneration/Generated";

    /// <summary>
    /// Stages <paramref name="absolutePath"/> (and its sibling files) into the project,
    /// imports it, and instantiates it under <paramref name="parent"/>.
    /// </summary>
    /// <param name="preferVertexColors">
    /// TripoSR bakes albedo into vertex colors and glTF PBR shaders render those meshes
    /// washed out, so refined meshes ask for the unlit vertex-color shader instead.
    /// </param>
    public static bool TryInstantiate(
        string absolutePath,
        Transform parent,
        bool preferVertexColors,
        out GameObject instance,
        out string assetPath)
    {
        instance = null;
        assetPath = StageIntoProject(absolutePath);
        if (string.IsNullOrWhiteSpace(assetPath))
            return false;

        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

        var meshAsset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        if (meshAsset == null)
        {
            UnityEngine.Object loaded = AssetDatabase.LoadMainAssetAtPath(assetPath);
            Debug.LogWarning(
                $"Spatial Generation: imported '{assetPath}' but its main asset is " +
                $"'{loaded?.GetType().Name ?? "null"}', not a GameObject. Install an importer for this format.");
            return false;
        }

        instance = PrefabUtility.InstantiatePrefab(meshAsset) as GameObject;
        if (instance == null)
            return false;

        instance.transform.SetParent(parent, worldPositionStays: false);
        instance.transform.localPosition = Vector3.zero;

        bool hasVertexColors = HasVertexColors(instance);
        ReplaceUnsupportedMaterials(instance, hasVertexColors);
        if (preferVertexColors && hasVertexColors)
            ApplyVertexColorMaterials(instance);

        return true;
    }

    public static bool TryInstantiate(string absolutePath, Transform parent, bool preferVertexColors, out GameObject instance) =>
        TryInstantiate(absolutePath, parent, preferVertexColors, out instance, out _);

    /// <summary>
    /// Copies the asset into a fresh run folder under <see cref="GeneratedRoot"/>.
    ///
    /// Only the files this asset needs, not the whole source directory. Copying the
    /// directory meant every previously downloaded mesh was duplicated into every new run
    /// folder and re-imported, so import cost grew with the number of runs rather than
    /// with the size of the new asset.
    /// </summary>
    private static string StageIntoProject(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) || !File.Exists(absolutePath))
            return null;

        EnsureFolder(GeneratedRoot);
        string folderName = $"run_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}";
        AssetDatabase.CreateFolder(GeneratedRoot, folderName);
        string destinationFolder = $"{GeneratedRoot}/{folderName}";

        foreach (string sourceFile in FilesRequiredBy(absolutePath))
        {
            string destination = $"{destinationFolder}/{Path.GetFileName(sourceFile)}";
            File.Copy(sourceFile, destination, overwrite: true);
        }

        return $"{destinationFolder}/{Path.GetFileName(absolutePath)}";
    }

    /// <summary>
    /// The asset plus its companion files.
    ///
    /// .glb and .fbx embed everything. .obj may sit beside an .mtl and textures, which
    /// conventionally share its name. .gltf references buffers and images by arbitrary
    /// filename, so there the whole directory is the only safe answer.
    /// </summary>
    private static IEnumerable<string> FilesRequiredBy(string absolutePath)
    {
        string directory = Path.GetDirectoryName(absolutePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return Array.Empty<string>();

        string extension = Path.GetExtension(absolutePath).ToLowerInvariant();
        IEnumerable<string> candidates = extension == ".gltf"
            ? Directory.GetFiles(directory)
            : Directory.GetFiles(directory, $"{Path.GetFileNameWithoutExtension(absolutePath)}.*");

        // .meta files carry GUIDs; copying them in would collide with the fresh import.
        return candidates.Where(file => !file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Saves a runtime-built mesh next to an imported asset so it survives a reload.</summary>
    public static Mesh PersistMesh(Mesh mesh, string siblingAssetPath, string fileName)
    {
        if (mesh == null || string.IsNullOrWhiteSpace(siblingAssetPath))
            return mesh;

        string folder = Path.GetDirectoryName(siblingAssetPath)?.Replace("\\", "/");
        if (string.IsNullOrWhiteSpace(folder))
            return mesh;

        string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{fileName}.asset");
        AssetDatabase.CreateAsset(mesh, path);
        return AssetDatabase.LoadAssetAtPath<Mesh>(path);
    }

    public static void EnsureFolder(string assetPath)
    {
        if (AssetDatabase.IsValidFolder(assetPath))
            return;

        string parent = Path.GetDirectoryName(assetPath)?.Replace("\\", "/");
        string name = Path.GetFileName(assetPath);
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(name))
            return;

        EnsureFolder(parent);
        if (!AssetDatabase.IsValidFolder(assetPath))
            AssetDatabase.CreateFolder(parent, name);
    }

    public static string ToProjectRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string root = projectRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);

        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? full[root.Length..].Replace("\\", "/")
            : path;
    }

    public static bool HasVertexColors(GameObject root)
    {
        foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
        {
            Mesh mesh = filter.sharedMesh;
            if (mesh != null && mesh.colors is { Length: > 0 })
                return true;
        }

        return false;
    }

    private static void ReplaceUnsupportedMaterials(GameObject root, bool preferVertexColor)
    {
        Shader fallback = ResolveFallbackShader(preferVertexColor);
        if (fallback == null)
            return;

        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            Material[] materials = renderer.sharedMaterials;
            bool dirty = false;

            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material != null && material.shader != null && material.shader.isSupported)
                    continue;

                materials[i] = new Material(fallback) { name = "Generated_FallbackMaterial" };
                dirty = true;
            }

            if (dirty)
                renderer.sharedMaterials = materials;
        }
    }

    private static void ApplyVertexColorMaterials(GameObject root)
    {
        Shader shader = Shader.Find("SpatialGeneration/VertexColorUnlit");
        if (shader == null)
            return;

        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            Material[] materials = renderer.sharedMaterials;
            if (materials == null || materials.Length == 0)
                continue;

            var replacements = new Material[materials.Length];
            for (int i = 0; i < replacements.Length; i++)
                replacements[i] = new Material(shader) { name = "Generated_VertexColorUnlit" };

            renderer.sharedMaterials = replacements;
        }
    }

    private static Shader ResolveFallbackShader(bool preferVertexColor)
    {
        if (preferVertexColor)
        {
            Shader vertexColor = Shader.Find("SpatialGeneration/VertexColorUnlit");
            if (vertexColor != null)
                return vertexColor;
        }

        return Shader.Find("Universal Render Pipeline/Lit")
               ?? Shader.Find("Universal Render Pipeline/Simple Lit")
               ?? Shader.Find("Standard")
               ?? Shader.Find("Sprites/Default");
    }
}
