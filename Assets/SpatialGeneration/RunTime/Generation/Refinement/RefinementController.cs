using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

[ExecuteAlways]
public class RefinementController : MonoBehaviour
{
    private const string GeneratedRootName = "GeneratedContent";
    // HttpClient's default timeout is 100s, which is well below the multi-minute
    // TripoSR run time at higher geometry resolutions. Disable the built-in
    // timeout so the only timeout that applies is the CancellationTokenSource we
    // build from BackendSettings.comfyExecutionTimeoutSeconds in SendToBackend.
    private static readonly HttpClient Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

    public RegionSelectionManager selectionManager;
    public RegionMaskRenderer maskRenderer;

    [Header("Multi-View Refinement")]
    [Tooltip("Optional multi-view rig. When assigned, refinement uses RunMultiViewRefinement to produce per-view inpaints.")]
    public MultiViewRenderer multiViewRenderer;
    [Tooltip("Deterministic seed shared across all canonical views during multi-view refinement. " +
             "Use a fixed value (>=0) for reproducible output; -1 picks a random seed once per request.")]
    public int multiViewSeed = 1234567;
    [Tooltip("View whose refined image feeds the TripoSR reconstruction.")]
    public ViewType reconstructionView = ViewType.Front;

    [Range(0f, 1f)] public float denoiseStrength = 1.0f;
    public int steps = 20;
    public float cfgScale = 8f;

    [SerializeField] private string sessionId;
    [SerializeField] private bool isRunning;

    public bool IsRunning => isRunning;

    // Entry point for the 2D-based multi-view refinement pipeline. Renders
    // the selection from every canonical camera (front/left/right/top),
    // bundles per-view RGB/depth/mask into a single request, and sends it
    // to the FastAPI router's /refine_multi_view endpoint. All views share
    // a single fixed seed so the resulting inpaints stay mutually
    // consistent and the reconstruction view's refined image feeds into
    // TripoSR for the final mesh update.
    public async void RunMultiViewRefinement(string globalPrompt, string localPrompt)
    {
        if (isRunning)
        {
            Debug.LogWarning("Spatial Generation: A refinement request is already running.");
            return;
        }

        EnsureReferences();

        RegionSelection selection = selectionManager != null ? selectionManager.CurrentSelection : null;
        if (selection == null)
        {
            Debug.LogWarning("Spatial Generation: No region selection is active.");
            return;
        }

        // Provision (or refresh) the canonical multi-view rig so the four
        // cameras always frame the current selection, regardless of whether
        // the user wired a MultiViewRenderer into the scene by hand.
        EnsureMultiViewRig(selection);

        isRunning = true;
        try
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                sessionId = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";

            MultiViewData views = multiViewRenderer.RenderAllViews(selection);

            // Refinement intent lives entirely in the local prompt. Mixing in
            // the global style prompt dilutes the per-region change the user
            // asked for ("make the roof black"), so we use local-only and
            // fall back to global only if local was left empty.
            string effectivePrompt = !string.IsNullOrWhiteSpace(localPrompt)
                ? localPrompt.Trim()
                : (globalPrompt ?? string.Empty).Trim();

            int seed = multiViewSeed >= 0
                ? multiViewSeed
                : UnityEngine.Random.Range(0, int.MaxValue);

            MultiViewRefinementRequest request = new MultiViewRefinementRequest
            {
                requestId = Guid.NewGuid().ToString("N"),
                sessionId = sessionId,
                mode = "refine",
                positivePrompt = effectivePrompt,
                negativePrompt = string.Empty,
                seed = seed,
                steps = Mathf.Max(1, steps),
                cfg = Mathf.Max(0f, cfgScale),
                denoise = Mathf.Clamp(denoiseStrength, 0.85f, 1f),
                reconstructionView = reconstructionView.ToString(),
                views = views.views,
                selection = CloneSelection(selection)
            };

            WriteMultiViewDebugArtifacts(request);

            IGenerationBackend backend = BackendRegistry.Current;
            if (backend != null)
                await backend.EnsureReadyAsync();

            string responseJson = await SendToBackend(
                JsonUtility.ToJson(request),
                ResolveMultiViewUrl(BackendRegistry.Settings));
            MultiViewRefinementResponse response = JsonUtility.FromJson<MultiViewRefinementResponse>(responseJson);

            if (response == null)
                throw new InvalidOperationException("Backend returned an empty multi-view refinement response.");
            if (!response.success)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(response.errorMessage) ? "Multi-view refinement failed." : response.errorMessage);

            ApplyMultiViewResponse(response, selection);
            Debug.Log($"Spatial Generation: Multi-view refinement '{response.requestId}' completed with {response.refinedViews?.Count ?? 0} views.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Spatial Generation multi-view refinement failed: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            isRunning = false;
        }
    }

    private void ApplyMultiViewResponse(MultiViewRefinementResponse response, RegionSelection selection)
    {
        if (!string.IsNullOrWhiteSpace(response.meshBase64))
        {
            string meshPath = WriteMeshArtifact(response.requestId, response.meshBase64);
            var ctx = new RefinedMeshContext
            {
                requestId = response.requestId,
                meshAbsolutePath = meshPath,
                selectionCenter = selection.center,
                selectionSize = selection.size,
                selectionRotation = selection.rotation
            };
            TryInstantiateRefinedMesh(ctx);
        }

        // Fallback: if no mesh was returned, surface the reconstruction-view
        // refined image through the existing 2D preview path so the user
        // still gets visual feedback.
        if (string.IsNullOrWhiteSpace(response.meshBase64) && response.refinedViews != null)
        {
            string target = reconstructionView.ToString();
            RefinedViewResult chosen = response.refinedViews.Find(v => v.viewType == target);
            if (chosen == null && response.refinedViews.Count > 0)
                chosen = response.refinedViews[0];

            if (chosen != null && !string.IsNullOrWhiteSpace(chosen.refinedImageBase64))
            {
                // Reuse single-view 2D preview, but without a mask composite
                // because the canonical cameras don't share the original
                // request's selection-aligned mask texture.
                ApplyRefinedImage(response.requestId, chosen.refinedImageBase64, selection, null);
            }
        }
    }

    private void WriteMultiViewDebugArtifacts(MultiViewRefinementRequest request)
    {
        try
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string artifactDir = Path.Combine(projectRoot, "Logs", "Refinement", sessionId, request.requestId);
            Directory.CreateDirectory(artifactDir);

            File.WriteAllText(Path.Combine(artifactDir, "multi_view_request.json"), JsonUtility.ToJson(request, true));
            if (request.views == null)
                return;
            for (int i = 0; i < request.views.Count; i++)
            {
                ViewData v = request.views[i];
                if (v == null) continue;
                WriteBase64Png(v.rgbBase64, Path.Combine(artifactDir, $"{v.viewType}_rgb.png"));
                WriteBase64Png(v.depthBase64, Path.Combine(artifactDir, $"{v.viewType}_depth.png"));
                WriteBase64Png(v.maskBase64, Path.Combine(artifactDir, $"{v.viewType}_mask.png"));
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Spatial Generation: Failed to write multi-view debug artifacts: {ex.Message}");
        }
    }

    private static void WriteBase64Png(string base64, string path)
    {
        if (string.IsNullOrWhiteSpace(base64) || string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            File.WriteAllBytes(path, Convert.FromBase64String(base64));
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Spatial Generation: Failed to write artifact {path}: {ex.Message}");
        }
    }

    // Ensure there is a MultiViewRenderer + MultiViewCameraManager provisioned
    // and that the four canonical cameras are positioned to frame the current
    // selection. The rig is created lazily (as a child of this controller) on
    // first use so the user does not have to hand-place cameras, and the
    // framing is refreshed every run because the selection bounds change.
    private void EnsureMultiViewRig(RegionSelection selection)
    {
        if (selection == null)
            throw new ArgumentNullException(nameof(selection));

        if (multiViewRenderer == null)
        {
            GameObject existing = null;
            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);
                if (child != null && child.name == "MultiViewRig")
                {
                    existing = child.gameObject;
                    break;
                }
            }

            GameObject rigGo = existing != null ? existing : new GameObject("MultiViewRig");
            if (existing == null)
                rigGo.transform.SetParent(transform, worldPositionStays: false);

            MultiViewCameraManager mgr = rigGo.GetComponent<MultiViewCameraManager>();
            if (mgr == null)
                mgr = rigGo.AddComponent<MultiViewCameraManager>();

            MultiViewRenderer rend = rigGo.GetComponent<MultiViewRenderer>();
            if (rend == null)
                rend = rigGo.AddComponent<MultiViewRenderer>();
            rend.cameraManager = mgr;

            multiViewRenderer = rend;
        }

        MultiViewCameraManager cm = multiViewRenderer.cameraManager;
        if (cm == null)
        {
            cm = multiViewRenderer.gameObject.GetComponent<MultiViewCameraManager>()
                 ?? multiViewRenderer.gameObject.AddComponent<MultiViewCameraManager>();
            multiViewRenderer.cameraManager = cm;
        }

        // Frame the canonical cameras around the WHOLE generated scene, not
        // the selection. The selection mask (tight to the region) still
        // drives KSampler's SetLatentNoiseMask, so only the selection pixels
        // are inpainted, while the rest of the scene is re-encoded identity.
        // Framing the full scene lets TripoSR lift a refined mesh that spans
        // the entire original geometry instead of the cropped selection.
        Bounds frameBounds = ResolveSceneFramingBounds(selection);

        float maxExtent = Mathf.Max(
            Mathf.Abs(frameBounds.size.x),
            Mathf.Abs(frameBounds.size.y),
            Mathf.Abs(frameBounds.size.z));
        float padding = 1.2f;
        float halfHeight = Mathf.Max(0.1f, maxExtent * 0.5f * padding);
        float distance = Mathf.Max(1f, maxExtent * 2f);

        cm.captureResolution = new Vector2Int(512, 512);
        cm.orthographic = true;
        cm.orthographicSize = halfHeight;
        cm.rigTarget = frameBounds.center;
        cm.rigDistance = distance;
        cm.topHeight = distance;
        cm.nearClip = 0.01f;
        cm.farClip = Mathf.Max(distance + maxExtent * 4f, 1000f);
        cm.AutoSetupCameras();

        Quaternion r = selection.rotation;
        Vector3 c = frameBounds.center;
        PlaceCamera(cm.frontCamera, c + r * new Vector3(0f, 0f, -distance),
                    r * Quaternion.LookRotation(Vector3.forward, Vector3.up));
        PlaceCamera(cm.leftCamera, c + r * new Vector3(-distance, 0f, 0f),
                    r * Quaternion.LookRotation(Vector3.right, Vector3.up));
        PlaceCamera(cm.rightCamera, c + r * new Vector3(distance, 0f, 0f),
                    r * Quaternion.LookRotation(Vector3.left, Vector3.up));
        PlaceCamera(cm.topCamera, c + r * new Vector3(0f, distance, 0f),
                    r * Quaternion.LookRotation(Vector3.down, Vector3.forward));
    }

    // Combined bounds of the existing Generated_Mesh* under GeneratedContent,
    // falling back to the selection when no scene is present. This is the
    // framing target for the canonical multi-view cameras: we want TripoSR
    // to lift the entire original scene so the refined mesh can REPLACE
    // the original, with only the selected region's content changed.
    private static Bounds ResolveSceneFramingBounds(RegionSelection selection)
    {
        GameObject root = GameObject.Find(GeneratedRootName);
        if (root != null)
        {
            Renderer[] rs = root.GetComponentsInChildren<Renderer>(true);
            Bounds combined = new Bounds();
            bool has = false;
            for (int i = 0; i < rs.Length; i++)
            {
                Renderer r = rs[i];
                if (r == null) continue;
                string n = r.gameObject.name ?? string.Empty;
                // Skip prior refinement artifacts so repeated runs don't
                // progressively balloon the framing bounds.
                if (n.StartsWith("Refined_", StringComparison.Ordinal))
                    continue;
                if (!has) { combined = r.bounds; has = true; }
                else combined.Encapsulate(r.bounds);
            }
            if (has && combined.size.sqrMagnitude > 1e-6f)
            {
                // Ensure the selection is fully inside the framing box.
                combined.Encapsulate(new Bounds(selection.center, selection.size));
                return combined;
            }
        }
        return new Bounds(selection.center, selection.size);
    }

    private static void PlaceCamera(Camera cam, Vector3 worldPos, Quaternion worldRot)
    {
        if (cam == null)
            return;
        cam.transform.position = worldPos;
        cam.transform.rotation = worldRot;
    }

    private static string CombinePrompts(string globalPrompt, string localPrompt)
    {
        string g = (globalPrompt ?? string.Empty).Trim();
        string l = (localPrompt ?? string.Empty).Trim();
        if (g.Length == 0)
            return l;
        if (l.Length == 0)
            return g;
        return $"{g}, {l}";
    }

    private static RegionSelection CloneSelection(RegionSelection selection)
    {
        if (selection == null)
            return null;
        return new RegionSelection
        {
            selectionId = selection.selectionId,
            center = selection.center,
            size = selection.size,
            rotation = selection.rotation
        };
    }

    private static string ResolveMultiViewUrl(BackendSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings?.remoteUrl))
        {
            throw new InvalidOperationException(
                "Multi-view refinement requires the FastAPI router. Set BackendSettings.remoteUrl to the /generate URL.");
        }

        string baseUrl = settings.remoteUrl.TrimEnd('/');
        if (baseUrl.EndsWith("/generate", StringComparison.OrdinalIgnoreCase))
            return $"{baseUrl.Substring(0, baseUrl.Length - "/generate".Length)}/refine_multi_view";
        return $"{baseUrl}/refine_multi_view";
    }

    public async void RunRefinement(string globalPrompt, string localPrompt)
    {
        if (isRunning)
        {
            Debug.LogWarning("Spatial Generation: A refinement request is already running.");
            return;
        }

        EnsureReferences();
        RegionSelection selection = selectionManager != null ? selectionManager.CurrentSelection : null;
        if (selection == null)
        {
            Debug.LogWarning("Spatial Generation: No region selection is active.");
            return;
        }

        isRunning = true;

        Texture2D rgb = null;
        Texture2D depth = null;
        Texture2D mask = null;

        try
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                sessionId = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";

            maskRenderer.SetupCamera(selectionManager.GetWorldBounds());

            rgb = maskRenderer.RenderRGB();
            depth = maskRenderer.RenderDepth();
            mask = maskRenderer.RenderMask(selection);

            RefinementRequest request = RefinementRequestBuilder.Build(
                globalPrompt,
                localPrompt,
                rgb,
                depth,
                mask,
                selection,
                sessionId,
                denoiseStrength,
                steps,
                cfgScale);

            WriteDebugArtifacts(request, rgb, depth, mask);

            IGenerationBackend backend = BackendRegistry.Current;
            if (backend != null)
                await backend.EnsureReadyAsync();

            string responseJson = await SendToBackend(JsonUtility.ToJson(request));
            RefinementResponse response = JsonUtility.FromJson<RefinementResponse>(responseJson);
            if (response == null)
                throw new InvalidOperationException("Backend returned an empty refinement response.");

            if (!response.success)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(response.errorMessage) ? "Refinement failed." : response.errorMessage);

            ApplyResponse(response, selection, mask);
            Debug.Log($"Spatial Generation: Refinement '{response.requestId}' completed successfully.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Spatial Generation refinement failed: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            isRunning = false;

            if (rgb != null)
                UnityEngine.Object.DestroyImmediate(rgb);
            if (depth != null)
                UnityEngine.Object.DestroyImmediate(depth);
            if (mask != null)
                UnityEngine.Object.DestroyImmediate(mask);
        }
    }

    private async Task<string> SendToBackend(string json)
    {
        return await SendToBackend(json, ResolveRefinementUrl(BackendRegistry.Settings));
    }

    private async Task<string> SendToBackend(string json, string endpoint)
    {
        BackendSettings settings = BackendRegistry.Settings;

        int timeoutSeconds = 30;
        if (settings != null)
        {
            timeoutSeconds = Mathf.Max(
                30,
                settings.remoteTimeoutSeconds,
                settings.comfyExecutionTimeoutSeconds);
        }

        using var content = new StringContent(json ?? string.Empty, Encoding.UTF8, "application/json");
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using HttpResponseMessage response = await Http.PostAsync(endpoint, content, timeoutCts.Token);
        string responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Refinement endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}.\n{responseBody}");
        }

        return responseBody;
    }

    private void ApplyResponse(RefinementResponse response, RegionSelection selection, Texture2D maskTexture)
    {
        // The flat-quad preview used to be the primary feedback path, but a 2D
        // plane hanging at selection.center intersects the real 3D geometry and
        // looks like a "cross cut" slice from any non-capture viewing angle.
        // TripoSR returns a full refined mesh via meshBase64 — loading that as
        // the scene's generated mesh produces a coherent 3D result from every
        // angle. Fall back to the image preview only when the mesh path fails
        // (e.g. runtime player, or mesh import unavailable).
        bool meshApplied = false;
        if (!string.IsNullOrWhiteSpace(response.meshBase64))
        {
            string meshPath = WriteMeshArtifact(response.requestId, response.meshBase64);
            var ctx = new RefinedMeshContext
            {
                requestId = response.requestId,
                meshAbsolutePath = meshPath,
                selectionCenter = selection.center,
                selectionSize = selection.size,
                selectionRotation = selection.rotation
            };
            meshApplied = TryInstantiateRefinedMesh(ctx);
        }

        if (!meshApplied && !string.IsNullOrWhiteSpace(response.refinedImageBase64))
            ApplyRefinedImage(response.requestId, response.refinedImageBase64, selection, maskTexture);
    }

    // Raised when a refinement produces a new mesh on disk and the runtime
    // wants the (editor-side) importer to instantiate it into the scene.
    // Editor code subscribes via RefinementMeshLoader. Subscriber must return
    // true when the mesh has been instantiated successfully; false lets the
    // controller fall back to the 2D quad preview.
    public static event Func<RefinedMeshContext, bool> RefinedMeshReady;

    private bool TryInstantiateRefinedMesh(RefinedMeshContext ctx)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ctx.meshAbsolutePath) || !File.Exists(ctx.meshAbsolutePath))
                return false;

            Func<RefinedMeshContext, bool> handler = RefinedMeshReady;
            if (handler == null)
                return false;

            return handler(ctx);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Spatial Generation: Failed to load refined mesh ({ex.Message}); falling back to image preview.");
            return false;
        }
    }

    private void ApplyRefinedImage(string requestId, string imageBase64, RegionSelection selection, Texture2D maskTexture)
    {
        byte[] imageBytes = Convert.FromBase64String(imageBase64);
        Texture2D refinedTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!refinedTexture.LoadImage(imageBytes, false))
        {
            UnityEngine.Object.DestroyImmediate(refinedTexture);
            throw new InvalidOperationException("Refinement image payload could not be decoded.");
        }

        // Use the selection mask as the preview's alpha channel so we overlay
        // only the inpainted region instead of a full opaque rectangle that
        // contains scene/background pixels from the camera capture.
        CompositeMaskAsAlpha(refinedTexture, maskTexture);

        GameObject root = GameObject.Find(GeneratedRootName);
        if (root == null)
            root = new GameObject(GeneratedRootName);

        Transform existing = root.transform.Find("RefinementPreview");
        GameObject preview = existing != null ? existing.gameObject : GameObject.CreatePrimitive(PrimitiveType.Quad);
        preview.name = "RefinementPreview";
        preview.transform.SetParent(root.transform, true);

        if (existing == null)
        {
            Collider collider = preview.GetComponent<Collider>();
            if (collider != null)
                UnityEngine.Object.DestroyImmediate(collider);
        }

        Vector3[] corners = RegionSelectionManager.GetWorldCorners(selection);
        Camera cameraToUse = maskRenderer != null && maskRenderer.renderCamera != null
            ? maskRenderer.renderCamera
            : Camera.main;

        Quaternion rotation = cameraToUse != null
            ? cameraToUse.transform.rotation
            : selection.rotation;
        Vector3 right = rotation * Vector3.right;
        Vector3 up = rotation * Vector3.up;
        Vector3 forward = rotation * Vector3.forward;

        float minX = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float minY = float.PositiveInfinity;
        float maxY = float.NegativeInfinity;
        float meanZ = 0f;

        for (int i = 0; i < corners.Length; i++)
        {
            Vector3 delta = corners[i] - selection.center;
            float x = Vector3.Dot(delta, right);
            float y = Vector3.Dot(delta, up);
            float z = Vector3.Dot(delta, forward);
            minX = Mathf.Min(minX, x);
            maxX = Mathf.Max(maxX, x);
            minY = Mathf.Min(minY, y);
            maxY = Mathf.Max(maxY, y);
            meanZ += z;
        }

        meanZ /= Mathf.Max(1, corners.Length);
        preview.transform.position = selection.center + forward * meanZ;
        preview.transform.rotation = rotation;
        preview.transform.localScale = new Vector3(
            Mathf.Max(0.01f, maxX - minX),
            Mathf.Max(0.01f, maxY - minY),
            1f);

        Renderer renderer = preview.GetComponent<Renderer>();
        // A flat quad at selection.center that uses a normal depth test
        // visually intersects the underlying 3D mesh and looks like a
        // "cross cut" slice of the roof. Force the preview to draw on top
        // of all scene geometry so the refined content replaces the original
        // from any viewing angle.
        Shader shader = Shader.Find("Hidden/SpatialGen/RefinementOverlay");
        if (shader == null)
            shader = Shader.Find("Unlit/Transparent");
        if (shader == null)
            shader = Shader.Find("Sprites/Default");
        if (shader == null)
            shader = Shader.Find("Unlit/Texture");
        if (shader == null)
            shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
            shader = Shader.Find("Standard");

        Material material = renderer.sharedMaterial;
        if (material == null || material.shader != shader)
            material = new Material(shader);
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Overlay;

        material.mainTexture = refinedTexture;
        renderer.sharedMaterial = material;

        preview.hideFlags = HideFlags.None;
        Debug.Log($"Spatial Generation: Applied refinement preview for request '{requestId}'.");
    }

    private static void CompositeMaskAsAlpha(Texture2D refinedTexture, Texture2D maskTexture)
    {
        if (refinedTexture == null || maskTexture == null)
            return;

        Color[] rgba;
        Color[] mask;
        try
        {
            rgba = refinedTexture.GetPixels();
            mask = maskTexture.width == refinedTexture.width && maskTexture.height == refinedTexture.height
                ? maskTexture.GetPixels()
                : maskTexture.GetPixels(); // mismatched sizes are unexpected; log via instrumentation and skip scaling for now
        }
        catch
        {
            return;
        }

        if (rgba == null || mask == null || rgba.Length != mask.Length)
            return;

        for (int i = 0; i < rgba.Length; i++)
            rgba[i].a = Mathf.Clamp01(mask[i].grayscale);

        refinedTexture.SetPixels(rgba);
        refinedTexture.Apply(false, false);
    }

    private string WriteMeshArtifact(string requestId, string meshBase64)
    {
        byte[] meshBytes = Convert.FromBase64String(meshBase64);
        // Stage the .glb OUTSIDE the Assets tree. If we wrote under Assets,
        // Unity would auto-import and create a .meta file with a fresh GUID;
        // the editor's StageFileIntoGeneratedAssets later copies the entire
        // source directory (.glb + .meta) into Assets/.../Generated/run_*,
        // duplicating the .meta and triggering a GUID conflict warning.
        // Writing under Logs/ keeps the .glb outside AssetDatabase so the
        // subsequent copy into Assets imports fresh.
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string safeSession = string.IsNullOrWhiteSpace(sessionId) ? "adhoc" : sessionId;
        string safeReqId = string.IsNullOrWhiteSpace(requestId) ? "refinement_mesh" : requestId;
        string stagingDir = Path.Combine(projectRoot, "Logs", "Refinement", safeSession, safeReqId);
        Directory.CreateDirectory(stagingDir);

        string path = Path.Combine(stagingDir, $"{safeReqId}.glb");
        File.WriteAllBytes(path, meshBytes);

        Debug.Log($"Spatial Generation: Saved refinement mesh artifact to {path}");
        return path;
    }

    private void WriteDebugArtifacts(RefinementRequest request, Texture2D rgb, Texture2D depth, Texture2D mask)
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string artifactDir = Path.Combine(projectRoot, "Logs", "Refinement", sessionId, request.requestId);
        Directory.CreateDirectory(artifactDir);

        File.WriteAllText(Path.Combine(artifactDir, "request.json"), JsonUtility.ToJson(request, true));
        WritePng(rgb, Path.Combine(artifactDir, "rgb.png"));
        WritePng(depth, Path.Combine(artifactDir, "depth.png"));
        WritePng(mask, Path.Combine(artifactDir, "mask.png"));
    }

    private static void WritePng(Texture2D texture, string path)
    {
        if (texture == null || string.IsNullOrWhiteSpace(path))
            return;

        byte[] bytes = texture.EncodeToPNG();
        if (bytes == null || bytes.Length == 0)
            return;

        File.WriteAllBytes(path, bytes);
    }

    private static string ResolveRefinementUrl(BackendSettings settings)
    {
        // /refine is served only by the FastAPI router (see tools/comfy_router_backend/app.py).
        // Falling back to comfyBaseUrl would send the request straight to ComfyUI, which
        // returns 405 because it has no /refine route, so require remoteUrl explicitly.
        if (string.IsNullOrWhiteSpace(settings?.remoteUrl))
        {
            throw new InvalidOperationException(
                "Refinement requires the FastAPI router. Set BackendSettings.remoteUrl to " +
                "the router's /generate URL (e.g. http://127.0.0.1:8001/generate).");
        }

        string baseUrl = settings.remoteUrl.TrimEnd('/');
        if (baseUrl.EndsWith("/generate", StringComparison.OrdinalIgnoreCase))
            return $"{baseUrl.Substring(0, baseUrl.Length - "/generate".Length)}/refine";
        return $"{baseUrl}/refine";
    }

    private void EnsureReferences()
    {
        if (selectionManager == null)
            selectionManager = GetComponent<RegionSelectionManager>();
        if (maskRenderer == null)
            maskRenderer = GetComponent<RegionMaskRenderer>();

        if (selectionManager == null || maskRenderer == null)
        {
            throw new InvalidOperationException(
                "RefinementController requires RegionSelectionManager and RegionMaskRenderer references.");
        }
    }

    private void Reset()
    {
        selectionManager = GetComponent<RegionSelectionManager>();
        maskRenderer = GetComponent<RegionMaskRenderer>();
    }
}
