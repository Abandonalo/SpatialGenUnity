using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public enum MultiViewMaskMode
{
    /// <summary>Depth prepass + OBB / viewport mask shader.</summary>
    ObbDepthShader = 0,

    /// <summary>
    /// Mesh white mask when a subtree restrict is active; otherwise multi-view uses OBB+depth from <see cref="RegionSelection"/>.
    /// </summary>
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

    static Material s_obbDepthPassMaterial;
    static Material s_obbMaskPassMaterial;

    /// <summary>
    /// When non-null/non-empty during <see cref="RenderAllViews(RegionSelection, string)"/>, only that direct subtree
    /// under GeneratedContent participates in ScreenSpaceGeneratedMesh masks.
    /// </summary>
    string _restrictScreenSpaceMaskToGeneratedChildName;

    public MultiViewCameraManager cameraManager;

    [Tooltip("Optional fallback culling mask source. When null, each view uses its own cullingMask.")]
    public Camera cullingSource;

    [Header("Output")]
    [Tooltip("If true, flip rendered textures vertically so PNG encoding matches the inpainter's expected row order.")]
    public bool flipVerticalOnReadback = false;

    [Header("Mask")]
    [Tooltip("ObbDepthShader: OBB + depth (default for region refinement). ScreenSpace: white mesh mask only when a subtree restrict is set.")]
    public MultiViewMaskMode maskMode = MultiViewMaskMode.ObbDepthShader;

    [Tooltip("Unity layer for ScreenSpaceGeneratedMesh (must exist in Tags & Layers).")]
    public string maskGeometryLayerName = RegionSelectionManager.MaskSelectionLayerName;

    [Tooltip("When true, build a Sobel edge map from the linear depth image and send as edgesBase64 per view.")]
    public bool includeEdgesFromDepth = true;

    [Tooltip("Logged only: exported mask pass always sends 0 (see RegionMaskRenderer.MaskExportFavorUpwardNormals).")]
    [Range(0f, 1f)]
    public float maskFavorUpwardNormals = 0f;

    [Tooltip("Max |Δ| in linear encoded depth (EncodeLinearDepth) vs fragment. Larger = more tolerant (fuller mask at depth discontinuities).")]
    public float depthMaskEpsilon = 0.018f;

    [Tooltip("0=binary mask for backend. 1–3=depth debug (no binarize).")]
    [Range(0f, 3f)]
    public float maskDebugView = 0f;

    public MultiViewData RenderAllViews(RegionSelection selection)
    {
        return RenderAllViews(selection, null);
    }

    /// <param name="restrictScreenSpaceMaskToGeneratedContentDirectChild">
    /// ScreenSpaceGeneratedMesh only: direct child name under GeneratedContent whose renderers move to the mask layer
    /// (null/empty = all Generated_Mesh* / Refined_Mesh* subtrees, previous behavior).</param>
    public MultiViewData RenderAllViews(RegionSelection selection, string restrictScreenSpaceMaskToGeneratedContentDirectChild)
    {
        if (selection == null)
            throw new ArgumentNullException(nameof(selection));

        EnsureReferences();
        cameraManager.ValidateCanonicalConsistency();

        MultiViewData result = new MultiViewData();

        bool hadRestrict =
            !string.IsNullOrEmpty(restrictScreenSpaceMaskToGeneratedContentDirectChild);

        try
        {
            if (hadRestrict)
                _restrictScreenSpaceMaskToGeneratedChildName = restrictScreenSpaceMaskToGeneratedContentDirectChild;

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
                    mask = RenderMask(cam, selection, resolution, view);

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
        finally
        {
            if (hadRestrict)
                _restrictScreenSpaceMaskToGeneratedChildName = null;
        }
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

    private Texture2D RenderMask(Camera cam, RegionSelection selection, Vector2Int resolution, ViewType viewType)
    {
        bool screenSpaceWithRestrict = maskMode == MultiViewMaskMode.ScreenSpaceGeneratedMesh
            && !string.IsNullOrEmpty(_restrictScreenSpaceMaskToGeneratedChildName);

        if (screenSpaceWithRestrict)
            return RenderMaskScreenSpace(cam, selection, resolution);

        return RenderMaskObbDepth(cam, selection, resolution, viewType);
    }

    private Texture2D RenderMaskScreenSpace(Camera cam, RegionSelection selection, Vector2Int resolution)
    {
        int maskLayer = LayerMask.NameToLayer(maskGeometryLayerName);
        if (maskLayer < 0)
        {
            throw new InvalidOperationException(
                $"MultiViewRenderer: layer '{maskGeometryLayerName}' is not defined. Add it under Edit > Project Settings > Tags and Layers.");
        }

        var savedLayers = new List<(Renderer renderer, int savedLayer)>();
        RegionSelectionManager.TryBeginScreenSpaceMaskPass(maskLayer, savedLayers,
            string.IsNullOrEmpty(_restrictScreenSpaceMaskToGeneratedChildName)
                ? null
                : _restrictScreenSpaceMaskToGeneratedChildName);

        Material whiteMat = GetIsolationWhiteMaterial();
        // Only meshes we explicitly moved onto the mask layer — never sweep the whole scene by layer index.
        Dictionary<Renderer, Material[]> materialBackups =
            SnapshotAndApplyWhiteMaterials(savedLayers, whiteMat);

        int w = Mathf.Max(64, resolution.x);
        int h = Mathf.Max(64, resolution.y);

        RenderTexture rt = null;
        GameObject cloneRoot = null;
        try
        {
            rt = CreateMaskIsolationRenderTexture(w, h);
            cloneRoot = InstantiateMaskIsolationCamera(cam, rt, 1 << maskLayer);

            cloneRoot.SetActive(true);
            Camera maskCam = cloneRoot.GetComponent<Camera>();
            if (maskCam == null)
                throw new InvalidOperationException("SpatialGeneration: cloned mask camera missing Camera.");

            maskCam.Render();

            Texture2D mask = ReadMaskIsolationPixels(rt);
            if (maskDebugView < 0.25f)
                Binarize(mask);
            return mask;
        }
        finally
        {
            if (cloneRoot != null)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                    UnityEngine.Object.DestroyImmediate(cloneRoot);
                else
                    UnityEngine.Object.Destroy(cloneRoot);
#else
                UnityEngine.Object.Destroy(cloneRoot);
#endif
            }

            DestroyMaskIsolationRenderTexture(rt);

            foreach (KeyValuePair<Renderer, Material[]> kv in materialBackups)
            {
                if (kv.Key != null)
                    kv.Key.sharedMaterials = kv.Value;
            }

            RegionSelectionManager.EndScreenSpaceMaskPass(savedLayers);
        }
    }

    /// <summary>
    /// Backup every slot in <see cref="Renderer.sharedMaterials"/> and assign white Unlit for each submesh.
    /// Only renderers from the active mask pass (Geometry moved to <c>SpatialGenMaskSelection</c>).
    /// </summary>
    private static Dictionary<Renderer, Material[]> SnapshotAndApplyWhiteMaterials(
        List<(Renderer renderer, int savedLayer)> maskPassRenderers,
        Material whiteMat)
    {
        var backups = new Dictionary<Renderer, Material[]>();
        for (int i = 0; i < maskPassRenderers.Count; i++)
        {
            Renderer r = maskPassRenderers[i].renderer;
            if (r == null || !r.enabled)
                continue;

            Material[] originals = r.sharedMaterials;
            if (originals == null || originals.Length == 0)
                continue;

            var backupCopy = new Material[originals.Length];
            Array.Copy(originals, backupCopy, originals.Length);
            backups[r] = backupCopy;

            var swapped = new Material[originals.Length];
            for (int s = 0; s < originals.Length; s++)
                swapped[s] = whiteMat;
            r.sharedMaterials = swapped;
        }

        return backups;
    }

    private static RenderTexture CreateMaskIsolationRenderTexture(int width, int height)
    {
        var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            name = "SpatialGen_MultiView_MaskIsolation",
            hideFlags = HideFlags.DontSave
        };

        rt.Create();
        return rt;
    }

    private static void DestroyMaskIsolationRenderTexture(RenderTexture rt)
    {
        if (rt == null)
            return;
        rt.Release();

#if UNITY_EDITOR
        if (!Application.isPlaying)
            UnityEngine.Object.DestroyImmediate(rt);
        else
            UnityEngine.Object.Destroy(rt);
#else
        UnityEngine.Object.Destroy(rt);
#endif
    }

    /// <summary>Does not mutate the canonical multi-view cameras — clones for an isolated Render().</summary>
    private GameObject InstantiateMaskIsolationCamera(Camera sourceCam, RenderTexture target, int cullingLayersMask)
    {
        GameObject cloneRoot = UnityEngine.Object.Instantiate(sourceCam.gameObject);
        cloneRoot.name = "__SpatialGen_IsolatedMaskCamera";
        cloneRoot.hideFlags = HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor | HideFlags.HideInHierarchy;
        cloneRoot.SetActive(false);

        foreach (AudioListener al in cloneRoot.GetComponentsInChildren<AudioListener>(true))
        {
            if (al == null)
                continue;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                UnityEngine.Object.DestroyImmediate(al);
            else
                UnityEngine.Object.Destroy(al);
#else
            UnityEngine.Object.Destroy(al);
#endif
        }

        Camera maskCam = cloneRoot.GetComponent<Camera>();
        if (maskCam == null)
            return cloneRoot;

        maskCam.tag = "Untagged";
        maskCam.enabled = false;
        maskCam.targetTexture = target;
        maskCam.allowHDR = false;
        maskCam.allowMSAA = false;
        maskCam.depthTextureMode = DepthTextureMode.None;
        maskCam.backgroundColor = Color.black;
        maskCam.clearFlags = CameraClearFlags.SolidColor;
        maskCam.cullingMask = cullingLayersMask;
        // stereoTargetEye is not valid on scriptable render pipelines (URP/HDRP).

        TryApplyUrpMaskCloneSettings(maskCam);

        return cloneRoot;
    }

    /// <summary>Turn off PP / extra URP attachments on the cloned camera via reflection — no asmdef dependency on URP.</summary>
    private static void TryApplyUrpMaskCloneSettings(Camera cam)
    {
        if (cam == null)
            return;

        Type uacdType = Type.GetType(
            "UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");
        if (uacdType == null)
            return;

        Component uacd = cam.GetComponent(uacdType);
        if (uacd == null)
            return;

        BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        TryAssignPropertyOrField(uacd, "renderPostProcessing", false, bf);
        TryAssignPropertyOrField(uacd, "requiresDepthTexture", false, bf);
        TryAssignPropertyOrField(uacd, "requiresColorTexture", false, bf);

        // Standalone Render() to an RT with renderType=Overlay (or a non-empty camera stack) often yields a full
        // solid frame (commonly white). Force Base and strip stacks for an offscreen one-shot render.
        Type cameraRenderTypeEnum = Type.GetType(
            "UnityEngine.Rendering.Universal.CameraRenderType, Unity.RenderPipelines.Universal.Runtime");
        if (cameraRenderTypeEnum != null && cameraRenderTypeEnum.IsEnum)
        {
            try
            {
                object baseMode = Enum.Parse(cameraRenderTypeEnum, "Base");
                TryAssignPropertyOrField(uacd, "renderType", baseMode, bf);
            }
            catch (ArgumentException)
            {
                // Ignore if enum shape changes between URP versions.
            }
        }

        PropertyInfo stackProp = uacdType.GetProperty("cameraStack", bf);
        if (stackProp?.GetValue(uacd) is IList<Camera> stack)
        {
            stack.Clear();
        }

        PropertyInfo volumeMaskProp = uacdType.GetProperty("volumeLayerMask", bf);
        if (volumeMaskProp != null && volumeMaskProp.CanWrite)
        {
            Type pt = volumeMaskProp.PropertyType;
            if (pt == typeof(LayerMask))
                volumeMaskProp.SetValue(uacd, (LayerMask)0);
            else if (pt == typeof(int))
                volumeMaskProp.SetValue(uacd, 0);
        }
    }

    private static void TryAssignPropertyOrField(object target, string memberName, object value,
        BindingFlags bindingFlags)
    {
        Type t = target.GetType();

        PropertyInfo p = t.GetProperty(memberName, bindingFlags);
        if (p != null && p.CanWrite)
        {
            p.SetValue(target, value);
            return;
        }

        FieldInfo f = t.GetField(memberName, bindingFlags);
        f?.SetValue(target, value);
    }

    private Texture2D ReadMaskIsolationPixels(RenderTexture rt)
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

    private static Material s_isolationWhiteMaterial;

    /// <summary>URP-compatible unlit opaque white (shared); used only during mask isolation.</summary>
    private static Material GetIsolationWhiteMaterial()
    {
        if (s_isolationWhiteMaterial != null && s_isolationWhiteMaterial.shader != null)
            return s_isolationWhiteMaterial;

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit")
            ?? Shader.Find("Unlit/Color")
            ?? Shader.Find("Sprites/Default");
        if (shader == null)
            throw new InvalidOperationException(
                "MultiViewRenderer mask isolation requires an Unlit shader (try Universal Render Pipeline/Unlit).");

        s_isolationWhiteMaterial = new Material(shader)
        {
            name = "SpatialGen_MultiViewIsolationWhite",
            hideFlags = HideFlags.DontSave
        };
        ConfigureIsolationWhiteMaterial(s_isolationWhiteMaterial);
        return s_isolationWhiteMaterial;
    }

    private static void ConfigureIsolationWhiteMaterial(Material material)
    {
        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", Color.white);
        if (material.HasProperty("_Color"))
            material.SetColor("_Color", Color.white);
    }

    private Texture2D RenderMaskObbDepth(Camera cam, RegionSelection selection, Vector2Int resolution, ViewType viewType)
    {
        Shader depthShader = Shader.Find(DepthShaderName);
        Shader maskShader = Shader.Find(MaskShaderName);
        if (depthShader == null)
            throw new InvalidOperationException($"Depth shader '{DepthShaderName}' not found.");
        if (maskShader == null)
            throw new InvalidOperationException($"Mask shader '{MaskShaderName}' not found.");

        float maxDepth = Mathf.Max(0.01f, cam.farClipPlane);
        int effectiveCull = cam.cullingMask;
        if (cullingSource != null)
            effectiveCull = cullingSource.cullingMask;

        var obbRenderers = new List<Renderer>(256);
        CollectRenderersForCullingMask(effectiveCull, obbRenderers);

        RenderTexture depthBuf = AcquireTarget(resolution);
        try
        {
            Material depthMat = EnsureObbDepthPassMaterial(depthShader, maxDepth);
            Shader.SetGlobalFloat("_MaxDepth", maxDepth);
            RenderCameraWithSwappedMaterials(cam, depthBuf, effectiveCull, depthMat, obbRenderers);

            Matrix4x4 worldToSelection = Matrix4x4.TRS(
                selection.center,
                selection.rotation,
                Vector3.one).inverse;

            Vector3 half = selection.size * 0.5f;
            const float obbWorldMargin = 1.025f;
            Vector4 halfExtents = new Vector4(
                Mathf.Max(0.005f, Mathf.Abs(half.x)) * obbWorldMargin,
                Mathf.Max(0.005f, Mathf.Abs(half.y)) * obbWorldMargin,
                Mathf.Max(0.005f, Mathf.Abs(half.z)) * obbWorldMargin,
                0f);

            Shader.SetGlobalTexture("_RegionMaskSceneDepthTex", depthBuf);
            Shader.SetGlobalFloat("_DepthMaskEpsilon", Mathf.Max(1e-6f, depthMaskEpsilon));
            Shader.SetGlobalFloat("_RegionMaskUseDepthTest", 1f);
            Shader.SetGlobalFloat("_MaskDebugView", maskDebugView);

            // OBB mask uses inflated half-extents (obbWorldMargin); Top-down captures showed ~2× white-area vs lateral views (logs).
            // Clip Top masks to the projected selection hull (exact corners), matching RegionMaskRenderer.
            if (viewType == ViewType.Top
                && RegionMaskRenderer.TryGetSelectionViewportUvBounds(cam, selection, out Vector4 uvFootprint))
            {
                Shader.SetGlobalVector("_MaskViewportUvMinMax", uvFootprint);
                Shader.SetGlobalFloat("_RegionMaskUseViewportClip", 1f);
            }
            else
                Shader.SetGlobalFloat("_RegionMaskUseViewportClip", 0f);

            Shader.SetGlobalMatrix("_SelectionWorldToLocal", worldToSelection);
            Shader.SetGlobalVector("_SelectionHalfExtents", halfExtents);
            Shader.SetGlobalFloat("_MaskFavorUpwardNormals", RegionMaskRenderer.MaskExportFavorUpwardNormals);
            Shader.SetGlobalFloat("_MaxDepth", maxDepth);

            Material maskMat = EnsureObbMaskPassMaterial(maskShader);
            RenderTexture rt = AcquireTarget(resolution);
            try
            {
                RenderCameraWithSwappedMaterials(cam, rt, effectiveCull, maskMat, obbRenderers);
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

    private static void CollectRenderersForCullingMask(int cullingMaskBits, List<Renderer> results)
    {
        results.Clear();
        Renderer[] found = UnityEngine.Object.FindObjectsByType<Renderer>(
            FindObjectsInactive.Exclude,
            FindObjectsSortMode.None);
        for (int i = 0; i < found.Length; i++)
        {
            Renderer r = found[i];
            if (r == null || !r.gameObject.activeInHierarchy)
                continue;
            int layer = r.gameObject.layer;
            if (((1 << layer) & cullingMaskBits) == 0)
                continue;

            Material[] mats = r.sharedMaterials;
            if (mats == null || mats.Length == 0)
                continue;

            results.Add(r);
        }
    }

    private void RenderCameraWithSwappedMaterials(
        Camera cam,
        RenderTexture target,
        int effectiveCullingMask,
        Material replacement,
        List<Renderer> renderers)
    {
        var backup = new Dictionary<Renderer, Material[]>(renderers.Count);
        for (int i = 0; i < renderers.Count; i++)
        {
            Renderer r = renderers[i];
            if (r == null)
                continue;
            Material[] originals = r.sharedMaterials;
            var copy = new Material[originals.Length];
            Array.Copy(originals, copy, originals.Length);
            backup[r] = copy;
            var swapped = new Material[originals.Length];
            for (int s = 0; s < originals.Length; s++)
                swapped[s] = replacement;
            r.sharedMaterials = swapped;
        }

        RenderTexture prevTarget = cam.targetTexture;
        CameraClearFlags prevFlags = cam.clearFlags;
        Color prevBackground = cam.backgroundColor;
        int prevMask = cam.cullingMask;

        try
        {
            cam.targetTexture = target;
            cam.cullingMask = effectiveCullingMask;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.Render();
        }
        finally
        {
            foreach (KeyValuePair<Renderer, Material[]> kv in backup)
            {
                if (kv.Key != null)
                    kv.Key.sharedMaterials = kv.Value;
            }

            cam.targetTexture = prevTarget;
            cam.clearFlags = prevFlags;
            cam.backgroundColor = prevBackground;
            cam.cullingMask = prevMask;
        }
    }

    private static Material EnsureObbDepthPassMaterial(Shader depthShader, float maxDepth)
    {
        if (s_obbDepthPassMaterial == null || s_obbDepthPassMaterial.shader != depthShader)
        {
            DestroyObbShareableMaterial(ref s_obbDepthPassMaterial);
            s_obbDepthPassMaterial = new Material(depthShader)
            {
                hideFlags = HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor
            };
        }

        s_obbDepthPassMaterial.SetFloat("_MaxDepth", maxDepth);
        return s_obbDepthPassMaterial;
    }

    private static Material EnsureObbMaskPassMaterial(Shader maskShader)
    {
        if (s_obbMaskPassMaterial == null || s_obbMaskPassMaterial.shader != maskShader)
        {
            DestroyObbShareableMaterial(ref s_obbMaskPassMaterial);
            s_obbMaskPassMaterial = new Material(maskShader)
            {
                hideFlags = HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor
            };
        }

        return s_obbMaskPassMaterial;
    }

    private static void DestroyObbShareableMaterial(ref Material mat)
    {
        if (mat == null)
            return;
#if UNITY_EDITOR
        if (!Application.isPlaying)
            UnityEngine.Object.DestroyImmediate(mat);
        else
            UnityEngine.Object.Destroy(mat);
#else
        UnityEngine.Object.Destroy(mat);
#endif
        mat = null;
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
