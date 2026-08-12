using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using SpatialGeneration.Generation.Backends;
using SpatialGeneration.Utils;

/// <summary>
/// Runs a region-scoped refinement.
///
/// The user's selection box is the unit of work throughout: the canonical cameras frame it,
/// the inpaint mask is its projection, the lifted mesh covers it, and the editor splices that
/// mesh back in place of the box's contents. Geometry outside the box is never regenerated,
/// which is what lets scene stability be measured rather than asserted.
/// </summary>
[ExecuteAlways]
public class RefinementController : MonoBehaviour
{
    /// <summary>How much scene context around the selection the cameras include.</summary>
    private const float ContextPadding = 1.6f;

    public RegionSelectionManager selectionManager;

    [Header("Multi-view rig")]
    [Tooltip("Created automatically on first use if left empty.")]
    public MultiViewRenderer multiViewRenderer;

    [Tooltip("View whose refined image is lifted back to 3D.")]
    public ViewType reconstructionView = ViewType.Front;

    [Header("Sampling")]
    [Tooltip("Shared by every view so the four inpaints stay mutually consistent. -1 randomises per run.")]
    public int seed = 1234567;

    public int steps = RefinementDefaults.Steps;
    public float cfgScale = RefinementDefaults.Cfg;

    [SerializeField] private string sessionId;
    private bool isRunning;

    public bool IsRunning => isRunning;

    /// <summary>
    /// Raised when a refined region mesh is on disk. The editor-side loader returns true once
    /// it has spliced the mesh into the scene.
    /// </summary>
    public static event Func<RefinedMeshContext, bool> RefinedMeshReady;

    /// <summary>Clears a stuck busy flag after a domain reload or an interrupted run.</summary>
    public void ClearRunningState() => isRunning = false;

    public async void RunRefinement(string localPrompt)
    {
        if (isRunning)
        {
            Debug.LogWarning("Spatial Generation: a refinement is already running.");
            return;
        }

        RegionSelection selection = selectionManager != null ? selectionManager.CurrentSelection : null;
        if (selection == null)
        {
            Debug.LogWarning("Spatial Generation: no region is selected.");
            return;
        }

        if (string.IsNullOrWhiteSpace(localPrompt))
        {
            Debug.LogWarning("Spatial Generation: describe the change you want before refining.");
            return;
        }

        isRunning = true;
        try
        {
            await RunRefinementAsync(selection.Clone(), localPrompt.Trim());
        }
        catch (Exception ex)
        {
            Debug.LogError($"Spatial Generation refinement failed: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            isRunning = false;
        }
    }

    private async Task RunRefinementAsync(RegionSelection selection, string localPrompt)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            sessionId = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";

        BackendSettings settings = BackendRegistry.Settings;
        await BackendRegistry.Current.EnsureReadyAsync();

        EnsureMultiViewRig(selection);
        MultiViewData views = multiViewRenderer.RenderAllViews(selection);

        var request = new MultiViewRefinementRequest
        {
            requestId = Guid.NewGuid().ToString("N"),
            sessionId = sessionId,
            positivePrompt = localPrompt,
            seed = seed >= 0 ? seed : UnityEngine.Random.Range(0, int.MaxValue),
            steps = Mathf.Max(1, steps),
            cfg = Mathf.Max(0f, cfgScale),
            denoise = RefinementDefaults.Denoise,
            lifter = LifterId(settings.refinementLifter),
            allowFallback = settings.refinementLifter == RefinementLifter.Auto,
            reconstructionView = reconstructionView.ToString(),
            views = views.views
        };

        ApplyReconstructionCrop(request, selection);

        string artifactDir = WriteRequestArtifacts(request, views);

        MultiViewRefinementResponse response = await SubmitAsync(request, settings);
        if (!response.success)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(response.errorMessage)
                ? "Refinement failed without a reported reason."
                : response.errorMessage);
        }

        // Keep the refined 2D view next to the mesh: it is the record of what the model
        // actually painted, and the first thing to look at when a result surprises you.
        var refinedPaths = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (RefinedViewResult refined in response.refinedViews)
        {
            string path = Path.Combine(artifactDir, $"{refined.viewType}_refined.png");
            WriteBase64Png(refined.refinedImageBase64, path);
            if (File.Exists(path))
                refinedPaths[refined.viewType] = path;
        }

        foreach (string warning in response.warnings ?? new System.Collections.Generic.List<string>())
            Debug.LogWarning($"Spatial Generation refinement: {warning}");

        if (string.IsNullOrWhiteSpace(response.meshBase64))
            throw new InvalidOperationException("Refinement returned no mesh; the region cannot be replaced.");

        string meshPath = WriteMeshArtifact(artifactDir, response.requestId, response.meshBase64);
        bool applied = RefinedMeshReady?.Invoke(new RefinedMeshContext
        {
            requestId = response.requestId,
            meshAbsolutePath = meshPath,
            Region = selection,
            Views = BuildViewProjections(views, refinedPaths),
            LifterUsed = response.lifterUsed
        }) ?? false;

        if (!applied)
        {
            Debug.LogWarning(
                $"Spatial Generation: refined mesh saved to {meshPath} but could not be placed. " +
                "Import it manually, or check the console for importer errors.");
            return;
        }

        Debug.Log($"Spatial Generation: refinement '{response.requestId}' applied to region '{selection.selectionId}'.");
    }

    private System.Collections.Generic.List<RefinedViewProjection> BuildViewProjections(
        MultiViewData captured,
        System.Collections.Generic.IReadOnlyDictionary<string, string> refinedPaths)
    {
        var result = new System.Collections.Generic.List<RefinedViewProjection>();
        foreach (ViewData view in captured.views)
        {
            if (!refinedPaths.TryGetValue(view.viewType, out string path) ||
                !Enum.TryParse(view.viewType, true, out ViewType type))
                continue;

            Camera camera = multiViewRenderer.cameraManager.GetCamera(type);
            if (camera == null)
                continue;

            result.Add(new RefinedViewProjection
            {
                viewType = view.viewType,
                imageAbsolutePath = path,
                camera = camera,
                worldToCameraMatrix = view.cameraWorldToCamera,
                projectionMatrix = view.cameraProjection,
                cameraPosition = view.cameraPosition,
                cameraForward = view.cameraForward,
                hasStoredProjection = true,
                cropMinX = view.cropMinX,
                cropMinY = view.cropMinY,
                cropMaxX = view.cropMaxX,
                cropMaxY = view.cropMaxY,
                flipVertical = multiViewRenderer.flipVerticalOnReadback
            });
        }
        return result;
    }

    private static string LifterId(RefinementLifter lifter) => lifter switch
    {
        RefinementLifter.Hunyuan3D2MV => "hunyuan3d_2mv",
        RefinementLifter.TripoSR => "tripo_sr",
        _ => "auto"
    };

    /// <summary>
    /// Tells the router which part of the refined image to lift.
    ///
    /// The cameras deliberately include context around the selection so the inpaint has
    /// something to blend against, but lifting that whole frame would produce a mesh covering
    /// the neighbours too. Cropping to the selection's own footprint keeps the reconstruction
    /// scoped to the region the user asked about.
    /// </summary>
    private void ApplyReconstructionCrop(MultiViewRefinementRequest request, RegionSelection selection)
    {
        Camera camera = multiViewRenderer.cameraManager.GetCamera(reconstructionView);
        if (camera == null || !selection.TryGetViewportUvBounds(camera, out Vector4 uv))
            return;

        request.cropMinX = uv.x;
        request.cropMinY = uv.y;
        request.cropMaxX = uv.z;
        request.cropMaxY = uv.w;
    }

    private static async Task<MultiViewRefinementResponse> SubmitAsync(
        MultiViewRefinementRequest request, BackendSettings settings)
    {
        string endpoint = settings.Endpoint("refine");
        string body = await RouterClient.PostJsonAsync(
            endpoint, JsonUtility.ToJson(request), settings.requestTimeoutSeconds);

        MultiViewRefinementResponse response = Parse(body);
        if (!response.IsPending)
            return response;

        // The Colab router queues refinements and answers immediately, so poll for the result.
        string statusUrl = $"{endpoint}/{Uri.EscapeDataString(request.requestId)}";
        DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(60, settings.executionTimeoutSeconds));

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(2000);
            response = Parse(await RouterClient.GetStringAsync(statusUrl, settings.requestTimeoutSeconds));
            if (!response.IsPending)
                return response;
        }

        throw new TimeoutException(
            $"Refinement '{request.requestId}' did not finish within {settings.executionTimeoutSeconds}s.");
    }

    private static MultiViewRefinementResponse Parse(string json)
    {
        MultiViewRefinementResponse response = JsonUtility.FromJson<MultiViewRefinementResponse>(json);
        return response ?? throw new InvalidOperationException("Backend returned an empty refinement response.");
    }

    /// <summary>
    /// Creates the canonical camera rig if needed and frames it on the selection.
    ///
    /// Framing on the selection (rather than the whole scene) is what keeps the edit local:
    /// the lifted mesh then covers the region at full capture resolution instead of being a
    /// low-detail reconstruction of everything. The padding leaves enough surrounding
    /// geometry in frame for the inpaint to blend against.
    /// </summary>
    private void EnsureMultiViewRig(RegionSelection selection)
    {
        if (multiViewRenderer == null)
            multiViewRenderer = CreateRig();

        MultiViewCameraManager cameras = multiViewRenderer.cameraManager;
        if (cameras == null)
        {
            cameras = ComponentUtils.GetOrAdd<MultiViewCameraManager>(multiViewRenderer.gameObject);
            multiViewRenderer.cameraManager = cameras;
        }

        Bounds region = selection.GetWorldBounds();
        float extent = Mathf.Max(region.size.x, Mathf.Max(region.size.y, region.size.z));
        float distance = Mathf.Max(1f, extent * 3f);

        cameras.captureResolution = new Vector2Int(RefinementDefaults.ViewResolution, RefinementDefaults.ViewResolution);
        cameras.orthographicSize = Mathf.Max(0.05f, extent * 0.5f * ContextPadding);
        cameras.rigTarget = region.center;
        cameras.rigDistance = distance;
        cameras.nearClip = 0.01f;
        cameras.farClip = distance + extent * 6f;

        // Orient the rig with the selection so "Front" means the region's own front face.
        cameras.rigRotation = selection.rotation;
        cameras.ApplyLayout();
    }

    private MultiViewRenderer CreateRig()
    {
        const string rigName = "MultiViewRig";
        Transform existing = transform.Find(rigName);
        GameObject rig = existing != null ? existing.gameObject : new GameObject(rigName);
        if (existing == null)
            rig.transform.SetParent(transform, worldPositionStays: false);

        MultiViewCameraManager cameras = ComponentUtils.GetOrAdd<MultiViewCameraManager>(rig);
        MultiViewRenderer renderer = ComponentUtils.GetOrAdd<MultiViewRenderer>(rig);
        renderer.cameraManager = cameras;
        return renderer;
    }

    private string WriteRequestArtifacts(MultiViewRefinementRequest request, MultiViewData views)
    {
        string artifactDir = Path.Combine(
            Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
            "Logs", "Refinement", sessionId, request.requestId);
        Directory.CreateDirectory(artifactDir);

        // The base64 payloads would bloat the JSON past anything readable, so views are
        // written out as PNGs beside a payload-free copy of the request.
        MultiViewRefinementRequest summary = request.WithoutViewPayloads();
        File.WriteAllText(Path.Combine(artifactDir, "request.json"), JsonUtility.ToJson(summary, true));

        foreach (ViewData view in views.views)
        {
            WriteBase64Png(view.rgbBase64, Path.Combine(artifactDir, $"{view.viewType}_rgb.png"));
            WriteBase64Png(view.depthBase64, Path.Combine(artifactDir, $"{view.viewType}_depth.png"));
            WriteBase64Png(view.edgesBase64, Path.Combine(artifactDir, $"{view.viewType}_edges.png"));
            WriteBase64Png(view.maskBase64, Path.Combine(artifactDir, $"{view.viewType}_mask.png"));
        }

        return artifactDir;
    }

    /// <summary>
    /// Stages the .glb outside <c>Assets/</c>. Writing it under Assets would make Unity import
    /// it and mint a .meta, which the editor's staging copy would then duplicate into a second
    /// folder and trigger a GUID conflict.
    /// </summary>
    private static string WriteMeshArtifact(string artifactDir, string requestId, string meshBase64)
    {
        string fileName = string.IsNullOrWhiteSpace(requestId) ? "refined_region" : requestId;
        string path = Path.Combine(artifactDir, $"{fileName}.glb");
        File.WriteAllBytes(path, Convert.FromBase64String(meshBase64));
        return path;
    }

    private static void WriteBase64Png(string base64, string path)
    {
        if (string.IsNullOrWhiteSpace(base64))
            return;

        try
        {
            File.WriteAllBytes(path, Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            // Debug artifacts must never take down a run.
        }
    }

    private void Reset() => selectionManager = GetComponent<RegionSelectionManager>();

    private void OnValidate()
    {
        if (selectionManager == null)
            selectionManager = GetComponent<RegionSelectionManager>();
    }
}
