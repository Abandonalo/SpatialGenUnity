using System;
using UnityEngine;

[ExecuteAlways]
public class RegionMaskRenderer : MonoBehaviour
{
    /// <summary>
    /// Always wired into <c>Hidden/SpatialGen/RegionSelectionMask</c> for exported inpaint masks.
    /// A non-zero world-up gate is smooth, then the pipeline binarizes at 0.5 — which hollows interiors
    /// on some canonical views (e.g. Top). Inspector <see cref="maskFavorUpwardNormals"/> is unrelated to GPU.
    /// </summary>
    public const float MaskExportFavorUpwardNormals = 0f;

    /// <summary>
    /// Axis-aligned bounds in viewport UV [0,1] of OBB corners in front of <paramref name="cam"/>.
    /// </summary>
    public static bool TryGetSelectionViewportUvBounds(Camera cam, RegionSelection selection, out Vector4 minMaxXy)
    {
        minMaxXy = default;
        if (cam == null || selection == null)
            return false;

        Vector3[] corners = RegionSelectionManager.GetWorldCorners(selection);
        if (corners == null || corners.Length == 0)
            return false;

        bool any = false;
        float minX = 1f;
        float minY = 1f;
        float maxX = 0f;
        float maxY = 0f;
        for (int i = 0; i < corners.Length; i++)
        {
            Vector3 vp = cam.WorldToViewportPoint(corners[i]);
            if (vp.z <= 0f)
                continue;
            any = true;
            minX = Mathf.Min(minX, vp.x);
            minY = Mathf.Min(minY, vp.y);
            maxX = Mathf.Max(maxX, vp.x);
            maxY = Mathf.Max(maxY, vp.y);
        }

        if (!any)
            return false;

        minX = Mathf.Clamp01(minX);
        minY = Mathf.Clamp01(minY);
        maxX = Mathf.Clamp01(maxX);
        maxY = Mathf.Clamp01(maxY);
        if (maxX <= minX || maxY <= minY)
            return false;

        minMaxXy = new Vector4(minX, minY, maxX, maxY);
        return true;
    }

    private const string DepthShaderName = "Hidden/SpatialGen/EncodeLinearDepth";
    private const string MaskShaderName = "Hidden/SpatialGen/RegionSelectionMask";

    public Camera renderCamera;

    public RenderTexture rgbRT;
    public RenderTexture depthRT;
    public RenderTexture maskRT;

    [Tooltip("Logged only: exported mask pass always sends 0 to the shader (see MaskExportFavorUpwardNormals).")]
    [Range(0f, 1f)]
    public float maskFavorUpwardNormals = 0f;

    [Header("Depth-aware mask (prepass linear depth)")]
    [Tooltip("Max |Δ| in normalized linear depth (EncodeLinearDepth 0..1) vs fragment. Try 0.002–0.008 if noisy.")]
    public float depthMaskEpsilon = 0.004f;

    [Tooltip("0=binary mask (inpaint). 1–3=depth debug (no binarize — do not send to Comfy).")]
    [Range(0f, 3f)]
    public float maskDebugView = 0f;

    private Camera _driverCamera;

    public Texture2D RenderRGB()
    {
        Camera cameraToUse = PrepareCamera();
        RenderTexture target = EnsureTarget(ref rgbRT, RenderTextureFormat.ARGB32);
        RenderScene(cameraToUse, target, null);
        Texture2D tex = ReadTexture(target, TextureFormat.RGBA32);
        return tex;
    }

    public Texture2D RenderDepth()
    {
        Camera cameraToUse = PrepareCamera();
        Shader depthShader = Shader.Find(DepthShaderName);
        if (depthShader == null)
            throw new System.InvalidOperationException($"Depth shader '{DepthShaderName}' not found.");

        Shader.SetGlobalFloat("_MaxDepth", Mathf.Max(0.01f, cameraToUse.farClipPlane));
        RenderTexture target = EnsureTarget(ref depthRT, RenderTextureFormat.ARGB32);
        RenderScene(cameraToUse, target, depthShader);
        Texture2D tex = ReadTexture(target, TextureFormat.RGBA32);
        return tex;
    }

    public Texture2D RenderMask(RegionSelection selection)
    {
        if (selection == null)
            throw new System.ArgumentNullException(nameof(selection));

        Camera cameraToUse = PrepareCamera();
        Shader depthShader = Shader.Find(DepthShaderName);
        Shader maskShader = Shader.Find(MaskShaderName);
        if (depthShader == null)
            throw new System.InvalidOperationException($"Depth shader '{DepthShaderName}' not found.");
        if (maskShader == null)
            throw new System.InvalidOperationException($"Mask shader '{MaskShaderName}' not found.");

        Matrix4x4 worldToSelection = Matrix4x4.TRS(
            selection.center,
            selection.rotation,
            Vector3.one).inverse;

        Vector4 halfExtentsGlobal = ClampHalfExtents(selection.size * 0.5f);
        float maxDepth = Mathf.Max(0.01f, cameraToUse.farClipPlane);
        RenderTexture depthTarget = EnsureTarget(ref depthRT, RenderTextureFormat.ARGB32);
        RenderTexture maskTarget = EnsureTarget(ref maskRT, RenderTextureFormat.ARGB32);

        try
        {
            Shader.SetGlobalFloat("_MaxDepth", maxDepth);
            RenderScene(cameraToUse, depthTarget, depthShader);

            Shader.SetGlobalTexture("_RegionMaskSceneDepthTex", depthTarget);
            Shader.SetGlobalFloat("_DepthMaskEpsilon", Mathf.Max(1e-6f, depthMaskEpsilon));
            Shader.SetGlobalFloat("_RegionMaskUseDepthTest", 1f);
            Shader.SetGlobalFloat("_MaskDebugView", maskDebugView);

            if (TryGetSelectionViewportUvBounds(cameraToUse, selection, out Vector4 uvBounds))
            {
                Shader.SetGlobalVector("_MaskViewportUvMinMax", uvBounds);
                Shader.SetGlobalFloat("_RegionMaskUseViewportClip", 1f);
            }
            else
                Shader.SetGlobalFloat("_RegionMaskUseViewportClip", 0f);

            Shader.SetGlobalMatrix("_SelectionWorldToLocal", worldToSelection);
            Shader.SetGlobalVector("_SelectionHalfExtents", halfExtentsGlobal);
            Shader.SetGlobalFloat("_MaskFavorUpwardNormals", MaskExportFavorUpwardNormals);
            Shader.SetGlobalFloat("_MaxDepth", maxDepth);

            RenderScene(cameraToUse, maskTarget, maskShader);

            Texture2D mask = ReadTexture(maskTarget, TextureFormat.RGBA32);
            if (maskDebugView < 0.25f)
                Binarize(mask);
            return mask;
        }
        finally
        {
            Shader.SetGlobalTexture("_RegionMaskSceneDepthTex", null);
            Shader.SetGlobalFloat("_RegionMaskUseDepthTest", 0f);
            Shader.SetGlobalFloat("_RegionMaskUseViewportClip", 0f);
            Shader.SetGlobalFloat("_MaskDebugView", 0f);
        }
    }

    public void SetupCamera(Bounds bounds)
    {
        Camera source = ResolveSourceCamera();
        EnsureDriverCamera(source);

        _driverCamera.CopyFrom(source);
        _driverCamera.enabled = false;
        _driverCamera.allowHDR = false;
        _driverCamera.allowMSAA = false;

        Vector2Int resolution = ResolveResolution(source);
        EnsureTarget(ref rgbRT, RenderTextureFormat.ARGB32, resolution.x, resolution.y);
        EnsureTarget(ref depthRT, RenderTextureFormat.ARGB32, resolution.x, resolution.y);
        EnsureTarget(ref maskRT, RenderTextureFormat.ARGB32, resolution.x, resolution.y);

        if (_driverCamera.orthographic)
        {
            ConfigureOrthographicCamera(_driverCamera, source, bounds);
            return;
        }

        ConfigurePerspectiveCamera(_driverCamera, source, bounds);
    }

    private Camera PrepareCamera()
    {
        Camera source = ResolveSourceCamera();
        if (_driverCamera == null)
            SetupCamera(new Bounds(source.transform.position + source.transform.forward * 2f, Vector3.one));

        return _driverCamera;
    }

    private Camera ResolveSourceCamera()
    {
        if (renderCamera != null)
            return renderCamera;

        renderCamera = Camera.main;
        if (renderCamera != null)
            return renderCamera;

        Camera[] cameras = FindObjectsByType<Camera>(FindObjectsSortMode.None);
        if (cameras != null && cameras.Length > 0)
        {
            renderCamera = cameras[0];
            return renderCamera;
        }

        throw new System.InvalidOperationException("No camera available for region refinement rendering.");
    }

    private void EnsureDriverCamera(Camera source)
    {
        if (_driverCamera != null)
            return;

        GameObject driver = new GameObject("RegionMaskRendererCamera");
        driver.hideFlags = HideFlags.HideAndDontSave;
        driver.transform.SetParent(transform, false);
        _driverCamera = driver.AddComponent<Camera>();
        _driverCamera.enabled = false;
        _driverCamera.CopyFrom(source);
    }

    private void ConfigurePerspectiveCamera(Camera driverCamera, Camera source, Bounds bounds)
    {
        Vector3 forward = source.transform.forward;
        Vector3 up = source.transform.up;
        Vector3 right = source.transform.right;

        Vector3[] corners = RegionSelectionManager.GetWorldCorners(new RegionSelection
        {
            center = bounds.center,
            size = bounds.size,
            rotation = Quaternion.identity
        });

        float verticalHalfFov = Mathf.Max(0.01f, source.fieldOfView * Mathf.Deg2Rad * 0.5f);
        float horizontalHalfFov = Mathf.Atan(Mathf.Tan(verticalHalfFov) * Mathf.Max(0.01f, source.aspect));

        float maxRight = 0f;
        float maxUp = 0f;
        float maxForward = 0f;
        for (int i = 0; i < corners.Length; i++)
        {
            Vector3 delta = corners[i] - bounds.center;
            maxRight = Mathf.Max(maxRight, Mathf.Abs(Vector3.Dot(delta, right)));
            maxUp = Mathf.Max(maxUp, Mathf.Abs(Vector3.Dot(delta, up)));
            maxForward = Mathf.Max(maxForward, Mathf.Abs(Vector3.Dot(delta, forward)));
        }

        float distanceForWidth = maxRight / Mathf.Tan(horizontalHalfFov);
        float distanceForHeight = maxUp / Mathf.Tan(verticalHalfFov);
        float distance = Mathf.Max(distanceForWidth, distanceForHeight) + maxForward + 0.25f;
        distance = Mathf.Max(distance, source.nearClipPlane + 0.25f);

        driverCamera.transform.position = bounds.center - forward * distance;
        driverCamera.transform.rotation = Quaternion.LookRotation(forward, up);
        driverCamera.nearClipPlane = Mathf.Max(0.01f, distance - maxForward - 1f);
        driverCamera.farClipPlane = Mathf.Max(driverCamera.nearClipPlane + 10f, distance + maxForward + 10f);
    }

    private static void ConfigureOrthographicCamera(Camera driverCamera, Camera source, Bounds bounds)
    {
        Vector3 forward = source.transform.forward;
        Vector3 up = source.transform.up;
        Vector3 right = source.transform.right;
        Vector3 extents = bounds.extents;

        float halfHeight = Mathf.Max(
            Mathf.Abs(Vector3.Dot(extents, Vector3.right)) + Mathf.Abs(Vector3.Dot(extents, Vector3.forward)),
            Mathf.Abs(Vector3.Dot(extents, up)));
        float halfWidth = Mathf.Max(
            Mathf.Abs(Vector3.Dot(extents, Vector3.up)) + Mathf.Abs(Vector3.Dot(extents, Vector3.forward)),
            Mathf.Abs(Vector3.Dot(extents, right)));

        driverCamera.transform.position = bounds.center - forward * 10f;
        driverCamera.transform.rotation = Quaternion.LookRotation(forward, up);
        driverCamera.orthographicSize = Mathf.Max(halfHeight, halfWidth / Mathf.Max(0.01f, source.aspect)) + 0.25f;
        driverCamera.nearClipPlane = 0.01f;
        driverCamera.farClipPlane = 1000f;
    }

    private void RenderScene(Camera cameraToUse, RenderTexture target, Shader overrideShader)
    {
        int previousMask = cameraToUse.cullingMask;
        CameraClearFlags previousFlags = cameraToUse.clearFlags;
        Color previousBackground = cameraToUse.backgroundColor;
        RenderTexture previousTarget = cameraToUse.targetTexture;

        Camera source = ResolveSourceCamera();
        try
        {
            cameraToUse.targetTexture = target;
            cameraToUse.cullingMask = source.cullingMask;

            if (overrideShader == null)
            {
                // For the RGB pass we want the viewer's actual scene (skybox or
                // whatever the source camera uses) so the model isn't fed a
                // hard-black background that dominates the inpaint result.
                cameraToUse.clearFlags = source.clearFlags;
                cameraToUse.backgroundColor = source.backgroundColor;
                cameraToUse.Render();
            }
            else
            {
                // Depth/mask passes need a solid-black background so fragments
                // outside the selection encode 0.
                cameraToUse.clearFlags = CameraClearFlags.SolidColor;
                cameraToUse.backgroundColor = Color.black;
                cameraToUse.RenderWithShader(overrideShader, string.Empty);
            }
        }
        finally
        {
            cameraToUse.cullingMask = previousMask;
            cameraToUse.clearFlags = previousFlags;
            cameraToUse.backgroundColor = previousBackground;
            cameraToUse.targetTexture = previousTarget;
        }
    }

    private RenderTexture EnsureTarget(ref RenderTexture target, RenderTextureFormat format)
    {
        Camera source = ResolveSourceCamera();
        Vector2Int resolution = ResolveResolution(source);
        return EnsureTarget(ref target, format, resolution.x, resolution.y);
    }

    private static RenderTexture EnsureTarget(ref RenderTexture target, RenderTextureFormat format, int width, int height)
    {
        width = Mathf.Max(64, width);
        height = Mathf.Max(64, height);

        bool needsReplacement = target == null ||
                                target.width != width ||
                                target.height != height ||
                                target.format != format;

        if (!needsReplacement)
            return target;

        if (target != null)
        {
            target.Release();
            UnityEngine.Object.DestroyImmediate(target);
        }

        target = new RenderTexture(width, height, 24, format)
        {
            antiAliasing = 1,
            useMipMap = false,
            autoGenerateMips = false,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        target.Create();
        return target;
    }

    private static Vector2Int ResolveResolution(Camera source)
    {
        BackendSettings settings = BackendRegistry.Settings;
        int width = settings != null ? settings.captureWidth : 0;
        int height = settings != null ? settings.captureHeight : 0;

        if (width <= 0)
            width = source != null ? source.pixelWidth : 512;
        if (height <= 0)
            height = source != null ? source.pixelHeight : 512;

        return new Vector2Int(width, height);
    }

    private static Texture2D ReadTexture(RenderTexture target, TextureFormat textureFormat)
    {
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = target;

        try
        {
            Texture2D texture = new Texture2D(target.width, target.height, textureFormat, false);
            texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            texture.Apply(false, false);
            return texture;
        }
        finally
        {
            RenderTexture.active = previous;
        }
    }

    private static Vector4 ClampHalfExtents(Vector3 halfExtents)
    {
        return new Vector4(
            Mathf.Max(0.005f, Mathf.Abs(halfExtents.x)),
            Mathf.Max(0.005f, Mathf.Abs(halfExtents.y)),
            Mathf.Max(0.005f, Mathf.Abs(halfExtents.z)),
            0f);
    }

    private static void Binarize(Texture2D texture)
    {
        if (texture == null)
            return;

        Color[] pixels = texture.GetPixels();
        for (int i = 0; i < pixels.Length; i++)
        {
            float value = pixels[i].grayscale >= 0.5f ? 1f : 0f;
            pixels[i] = new Color(value, value, value, 1f);
        }

        texture.SetPixels(pixels);
        texture.Apply(false, false);
    }

    private void OnDisable()
    {
        if (_driverCamera != null)
            UnityEngine.Object.DestroyImmediate(_driverCamera.gameObject);
    }
}
