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

    [Header("Remote HTTP (later)")]
    public string remoteUrl = "http://127.0.0.1:8000/generate";
    public int remoteTimeoutSeconds = 60;
}
