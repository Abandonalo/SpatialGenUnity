using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>Projects refined cardinal images onto a placed replacement mesh.</summary>
public static class MultiViewVertexColorProjector
{
    private sealed class LoadedView
    {
        public RefinedViewProjection Projection;
        public Texture2D Texture;
    }

    public static int Apply(
        GameObject target,
        IReadOnlyList<RefinedViewProjection> projections,
        string assetFolderSibling)
    {
        if (target == null || projections == null || projections.Count == 0)
            return 0;

        List<LoadedView> views = Load(projections);
        if (views.Count == 0)
            return 0;

        int coloured = 0;
        try
        {
            foreach (MeshFilter filter in target.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh source = filter.sharedMesh;
                if (source == null || source.vertexCount == 0)
                    continue;

                Mesh mesh = UnityEngine.Object.Instantiate(source);
                mesh.name = $"{source.name}_ProjectedColor";
                if (mesh.normals.Length != mesh.vertexCount)
                    mesh.RecalculateNormals();

                Vector3[] vertices = mesh.vertices;
                Vector3[] normals = mesh.normals;
                var colors = new Color[vertices.Length];
                Matrix4x4 normalToWorld = filter.transform.localToWorldMatrix.inverse.transpose;
                for (int i = 0; i < vertices.Length; i++)
                {
                    Vector3 world = filter.transform.TransformPoint(vertices[i]);
                    Vector3 normal = normalToWorld.MultiplyVector(normals[i]).normalized;
                    colors[i] = Sample(world, normal, views, out bool covered);
                    if (covered)
                        coloured++;
                }

                mesh.colors = colors;
                filter.sharedMesh = MeshImporter.PersistMesh(
                    mesh, assetFolderSibling, $"{source.name}_ProjectedColor");
            }
        }
        finally
        {
            foreach (LoadedView view in views)
                UnityEngine.Object.DestroyImmediate(view.Texture);
        }

        return coloured;
    }

    private static Color Sample(
        Vector3 world,
        Vector3 normal,
        IReadOnlyList<LoadedView> views,
        out bool covered)
    {
        Color first = Color.white, second = Color.white;
        float firstWeight = 0f, secondWeight = 0f;

        foreach (LoadedView loaded in views)
        {
            RefinedViewProjection projection = loaded.Projection;
            if (!TryProject(projection, world, out Vector3 viewport, out Vector3 toCamera))
                continue;
            if (viewport.z <= 0f ||
                viewport.x < projection.cropMinX || viewport.x > projection.cropMaxX ||
                viewport.y < projection.cropMinY || viewport.y > projection.cropMaxY)
                continue;

            float facing = Mathf.Max(0f, Vector3.Dot(normal, toCamera));
            float weight = facing * facing * facing * facing;
            if (weight <= 1e-4f)
                continue;

            float y = projection.flipVertical ? 1f - viewport.y : viewport.y;
            Color sample = loaded.Texture.GetPixelBilinear(viewport.x, y);
            if (weight > firstWeight)
            {
                secondWeight = firstWeight;
                second = first;
                firstWeight = weight;
                first = sample;
            }
            else if (weight > secondWeight)
            {
                secondWeight = weight;
                second = sample;
            }
        }

        covered = firstWeight > 0f;
        if (!covered)
            return Color.white;
        float total = firstWeight + secondWeight;
        return total > 0f ? (first * firstWeight + second * secondWeight) / total : first;
    }

    private static bool TryProject(
        RefinedViewProjection projection,
        Vector3 world,
        out Vector3 viewport,
        out Vector3 toCamera)
    {
        if (projection.hasStoredProjection)
        {
            Vector3 view = projection.worldToCameraMatrix.MultiplyPoint3x4(world);
            float depth = -view.z;
            Vector4 clip = projection.projectionMatrix * new Vector4(view.x, view.y, view.z, 1f);
            if (depth <= 0f || Mathf.Abs(clip.w) <= 1e-7f)
            {
                viewport = default;
                toCamera = default;
                return false;
            }
            float inverseW = 1f / clip.w;
            viewport = new Vector3(
                clip.x * inverseW * 0.5f + 0.5f,
                clip.y * inverseW * 0.5f + 0.5f,
                depth);
            toCamera = (projection.cameraPosition - world).normalized;
            return true;
        }

        Camera camera = projection.camera;
        if (camera == null)
        {
            viewport = default;
            toCamera = default;
            return false;
        }
        viewport = camera.WorldToViewportPoint(world);
        toCamera = (camera.transform.position - world).normalized;
        return true;
    }

    private static List<LoadedView> Load(IReadOnlyList<RefinedViewProjection> projections)
    {
        var result = new List<LoadedView>(projections.Count);
        foreach (RefinedViewProjection projection in projections)
        {
            if (projection == null || string.IsNullOrWhiteSpace(projection.imageAbsolutePath) ||
                !File.Exists(projection.imageAbsolutePath))
                continue;

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            if (!texture.LoadImage(File.ReadAllBytes(projection.imageAbsolutePath), markNonReadable: false))
            {
                UnityEngine.Object.DestroyImmediate(texture);
                continue;
            }
            result.Add(new LoadedView { Projection = projection, Texture = texture });
        }
        return result;
    }
}
