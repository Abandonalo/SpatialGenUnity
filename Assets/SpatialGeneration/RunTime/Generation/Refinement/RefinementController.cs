using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

[ExecuteAlways]
public class RefinementController : MonoBehaviour
{
    private const string GeneratedRootName = "GeneratedContent";
    private static readonly HttpClient Http = new HttpClient();

    public RegionSelectionManager selectionManager;
    public RegionMaskRenderer maskRenderer;

    [Range(0f, 1f)] public float denoiseStrength = 0.6f;
    public int steps = 20;
    public float cfgScale = 8f;

    [SerializeField] private string sessionId;
    [SerializeField] private bool isRunning;

    public bool IsRunning => isRunning;

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

            string responseJson = await SendToBackend(JsonUtility.ToJson(request));
            RefinementResponse response = JsonUtility.FromJson<RefinementResponse>(responseJson);
            if (response == null)
                throw new InvalidOperationException("Backend returned an empty refinement response.");

            if (!response.success)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(response.errorMessage) ? "Refinement failed." : response.errorMessage);

            ApplyResponse(response, selection);
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
        BackendSettings settings = BackendRegistry.Settings;
        string endpoint = ResolveRefinementUrl(settings);
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException("No refinement backend URL is configured.");

        int timeoutSeconds = 30;
        if (settings != null)
        {
            timeoutSeconds = settings.remoteTimeoutSeconds > 0
                ? settings.remoteTimeoutSeconds
                : Mathf.Max(30, settings.comfyExecutionTimeoutSeconds);
        }
        Http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

        using var content = new StringContent(json ?? string.Empty, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await Http.PostAsync(endpoint, content);
        string responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Refinement endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}.\n{responseBody}");
        }

        return responseBody;
    }

    private void ApplyResponse(RefinementResponse response, RegionSelection selection)
    {
        if (!string.IsNullOrWhiteSpace(response.refinedImageBase64))
            ApplyRefinedImage(response.requestId, response.refinedImageBase64, selection);

        if (!string.IsNullOrWhiteSpace(response.meshBase64))
            WriteMeshArtifact(response.requestId, response.meshBase64);
    }

    private void ApplyRefinedImage(string requestId, string imageBase64, RegionSelection selection)
    {
        byte[] imageBytes = Convert.FromBase64String(imageBase64);
        Texture2D refinedTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!refinedTexture.LoadImage(imageBytes, false))
        {
            UnityEngine.Object.DestroyImmediate(refinedTexture);
            throw new InvalidOperationException("Refinement image payload could not be decoded.");
        }

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
        Shader shader = Shader.Find("Unlit/Texture");
        if (shader == null)
            shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
            shader = Shader.Find("Standard");

        Material material = renderer.sharedMaterial;
        if (material == null || material.shader != shader)
            material = new Material(shader);

        material.mainTexture = refinedTexture;
        renderer.sharedMaterial = material;

        preview.hideFlags = HideFlags.None;
        Debug.Log($"Spatial Generation: Applied refinement preview for request '{requestId}'.");
    }

    private void WriteMeshArtifact(string requestId, string meshBase64)
    {
        byte[] meshBytes = Convert.FromBase64String(meshBase64);
        string dir = Path.Combine(Application.persistentDataPath, "SpatialGeneration", "RefinementMeshes");
        Directory.CreateDirectory(dir);

        string fileName = string.IsNullOrWhiteSpace(requestId) ? "refinement_mesh.glb" : $"{requestId}.glb";
        string path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, meshBytes);

        Debug.Log($"Spatial Generation: Saved refinement mesh artifact to {path}");
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
        if (!string.IsNullOrWhiteSpace(settings?.remoteUrl))
        {
            string baseUrl = settings.remoteUrl.TrimEnd('/');
            if (baseUrl.EndsWith("/generate", StringComparison.OrdinalIgnoreCase))
                return $"{baseUrl.Substring(0, baseUrl.Length - "/generate".Length)}/refine";
            return $"{baseUrl}/refine";
        }

        if (!string.IsNullOrWhiteSpace(settings?.comfyBaseUrl))
            return $"{settings.comfyBaseUrl.TrimEnd('/')}/refine";

        return string.Empty;
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
