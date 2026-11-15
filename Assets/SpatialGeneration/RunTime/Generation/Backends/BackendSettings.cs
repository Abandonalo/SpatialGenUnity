using System;
using UnityEngine;

/// <summary>Which implementation <see cref="BackendRegistry"/> hands out.</summary>
public enum BackendKind
{
    /// <summary>The FastAPI router in <c>tools/comfy_router_backend</c> (local or Colab).</summary>
    Router = 0,

    /// <summary>Offline stand-in that echoes proxies back as primitives.</summary>
    Mock = 1,

    /// <summary>
    /// Claude Code or Codex, launched headless, building low-poly assets in the open scene
    /// through MCP for Unity. Driven from the editor by SpatialGeneration.Agent.AgentWorkflow.
    /// </summary>
    Agent = 2
}

/// <summary>Coding-agent CLI the MCP agent backend launches.</summary>
public enum AgentCli
{
    ClaudeCode = 0,
    Codex = 1
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

/// <summary>Image-to-3D implementation used for local region refinement.</summary>
public enum RefinementLifter
{
    /// <summary>Prefer Hunyuan3D-2mv and fall back to TripoSR when it is unavailable.</summary>
    Auto = 0,

    Hunyuan3D2MV = 1,
    TripoSR = 2
}

/// <summary>
/// Project-wide backend configuration. Lives at
/// <c>Assets/SpatialGeneration/Resources/SpatialGenerationBackendSettings.asset</c>
/// and is edited through Tools > Spatial Generation Researcher Tools.
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

    [Header("MCP agent")]
    public AgentCli agentCli = AgentCli.ClaudeCode;

    [Tooltip("Absolute path to the claude executable. Leave empty to search the usual install " +
             "locations; Unity launched from the Dock does not inherit your shell PATH.")]
    public string claudeExecutable = string.Empty;

    [Tooltip("Absolute path to the codex executable. Leave empty to search the usual install locations.")]
    public string codexExecutable = string.Empty;

    [Tooltip("HTTP endpoint of the MCP for Unity server (Window > MCP for Unity).")]
    public string mcpServerUrl = "http://127.0.0.1:8080/mcp";

    [Tooltip("Model passed to the CLI (--model / -m). Empty uses the CLI's default. Freeze this for the study.")]
    public string agentModel = string.Empty;

    [Tooltip("Reasoning effort: empty = CLI default, or low, medium, high, xhigh, max. " +
             "Claude Code gets --effort; Codex gets model_reasoning_effort (max is sent as xhigh).")]
    public string agentEffort = string.Empty;

    [Tooltip("Claude Code --max-turns for one generation run or edit.")]
    public int agentMaxTurns = 80;

    [Tooltip("A run that takes longer is stopped and reported as failed.")]
    public int agentTimeoutSeconds = 600;

    [Tooltip("Upper bound on primitives per asset; the tools reject parts beyond it.")]
    public int maxPartsPerAsset = 60;

    [Tooltip("Extra arguments appended to the claude command line.")]
    public string claudeExtraArguments = string.Empty;

    [Tooltip("Extra arguments appended to the codex exec command line.")]
    public string codexExtraArguments = string.Empty;

    [Header("Study")]
    [Tooltip("Generation runs a participant may start before they must accept a version.")]
    public int maxGenerationRuns = 3;

    [Header("Local processes")]
    [Tooltip("Start the FastAPI router (tools/start_backend.sh) when Generate finds it down. " +
             "Local preset only.")]
    public bool autoStartRouter = true;

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

    [Tooltip("Prepended to every prompt, e.g. \"low poly 3d model of\" + \"a house\". " +
             "A 3D-asset style yields an isolated object; photographic wording brings a whole " +
             "scene with it. Clear this to send the prompt unmodified.")]
    public string assetStyle = "low poly 3d model of";

    [Header("3D lifting")]
    public int geometryResolution = 512;
    public float tripoSrThreshold = 25f;

    [Tooltip("Auto uses Hunyuan3D-2mv when its ComfyUI node and model are installed, then " +
             "falls back to TripoSR without losing the refinement run.")]
    public RefinementLifter refinementLifter = RefinementLifter.Auto;

    [Header("Placement")]
    [Tooltip("Keep the generated mesh's own proportions and fit it inside the proxy. " +
             "Off (default) stretches it to fill the proxy, because reconstruction models " +
             "normalise their output and rarely match the volume you authored.")]
    public bool preserveAssetProportions;

    [Header("Output")]
    [Tooltip("Project-relative folder for raw backend downloads. Kept outside Assets/ so " +
             "Unity does not import every payload; the importer copies what it needs in.")]
    public string outputFolder = "Logs/SpatialGeneration/Downloads";

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
