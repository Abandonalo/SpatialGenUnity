using UnityEngine;

public enum BackendKind { Mock, LocalFile, RemoteHttp }

[CreateAssetMenu(menuName = "Spatial Generation/Backend Settings", fileName = "SpatialGenerationBackendSettings")]
public class BackendSettings : ScriptableObject
{
    public BackendKind backendKind = BackendKind.LocalFile;

    [Header("Local File Handoff")]
    public string handoffFolder = "SpatialGenHandoff";
    public string requestFileName = "request.json";
    public string responseFileName = "response.json";
    public float pollIntervalSeconds = 0.25f;
    public float maxWaitSeconds = 120f;

    [Header("Remote HTTP (Legacy/Fallback)")]
    public string remoteUrl = "http://127.0.0.1:8000/generate";
    public int remoteTimeoutSeconds = 60;

    [Header("ComfyUI Integration")]
    public bool comfyAutoStart = false;
    public string comfyBaseUrl = "http://127.0.0.1:8000";
    public string comfyWsUrl = "ws://127.0.0.1:8000/ws";
    public string comfyClientId = "spatialgen-unity-client";
    public string comfyWorkflowTemplatePath = "SpatialGenHandoff/comfy_workflow_api.json";
    public string comfyInputFolder = "SpatialGenHandoff/comfy_inputs";
    public string comfyCheckpointName = "motiondesignv13dartC4D_v10.safetensors";
    public string comfyDepthControlNetName = "controlnet-depth/diffusion_pytorch_model.safetensors";
    public string comfyCannyControlNetName = "controlnet-canny/diffusion_pytorch_model.safetensors";
    public string comfyTripoSrModelName = "model.safetensors";
    public int comfyGeometryResolution = 256;
    public float comfyTripoSrThreshold = 25f;
    public string comfyOutputAssetFolder = "Assets/SpatialGeneration/GeneratedAssets";
    public string comfyLaunchCommand = "/usr/bin/python3";
    public string comfyLaunchArguments = "main.py --listen 127.0.0.1 --port 8000";
    public string comfyWorkingDirectory = "";
    public int comfyBootTimeoutSeconds = 25;
    public int comfyExecutionTimeoutSeconds = 180;

    [Header("Generation Defaults")]
    public string prompt = "high quality 3d scene";
    public string negativePrompt = "low quality, blurry, artifacts";
    public int seed = -1;
    public int steps = 30;
    public float cfg = 7.0f;
    public string sampler = "euler";
    public int captureWidth = 512;
    public int captureHeight = 512;
    public bool blockOnValidationErrors = true;
}
