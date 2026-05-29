using UnityEngine;

public enum BackendKind { Mock, LocalFile, RemoteHttp }

public enum BackendPreset { LocalComfyApi, Colab }

[CreateAssetMenu(menuName = "Spatial Generation/Backend Settings", fileName = "SpatialGenerationBackendSettings")]
public class BackendSettings : ScriptableObject
{
    // Keep runtime defaults minimal; editable values live in Resources/SpatialGenerationBackendSettings.asset.
    public BackendKind backendKind;

    [Header("Backend Preset")]
    public BackendPreset backendPreset;
    public string colabNotebookPath;
    public string colabNotebookUrl;

    [Header("Local File Handoff")]
    public string handoffFolder;
    public string requestFileName;
    public string responseFileName;
    public float pollIntervalSeconds;
    public float maxWaitSeconds;

    [Header("Remote HTTP (Legacy/Fallback)")]
    public string remoteUrl;
    public int remoteTimeoutSeconds;

    [Header("ComfyUI Integration")]
    public bool comfyAutoStart;
    public string comfyBaseUrl;
    public string comfyWsUrl;
    public string comfyClientId;
    public string comfyWorkflowTemplatePath;
    public string comfyInputFolder;
    public string comfyCheckpointName;
    public string comfyTripoSrModelName;
    public int comfyGeometryResolution;
    public float comfyTripoSrThreshold;
    public string comfyOutputAssetFolder;
    public string comfyLaunchCommand;
    public string comfyLaunchArguments;
    public string comfyWorkingDirectory;
    public int comfyBootTimeoutSeconds;
    public int comfyExecutionTimeoutSeconds;

    [Header("Generation Defaults")]
    public int seed;
    public int steps;
    public float cfg;
    public string sampler;
    public int captureWidth;
    public int captureHeight;
    public bool blockOnValidationErrors;
}
