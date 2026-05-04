using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

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

    public MultiViewCameraManager cameraManager;

    [Tooltip("Optional fallback culling mask source. When null, each view uses its own cullingMask.")]
    public Camera cullingSource;

    [Header("Output")]
    [Tooltip("If true, flip rendered textures vertically so PNG encoding matches the inpainter's expected row order.")]
    public bool flipVerticalOnReadback = false;

    [Header("Mask")]
    [Tooltip("1 = weight mask by world up (favor roof/slabs; reduce vertical walls inside the same X/Z box). 0 = position-in-OBB only.")]
    [Range(0f, 1f)]
    public float maskFavorUpwardNormals = 0.85f;

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
            Texture2D mask = null;
            try
            {
                rgb = RenderRGB(cam, resolution);
                depth = RenderDepth(cam, resolution);
                mask = RenderMask(cam, selection, resolution);

                result.views.Add(new ViewData
                {
                    viewType = view.ToString(),
                    width = resolution.x,
                    height = resolution.y,
                    rgbBase64 = EncodePng(rgb),
                    depthBase64 = EncodePng(depth),
                    maskBase64 = EncodePng(mask)
                });
            }
            finally
            {
                if (rgb != null) UnityEngine.Object.DestroyImmediate(rgb);
                if (depth != null) UnityEngine.Object.DestroyImmediate(depth);
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
        Shader maskShader = Shader.Find(MaskShaderName);
        if (maskShader == null)
            throw new InvalidOperationException($"Mask shader '{MaskShaderName}' not found.");

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

        Shader.SetGlobalMatrix("_SelectionWorldToLocal", worldToSelection);
        Shader.SetGlobalVector("_SelectionHalfExtents", halfExtents);
        Shader.SetGlobalFloat("_MaskFavorUpwardNormals", maskFavorUpwardNormals);

        RenderTexture rt = AcquireTarget(resolution);
        try
        {
            RenderPass(cam, rt, maskShader, clearToSource: false);
            Texture2D mask = ReadPixels(rt);
            Binarize(mask);
            // #region agent log
#if UNITY_EDITOR
            AgentNdjson(mask, selection, maskFavorUpwardNormals, $"MultiViewRenderer:RenderMask exit", cam.name, HypothesisMultiviewMask);
#endif
            // #endregion
            return mask;
        }
        finally
        {
            RenderTexture.ReleaseTemporary(rt);
        }
    }

#if UNITY_EDITOR
    private const string HypothesisMultiviewMask = "H1_H2_H3";
    private const string AgentNdjsonLogPath = "/Users/alo/SpatialGenUnity/.cursor/debug-58c452.log";

    private static void AgentNdjson(Texture2D maskTex, RegionSelection sel, float maskUpW, string message, string viewLabel, string hypothesisId)
    {
        if (maskTex == null)
            return;
        float whiteFrac = MaskWhiteFraction(maskTex);
        var sb = new StringBuilder(512);
        sb.Append("{\"sessionId\":\"58c452\",\"runId\":\"maskPipeline\",\"hypothesisId\":\"").Append(hypothesisId).Append("\"");
        sb.Append(",\"location\":\"MultiViewRenderer.cs:RenderMask\"");
        sb.Append(",\"message\":\"").Append(message.Replace("\"", "'")).Append("\"");
        sb.Append(",\"data\":{");
        sb.Append("\"viewCamera\":\"").Append(viewLabel ?? "").Append("\"");
        sb.Append(",\"maskFavorUpwardNormals\":").Append(maskUpW.ToString(CultureInfo.InvariantCulture));
        if (sel != null)
        {
            sb.Append(",\"selSizeX\":").Append(sel.size.x.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"selSizeY\":").Append(sel.size.y.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"selSizeZ\":").Append(sel.size.z.ToString(CultureInfo.InvariantCulture));
        }
        sb.Append(",\"whitePixelFraction\":").Append(whiteFrac.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"width\":").Append(maskTex.width).Append(",\"height\":").Append(maskTex.height);
        sb.Append('}');
        sb.Append(",\"timestamp\":").Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).AppendLine("}");
        File.AppendAllText(AgentNdjsonLogPath, sb.ToString());
    }

    private static float MaskWhiteFraction(Texture2D tex)
    {
        Color[] pixels = tex.GetPixels();
        if (pixels == null || pixels.Length == 0)
            return -1f;
        int white = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            if (pixels[i].grayscale >= 0.5f)
                white++;
        }
        return (float)white / pixels.Length;
    }
#endif

    private void RenderPass(Camera cam, RenderTexture target, Shader overrideShader, bool clearToSource)
    {
        RenderTexture previousTarget = cam.targetTexture;
        CameraClearFlags previousFlags = cam.clearFlags;
        Color previousBackground = cam.backgroundColor;
        int previousMask = cam.cullingMask;

        try
        {
            cam.targetTexture = target;
            if (cullingSource != null)
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
