using System;
using System.Collections.Generic;
using UnityEngine;

public enum MultiViewMaskMode
{
    /// <summary>Depth prepass + OBB / viewport mask shader.</summary>
    ObbDepthShader = 0,

    /// <summary>Same camera as RGB; white replacement shader on generated/refined mesh only.</summary>
    ScreenSpaceGeneratedMesh = 1
}

// MultiViewRenderer captures per-view RGB, depth and region-mask textures
// from the canonical camera rig owned by MultiViewCameraManager.
//
// Cameras are used as-is: position, rotation, projection, resolution are
// never mutated here. This is a non-negotiable constraint of the
// multi-view refinement pipeline - if any camera moved between runs the
// server-side inpaint would drift between views and the final TripoSR
// reconstruction would lose consistency.
[ExecuteAlways]
[RequireComponent(typeof(MultiViewCameraManager))]
public class MultiViewRenderer : MonoBehaviour
{
    private const string DepthShaderName = "Hidden/SpatialGen/EncodeLinearDepth";
    private const string MaskShaderName = "Hidden/SpatialGen/RegionSelectionMask";
    private const string SolidWhiteMaskShaderName = "Hidden/SpatialGen/MaskSolidWhite";

    public MultiViewCameraManager cameraManager;

    [Tooltip("Optional fallback culling mask source. When null, each view uses its own cullingMask.")]
    public Camera cullingSource;

    [Header("Output")]
    [Tooltip("If true, flip rendered textures vertically so PNG encoding matches the inpainter's expected row order.")]
    public bool flipVerticalOnReadback = false;

    [Header("Mask")]
    [Tooltip("ObbDepthShader: selection/OBB path. ScreenSpaceGeneratedMesh: render generated mesh as white (same projection as RGB).")]
    public MultiViewMaskMode maskMode = MultiViewMaskMode.ScreenSpaceGeneratedMesh;

    [Tooltip("Unity layer for ScreenSpaceGeneratedMesh (must exist in Tags & Layers).")]
    public string maskGeometryLayerName = RegionSelectionManager.MaskSelectionLayerName;

    [Tooltip("When true, build a Sobel edge map from the linear depth image and send as edgesBase64 per view.")]
    public bool includeEdgesFromDepth = true;

    [Tooltip("Logged only: exported mask pass always sends 0 (see RegionMaskRenderer.MaskExportFavorUpwardNormals).")]
    [Range(0f, 1f)]
    public float maskFavorUpwardNormals = 0f;

    [Tooltip("Max |Δ| in linear encoded depth (EncodeLinearDepth) vs fragment.")]
    public float depthMaskEpsilon = 0.004f;

    [Tooltip("0=binary mask for backend. 1–3=depth debug (no binarize).")]
    [Range(0f, 3f)]
    public float maskDebugView = 0f;

    public MultiViewData RenderAllViews(RegionSelection selection)
    {
        if (selection == null)
            throw new ArgumentNullException(nameof(selection));

        EnsureReferences();
        cameraManager.ValidateCanonicalConsistency();

        MultiViewData result = new MultiViewData();

        foreach (ViewType view in cameraManager.GetAllViews())
        {
            Camera cam = cameraManager.GetCamera(view);
            if (cam == null)
                throw new InvalidOperationException($"MultiViewRenderer: camera for view {view} is null.");

            Vector2Int resolution = cameraManager.captureResolution;

            Texture2D rgb = null;
            Texture2D depth = null;
            Texture2D edges = null;
            Texture2D mask = null;
            try
            {
                rgb = RenderRGB(cam, resolution);
                depth = RenderDepth(cam, resolution);
                edges = includeEdgesFromDepth ? BuildSimpleEdgeMapFromDepth(depth) : null;
                mask = RenderMask(cam, selection, resolution);

                result.views.Add(new ViewData
                {
                    viewType = view.ToString(),
                    width = resolution.x,
                    height = resolution.y,
                    rgbBase64 = EncodePng(rgb),
                    depthBase64 = EncodePng(depth),
                    edgesBase64 = edges != null ? EncodePng(edges) : string.Empty,
                    maskBase64 = EncodePng(mask)
                });
            }
            finally
            {
                if (rgb != null) UnityEngine.Object.DestroyImmediate(rgb);
                if (depth != null) UnityEngine.Object.DestroyImmediate(depth);
                if (edges != null) UnityEngine.Object.DestroyImmediate(edges);
                if (mask != null) UnityEngine.Object.DestroyImmediate(mask);
            }
        }

        return result;
    }

    private Texture2D RenderRGB(Camera cam, Vector2Int resolution)
    {
        RenderTexture rt = AcquireTarget(resolution);
        try
        {
            RenderPass(cam, rt, null, clearToSource: true);
            return ReadPixels(rt);
        }
        finally
        {
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    private Texture2D RenderDepth(Camera cam, Vector2Int resolution)
    {
        Shader depthShader = Shader.Find(DepthShaderName);
        if (depthShader == null)
            throw new InvalidOperationException($"Depth shader '{DepthShaderName}' not found.");

        Shader.SetGlobalFloat("_MaxDepth", Mathf.Max(0.01f, cam.farClipPlane));

        RenderTexture rt = AcquireTarget(resolution);
        try
        {
            RenderPass(cam, rt, depthShader, clearToSource: false);
            return ReadPixels(rt);
        }
        finally
        {
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    private Texture2D RenderMask(Camera cam, RegionSelection selection, Vector2Int resolution)
    {
        return maskMode == MultiViewMaskMode.ScreenSpaceGeneratedMesh
            ? RenderMaskScreenSpace(cam, selection, resolution)
            : RenderMaskObbDepth(cam, selection, resolution);
    }

    private Texture2D RenderMaskScreenSpace(Camera cam, RegionSelection selection, Vector2Int resolution)
    {
        Shader whiteShader = Shader.Find(SolidWhiteMaskShaderName);
        if (whiteShader == null)
            throw new InvalidOperationException($"Mask shader '{SolidWhiteMaskShaderName}' not found.");

        int maskLayer = LayerMask.NameToLayer(maskGeometryLayerName);
        if (maskLayer < 0)
        {
            throw new InvalidOperationException(
                $"MultiViewRenderer: layer '{maskGeometryLayerName}' is not defined. Add it under Edit > Project Settings > Tags and Layers.");
        }

        var savedLayers = new List<(Renderer renderer, int savedLayer)>();
        RegionSelectionManager.TryBeginScreenSpaceMaskPass(maskLayer, savedLayers);
        try
        {
            RenderTexture rt = AcquireTarget(resolution);
            try
            {
                int maskOnly = 1 << maskLayer;
                RenderPass(cam, rt, whiteShader, clearToSource: false, maskOnly);
                Texture2D mask = ReadPixels(rt);
                if (maskDebugView < 0.25f)
                    Binarize(mask);
                return mask;
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rt);
            }
        }
        finally
        {
            RegionSelectionManager.EndScreenSpaceMaskPass(savedLayers);
        }
    }

    private Texture2D RenderMaskObbDepth(Camera cam, RegionSelection selection, Vector2Int resolution)
    {
        Shader depthShader = Shader.Find(DepthShaderName);
        Shader maskShader = Shader.Find(MaskShaderName);
        if (depthShader == null)
            throw new InvalidOperationException($"Depth shader '{DepthShaderName}' not found.");
        if (maskShader == null)
            throw new InvalidOperationException($"Mask shader '{MaskShaderName}' not found.");

        float maxDepth = Mathf.Max(0.01f, cam.farClipPlane);
        RenderTexture depthBuf = AcquireTarget(resolution);
        try
        {
            Shader.SetGlobalFloat("_MaxDepth", maxDepth);
            RenderPass(cam, depthBuf, depthShader, clearToSource: false);

            Matrix4x4 worldToSelection = Matrix4x4.TRS(
                selection.center,
                selection.rotation,
                Vector3.one).inverse;

            Vector3 half = selection.size * 0.5f;
            Vector4 halfExtents = new Vector4(
                Mathf.Max(0.005f, Mathf.Abs(half.x)),
                Mathf.Max(0.005f, Mathf.Abs(half.y)),
                Mathf.Max(0.005f, Mathf.Abs(half.z)),
                0f);

            Shader.SetGlobalTexture("_RegionMaskSceneDepthTex", depthBuf);
            Shader.SetGlobalFloat("_DepthMaskEpsilon", Mathf.Max(1e-6f, depthMaskEpsilon));
            Shader.SetGlobalFloat("_RegionMaskUseDepthTest", 1f);
            Shader.SetGlobalFloat("_MaskDebugView", maskDebugView);

            if (cam != null && RegionMaskRenderer.TryGetSelectionViewportUvBounds(cam, selection, out Vector4 uvBoundsMv))
            {
                Shader.SetGlobalVector("_MaskViewportUvMinMax", uvBoundsMv);
                Shader.SetGlobalFloat("_RegionMaskUseViewportClip", 1f);
            }
            else
                Shader.SetGlobalFloat("_RegionMaskUseViewportClip", 0f);

            Shader.SetGlobalMatrix("_SelectionWorldToLocal", worldToSelection);
            Shader.SetGlobalVector("_SelectionHalfExtents", halfExtents);
            Shader.SetGlobalFloat("_MaskFavorUpwardNormals", RegionMaskRenderer.MaskExportFavorUpwardNormals);
            Shader.SetGlobalFloat("_MaxDepth", maxDepth);

            RenderTexture rt = AcquireTarget(resolution);
            try
            {
                RenderPass(cam, rt, maskShader, clearToSource: false);
                Texture2D mask = ReadPixels(rt);
                if (maskDebugView < 0.25f)
                    Binarize(mask);
                return mask;
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rt);
            }
        }
        finally
        {
            Shader.SetGlobalTexture("_RegionMaskSceneDepthTex", null);
            Shader.SetGlobalFloat("_RegionMaskUseDepthTest", 0f);
            Shader.SetGlobalFloat("_RegionMaskUseViewportClip", 0f);
            Shader.SetGlobalFloat("_MaskDebugView", 0f);
            RenderTexture.ReleaseTemporary(depthBuf);
        }
    }

    private void RenderPass(Camera cam, RenderTexture target, Shader overrideShader, bool clearToSource, int? overrideCullingMask = null)
    {
        RenderTexture previousTarget = cam.targetTexture;
        CameraClearFlags previousFlags = cam.clearFlags;
        Color previousBackground = cam.backgroundColor;
        int previousMask = cam.cullingMask;

        try
        {
            cam.targetTexture = target;
            if (overrideCullingMask.HasValue)
                cam.cullingMask = overrideCullingMask.Value;
            else if (cullingSource != null)
                cam.cullingMask = cullingSource.cullingMask;

            if (overrideShader == null)
            {
                // For the RGB pass, preserve the camera's own clear flags and
                // background so the rendered frame matches what the user sees
                // in-scene.
                cam.Render();
            }
            else
            {
                // Depth / mask passes need a deterministic black background so
                // out-of-selection fragments encode as 0.
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.RenderWithShader(overrideShader, string.Empty);
            }
        }
        finally
        {
            cam.targetTexture = previousTarget;
            cam.clearFlags = previousFlags;
            cam.backgroundColor = previousBackground;
            cam.cullingMask = previousMask;
        }
    }

    private static Texture2D BuildSimpleEdgeMapFromDepth(Texture2D source)
    {
        if (source == null)
            return null;

        int width = source.width;
        int height = source.height;
        var edges = new Texture2D(width, height, TextureFormat.RGBA32, false);
        Color[] src = source.GetPixels();
        Color[] dst = new Color[src.Length];

        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int i = y * width + x;
                float gx =
                    -src[(y - 1) * width + (x - 1)].r + src[(y - 1) * width + (x + 1)].r +
                    -2f * src[y * width + (x - 1)].r + 2f * src[y * width + (x + 1)].r +
                    -src[(y + 1) * width + (x - 1)].r + src[(y + 1) * width + (x + 1)].r;
                float gy =
                    src[(y - 1) * width + (x - 1)].r + 2f * src[(y - 1) * width + x].r + src[(y - 1) * width + (x + 1)].r -
                    src[(y + 1) * width + (x - 1)].r - 2f * src[(y + 1) * width + x].r - src[(y + 1) * width + (x + 1)].r;
                float edge = Mathf.Clamp01(Mathf.Sqrt(gx * gx + gy * gy));
                dst[i] = new Color(edge, edge, edge, 1f);
            }
        }

        for (int x = 0; x < width; x++)
        {
            dst[x] = Color.black;
            dst[(height - 1) * width + x] = Color.black;
        }

        for (int y = 0; y < height; y++)
        {
            dst[y * width] = Color.black;
            dst[y * width + (width - 1)] = Color.black;
        }

        edges.SetPixels(dst);
        edges.Apply(false, false);
        return edges;
    }

    private static RenderTexture AcquireTarget(Vector2Int resolution)
    {
        int w = Mathf.Max(64, resolution.x);
        int h = Mathf.Max(64, resolution.y);
        RenderTexture rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
        rt.filterMode = FilterMode.Point;
        rt.wrapMode = TextureWrapMode.Clamp;
        return rt;
    }

    private Texture2D ReadPixels(RenderTexture rt)
    {
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = rt;
        try
        {
            Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply(false, false);
            if (flipVerticalOnReadback)
                FlipVertical(tex);
            return tex;
        }
        finally
        {
            RenderTexture.active = previous;
        }
    }

    private static void FlipVertical(Texture2D tex)
    {
        Color[] pixels = tex.GetPixels();
        int w = tex.width;
        int h = tex.height;
        Color[] flipped = new Color[pixels.Length];
        for (int y = 0; y < h; y++)
        {
            int srcRow = (h - 1 - y) * w;
            int dstRow = y * w;
            Array.Copy(pixels, srcRow, flipped, dstRow, w);
        }
        tex.SetPixels(flipped);
        tex.Apply(false, false);
    }

    private static void Binarize(Texture2D texture)
    {
        Color[] pixels = texture.GetPixels();
        for (int i = 0; i < pixels.Length; i++)
        {
            float value = pixels[i].grayscale >= 0.5f ? 1f : 0f;
            pixels[i] = new Color(value, value, value, 1f);
        }
        texture.SetPixels(pixels);
        texture.Apply(false, false);
    }

    private static string EncodePng(Texture2D texture)
    {
        if (texture == null)
            return string.Empty;
        byte[] bytes = texture.EncodeToPNG();
        if (bytes == null || bytes.Length == 0)
            return string.Empty;
        return Convert.ToBase64String(bytes);
    }

    private void EnsureReferences()
    {
        if (cameraManager == null)
            cameraManager = GetComponent<MultiViewCameraManager>();
        if (cameraManager == null)
            throw new InvalidOperationException("MultiViewRenderer requires a MultiViewCameraManager on the same GameObject.");
    }

    private void Reset()
    {
        cameraManager = GetComponent<MultiViewCameraManager>();
    }
}
