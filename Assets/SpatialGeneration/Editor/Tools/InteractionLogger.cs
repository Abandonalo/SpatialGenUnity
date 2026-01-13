using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class InteractionLogger
{
    private static string _sessionId;
    private static string _jsonlPath;
    private static string _csvPath;
    private static bool _initialized;
    private static bool _initializing;

    private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        if (_initializing) return;

        _initializing = true;

        _sessionId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string dir = Path.Combine(projectRoot, "Logs", "SpatialGenerationLogs");
        Directory.CreateDirectory(dir);

        _jsonlPath = Path.Combine(dir, $"interaction_{_sessionId}.jsonl");
        _csvPath  = Path.Combine(dir, $"interaction_{_sessionId}.csv");

        _initialized = true;

        // Write CSV header (if new)
        if (!File.Exists(_csvPath))
        {
            File.AppendAllText(_csvPath,
                "t,type,proxy_id,role,pos_x,pos_y,pos_z,size_x,size_y,size_z,extra\n");
        }

        // session_start event (write directly, no recursion)
        var start = new InteractionEvent
        {
            type = "session_start",
            session_id = _sessionId,
            t = EditorApplication.timeSinceStartup,
            unity = Application.unityVersion,
            project = Application.productName
        };

        AppendJsonl(start);
        AppendCsv(start);

        _initializing = false;
    }

    public static void Log(InteractionEvent e)
    {
        EnsureInitialized();

        if (string.IsNullOrEmpty(e.session_id)) e.session_id = _sessionId;
        if (e.t <= 0) e.t = EditorApplication.timeSinceStartup;

        AppendJsonl(e);
        AppendCsv(e);
    }

    private static void AppendJsonl(InteractionEvent e)
    {
        try
        {
            string json = JsonUtility.ToJson(e, false);
            File.AppendAllText(_jsonlPath, json + "\n");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"InteractionLogger JSONL failed: {ex.Message}");
        }
    }

    private static void AppendCsv(InteractionEvent e)
    {
        try
        {
            // Keep it compact and readable
            string line =
                $"{e.t.ToString("0.000", CI)}," +
                $"{Safe(e.type)}," +
                $"{Safe(e.proxy_id)}," +
                $"{Safe(e.role)}," +
                $"{e.position.x.ToString("0.###", CI)}," +
                $"{e.position.y.ToString("0.###", CI)}," +
                $"{e.position.z.ToString("0.###", CI)}," +
                $"{e.size.x.ToString("0.###", CI)}," +
                $"{e.size.y.ToString("0.###", CI)}," +
                $"{e.size.z.ToString("0.###", CI)}," +
                $"{Safe(e.extra)}\n";

            File.AppendAllText(_csvPath, line);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"InteractionLogger CSV failed: {ex.Message}");
        }
    }

    private static string Safe(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // Quote if it contains commas/newlines/quotes
        if (s.Contains(",") || s.Contains("\n") || s.Contains("\""))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    public static void RevealLogFolder()
{
    EnsureInitialized();
    EditorUtility.RevealInFinder(Path.GetDirectoryName(_jsonlPath));
}

}
