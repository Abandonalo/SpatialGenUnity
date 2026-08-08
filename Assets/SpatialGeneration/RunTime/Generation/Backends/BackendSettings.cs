using System;
using UnityEngine;

/// <summary>Which implementation <see cref="BackendRegistry"/> hands out.</summary>
public enum BackendKind
{
    /// <summary>The FastAPI router in <c>tools/comfy_router_backend</c> (local or Colab).</summary>
    Router = 0,

    /// <summary>Offline stand-in that echoes proxies back as primitives.</summary>
    Mock = 1
}

/// <summary>Where the router runs. Both presets speak the same HTTP API.</summary>
public enum BackendPreset
{
    /// <summary>Router on localhost, driving a locally installed ComfyUI.</summary>
    Local = 0,

    /// <summary>Router inside the Colab notebook, reached through the zrok tunnel.</summary>
    Colab = 1
}

/// <summary>Image-to-3D lifter used by the generation graph.</summary>
public enum GenerationModel
{
    Hunyuan3D21 = 0,
    TripoSR = 1
}

/// <summary>
/// Project-wide backend configuration. Lives at
/// <c>Assets/SpatialGeneration/Resources/SpatialGenerationBackendSettings.asset</c>
/// and is edited through the Spatial Generation window.
/// </summary>
[CreateAssetMenu(menuName = "Spatial Generation/Backend Settings", fileName = "SpatialGenerationBackendSettings")]
public class BackendSettings : ScriptableObject
{
    [Header("Backend")]
    public BackendKind backendKind = BackendKind.Router;
    public BackendPreset backendPreset = BackendPreset.Local;

    [Tooltip("Router origin used by the Local preset, e.g. http://127.0.0.1:8001")]
    public string localRouterUrl = "http://127.0.0.1:8001";

    [Tooltip("Router origin used by the Colab preset, e.g. https://<share>.share.zrok.io")]
    public string colabRouterUrl = "https://comfyuitunnel.share.zrok.io";

    [Header("Local ComfyUI")]
    [Tooltip("Start ComfyUI automatically when Generate finds it down. Local preset only.")]
    public bool autoStartComfy = true;

    [Tooltip("Leave empty to use the installed ComfyUI desktop app. Otherwise a python or " +
             "binary path that serves the ComfyUI API.")]
    public string comfyLaunchCommand = string.Empty;

    [Tooltip("Working directory for comfyLaunchCommand. Ignored when using the desktop app.")]
    public string comfyWorkingDirectory = string.Empty;

    [Tooltip("How long to wait for ComfyUI to answer after launching it. First starts load models.")]
    public int comfyBootTimeoutSeconds = 180;

    [Header("Colab")]
    public GenerationModel generationModel = GenerationModel.Hunyuan3D21;
    public string colabNotebookPath = "notebooks/Colab_ComfyUI.ipynb";
    public string colabNotebookUrl =
        "https://colab.research.google.com/github/Abandonalo/SpatialGenUnity/blob/main/notebooks/Colab_ComfyUI.ipynb";

    [Header("Timeouts (seconds)")]
    [Tooltip("Per-HTTP-request timeout: submit, status poll, download.")]
    public int requestTimeoutSeconds = 60;

    [Tooltip("How long a single generation or refinement may take end to end.")]
    public int executionTimeoutSeconds = 1200;

    [Header("Generation defaults")]
    [Tooltip("Negative values pick a fresh random seed per run.")]
    public int seed = -1;
    public int steps = 30;
    public float cfg = 7f;
    public string sampler = "euler";
    public int captureWidth = 512;
    public int captureHeight = 512;

    [Header("3D lifting")]
    public int geometryResolution = 512;
    public float tripoSrThreshold = 25f;

    [Header("Placement")]
    [Tooltip("Keep the generated mesh's own proportions and fit it inside the proxy. " +
             "Off (default) stretches it to fill the proxy, because reconstruction models " +
             "normalise their output and rarely match the volume you authored.")]
    public bool preserveAssetProportions;

    [Header("Output")]
    [Tooltip("Project-relative folder that downloaded backend outputs are written to.")]
    public string outputFolder = "Assets/SpatialGeneration/GeneratedAssets";

    [Tooltip("Abort generation when constraint validation reports errors.")]
    public bool blockOnValidationErrors = true;

    /// <summary>Router origin for the active preset, without a trailing slash.</summary>
    public string RouterBaseUrl
    {
        get
        {
            string url = backendPreset == BackendPreset.Colab ? colabRouterUrl : localRouterUrl;
            return NormalizeOrigin(url);
        }
    }

    /// <summary>Router endpoint, e.g. <c>Endpoint("generate")</c>.</summary>
    public string Endpoint(string path) => $"{RouterBaseUrl}/{path.TrimStart('/')}";

    /// <summary>Trims trailing slashes and any accidental <c>/generate</c> suffix pasted by the user.</summary>
    public static string NormalizeOrigin(string url)
    {
        string trimmed = (url ?? string.Empty).Trim().TrimEnd('/');
        const string generateSuffix = "/generate";
        if (trimmed.EndsWith(generateSuffix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^generateSuffix.Length];
        return trimmed;
    }
}
