using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class InteractionLogger
{
    private static string _sessionId;
    private static string _logFilePath;
    private static bool _initialized;
    private static bool _initializing; // reentrancy guard

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        if (_initializing) return; // prevents recursion if anything calls Log during init

        _initializing = true;

        _sessionId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        // ProjectRoot/Logs/SpatialGenerationLogs/
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string dir = Path.Combine(projectRoot, "Logs", "SpatialGenerationLogs");
        Directory.CreateDirectory(dir);

        _logFilePath = Path.Combine(dir, $"interaction_{_sessionId}.jsonl");

        // Mark initialized BEFORE writing session_start (prevents recursion)
        _initialized = true;

        // Write a session header WITHOUT calling Log()
        AppendLine(JsonUtility.ToJson(new InteractionEvent
        {
            type = "session_start",
            session_id = _sessionId,
            t = EditorApplication.timeSinceStartup,
            unity = Application.unityVersion,
            project = Application.productName
        }));

        _initializing = false;
    }

    public static string CurrentSessionId
    {
        get { EnsureInitialized(); return _sessionId; }
    }

    public static string CurrentLogFilePath
    {
        get { EnsureInitialized(); return _logFilePath; }
    }

    public static void Log(InteractionEvent e)
    {
        EnsureInitialized();

        try
        {
            if (string.IsNullOrEmpty(e.session_id))
                e.session_id = _sessionId;

            if (e.t <= 0)
                e.t = EditorApplication.timeSinceStartup;

            AppendLine(JsonUtility.ToJson(e, false));
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"InteractionLogger failed: {ex.Message}");
        }
    }

    private static void AppendLine(string line)
    {
        // Single append per event; JSONL
        File.AppendAllText(_logFilePath, line + "\n");
    }

    [MenuItem("Tools/Spatial Generation/Open Interaction Log Folder")]
    public static void OpenLogFolder()
    {
        EnsureInitialized();
        EditorUtility.RevealInFinder(Path.GetDirectoryName(_logFilePath));
    }
}
