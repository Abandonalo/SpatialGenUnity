using System;
using System.Collections.Generic;
using UnityEngine;
using SpatialGeneration.Utils;

/// <summary>
/// Captures RGB, depth, edges and the region mask from each canonical camera.
///
/// Cameras are used exactly as <see cref="MultiViewCameraManager"/> placed them: this class
/// never moves or reprojects them. Any drift between runs would desynchronise the per-view
/// inpaints and cost the reconstruction its cross-view agreement.
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(MultiViewCameraManager))]
public class MultiViewRenderer : MonoBehaviour
{
    private const string DepthShaderName = "Hidden/SpatialGen/EncodeLinearDepth";
    private const string MaskShaderName = "Hidden/SpatialGen/RegionSelectionMask";

    /// <summary>
    /// The mask shader's world-up gate is smooth, and the pipeline then binarises at 0.5,
    /// which hollows out interiors on the Top view. Exported masks always send 0.
    /// </summary>
    private const float MaskFavorUpwardNormals = 0f;

    private static Material s_depthMaterial;
    private static Material s_maskMaterial;

    public MultiViewCameraManager cameraManager;

    [Tooltip("Flip captures vertically if the inpainter expects the opposite PNG row order.")]
    public bool flipVerticalOnReadback;

    [Tooltip("Depth tolerance when deciding whether a fragment lies on the selection's surface. " +
             "Larger values give a fuller mask across depth discontinuities.")]
    public float depthMaskEpsilon = 0.018f;

    /// <summary>Renders every canonical view against <paramref name="selection"/>.</summary>
    public MultiViewData RenderAllViews(RegionSelection selection)
    {
        if (selection == null)
            throw new ArgumentNullException(nameof(selection));

        EnsureReferences();
        cameraManager.ValidateCanonicalConsistency();

        var result = new MultiViewData();
        Vector2Int resolution = cameraManager.captureResolution;

        foreach (ViewType view in MultiViewCameraManager.AllViews)
        {
            Camera camera = cameraManager.GetCamera(view);
            Texture2D rgb = null, depth = null, edges = null, mask = null;

            try
            {
                rgb = RenderRgb(camera, resolution);
                depth = RenderDepth(camera, resolution);
                edges = TextureUtils.BuildEdgeMap(depth);
                mask = RenderMask(camera, selection, resolution, view);

                result.views.Add(new ViewData
                {
                    viewType = view.ToString(),
                    width = resolution.x,
                    height = resolution.y,
                    rgbBase64 = TextureUtils.EncodePngBase64(rgb),
                    depthBase64 = TextureUtils.EncodePngBase64(depth),
                    edgesBase64 = TextureUtils.EncodePngBase64(edges),
                    maskBase64 = TextureUtils.EncodePngBase64(mask)
                });
            }
            finally
            {
                TextureUtils.Destroy(rgb, depth, edges, mask);
            }
        }

        return result;
    }

    /// <summary>The scene as the user sees it, so the inpaint has real context to blend into.</summary>
    private Texture2D RenderRgb(Camera camera, Vector2Int resolution)
    {
        RenderTexture target = AcquireTarget(resolution);
        try
        {
            RenderTexture previousTarget = camera.targetTexture;
            try
            {
                camera.targetTexture = target;
                camera.Render();
            }
            finally
            {
                camera.targetTexture = previousTarget;
            }

            return TextureUtils.ReadPixels(target, flipVerticalOnReadback);
        }
        finally
        {
            RenderTexture.ReleaseTemporary(target);
        }
    }

    private Texture2D RenderDepth(Camera camera, Vector2Int resolution)
    {
        Shader depthShader = FindShader(DepthShaderName);
        Shader.SetGlobalFloat("_MaxDepth", Mathf.Max(0.01f, camera.farClipPlane));

        RenderTexture target = AcquireTarget(resolution);
        try
        {
            RenderReplacementPass(camera, target, depthShader);
            return TextureUtils.ReadPixels(target, flipVerticalOnReadback);
        }
        finally
        {
            RenderTexture.ReleaseTemporary(target);
        }
    }

    /// <summary>
    /// Marks the fragments that lie inside the selection box AND on a visible surface.
    ///
    /// The box alone is not enough: it would also mark empty air in front of and behind the
    /// object. So a depth prepass runs first, and the mask shader keeps only fragments whose
    /// depth matches what the camera actually sees.
    /// </summary>
    private Texture2D RenderMask(Camera camera, RegionSelection selection, Vector2Int resolution, ViewType view)
    {
        Shader depthShader = FindShader(DepthShaderName);
        Shader maskShader = FindShader(MaskShaderName);

        float maxDepth = Mathf.Max(0.01f, camera.farClipPlane);
        var renderers = new List<Renderer>(256);
        CollectVisibleRenderers(camera.cullingMask, renderers);

        RenderTexture depthBuffer = AcquireTarget(resolution);
        try
        {
            Material depthMaterial = EnsureMaterial(ref s_depthMaterial, depthShader);
            depthMaterial.SetFloat("_MaxDepth", maxDepth);
            Shader.SetGlobalFloat("_MaxDepth", maxDepth);
            RenderWithSwappedMaterials(camera, depthBuffer, depthMaterial, renderers);

            // A small outward margin keeps surfaces exactly on the box face inside the mask.
            const float boxMargin = 1.025f;
            Vector3 half = RegionSelection.ClampSize(selection.size) * 0.5f * boxMargin;

            Shader.SetGlobalTexture("_RegionMaskSceneDepthTex", depthBuffer);
            Shader.SetGlobalFloat("_DepthMaskEpsilon", Mathf.Max(1e-6f, depthMaskEpsilon));
            Shader.SetGlobalFloat("_RegionMaskUseDepthTest", 1f);
            Shader.SetGlobalFloat("_MaskDebugView", 0f);
            Shader.SetGlobalMatrix("_SelectionWorldToLocal", selection.WorldToLocal());
            Shader.SetGlobalVector("_SelectionHalfExtents", new Vector4(half.x, half.y, half.z, 0f));
            Shader.SetGlobalFloat("_MaskFavorUpwardNormals", MaskFavorUpwardNormals);

            // The Top view looks along the box's thin axis, where the inflated half-extents
            // roughly double the marked area. Clipping to the exact projected hull fixes it.
            float useViewportClip = 0f;
            if (view == ViewType.Top && selection.TryGetViewportUvBounds(camera, out Vector4 uvBounds))
            {
                Shader.SetGlobalVector("_MaskViewportUvMinMax", uvBounds);
                useViewportClip = 1f;
            }
            Shader.SetGlobalFloat("_RegionMaskUseViewportClip", useViewportClip);

            Material maskMaterial = EnsureMaterial(ref s_maskMaterial, maskShader);
            RenderTexture target = AcquireTarget(resolution);
            try
            {
                RenderWithSwappedMaterials(camera, target, maskMaterial, renderers);
                Texture2D mask = TextureUtils.ReadPixels(target, flipVerticalOnReadback);
                TextureUtils.Binarize(mask);
                return mask;
            }
            finally
            {
                RenderTexture.ReleaseTemporary(target);
            }
        }
        finally
        {
            Shader.SetGlobalTexture("_RegionMaskSceneDepthTex", null);
            Shader.SetGlobalFloat("_RegionMaskUseDepthTest", 0f);
            Shader.SetGlobalFloat("_RegionMaskUseViewportClip", 0f);
            RenderTexture.ReleaseTemporary(depthBuffer);
        }
    }

    private static void CollectVisibleRenderers(int cullingMask, List<Renderer> results)
    {
        results.Clear();
        foreach (Renderer renderer in UnityEngine.Object.FindObjectsByType<Renderer>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (renderer == null || !renderer.gameObject.activeInHierarchy)
                continue;
            if (((1 << renderer.gameObject.layer) & cullingMask) == 0)
                continue;

            Material[] materials = renderer.sharedMaterials;
            if (materials != null && materials.Length > 0)
                results.Add(renderer);
        }
    }

    /// <summary>
    /// Renders with every collected renderer forced onto <paramref name="replacement"/>.
    ///
    /// <c>Camera.RenderWithShader</c> is the obvious tool here but it is a no-op under URP,
    /// so the materials are swapped by hand and restored in the finally block.
    /// </summary>
    private static void RenderWithSwappedMaterials(
        Camera camera, RenderTexture target, Material replacement, List<Renderer> renderers)
    {
        var backup = new Dictionary<Renderer, Material[]>(renderers.Count);
        foreach (Renderer renderer in renderers)
        {
            if (renderer == null)
                continue;

            Material[] originals = renderer.sharedMaterials;
            var copy = new Material[originals.Length];
            Array.Copy(originals, copy, originals.Length);
            backup[renderer] = copy;

            var swapped = new Material[originals.Length];
            for (int i = 0; i < swapped.Length; i++)
                swapped[i] = replacement;
            renderer.sharedMaterials = swapped;
        }

        RenderTexture previousTarget = camera.targetTexture;
        CameraClearFlags previousFlags = camera.clearFlags;
        Color previousBackground = camera.backgroundColor;

        try
        {
            camera.targetTexture = target;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.Render();
        }
        finally
        {
            foreach (KeyValuePair<Renderer, Material[]> entry in backup)
            {
                if (entry.Key != null)
                    entry.Key.sharedMaterials = entry.Value;
            }

            camera.targetTexture = previousTarget;
            camera.clearFlags = previousFlags;
            camera.backgroundColor = previousBackground;
        }
    }

    private static void RenderReplacementPass(Camera camera, RenderTexture target, Shader shader)
    {
        RenderTexture previousTarget = camera.targetTexture;
        CameraClearFlags previousFlags = camera.clearFlags;
        Color previousBackground = camera.backgroundColor;

        try
        {
            camera.targetTexture = target;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.RenderWithShader(shader, string.Empty);
        }
        finally
        {
            camera.targetTexture = previousTarget;
            camera.clearFlags = previousFlags;
            camera.backgroundColor = previousBackground;
        }
    }

    private static Shader FindShader(string name) =>
        Shader.Find(name) ?? throw new InvalidOperationException($"Shader '{name}' not found.");

    private static Material EnsureMaterial(ref Material cached, Shader shader)
    {
        if (cached != null && cached.shader == shader)
            return cached;

        if (cached != null)
            UnityEngine.Object.DestroyImmediate(cached);

        cached = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        return cached;
    }

    private static RenderTexture AcquireTarget(Vector2Int resolution)
    {
        RenderTexture target = RenderTexture.GetTemporary(
            Mathf.Max(64, resolution.x), Mathf.Max(64, resolution.y), 24, RenderTextureFormat.ARGB32);
        target.filterMode = FilterMode.Point;
        target.wrapMode = TextureWrapMode.Clamp;
        return target;
    }

    private void EnsureReferences()
    {
        cameraManager ??= GetComponent<MultiViewCameraManager>();
        if (cameraManager == null)
            throw new InvalidOperationException("MultiViewRenderer needs a MultiViewCameraManager on the same object.");
    }

    private void Reset() => cameraManager = GetComponent<MultiViewCameraManager>();
}
