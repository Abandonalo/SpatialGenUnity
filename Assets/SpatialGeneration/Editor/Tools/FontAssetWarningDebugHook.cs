using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class FontAssetWarningDebugHook
{
    private const string DebugLogPath = "/Users/alo/SpatialGenUnity/.cursor/debug-9a2848.log";
    private const string DebugSessionId = "9a2848";

    static FontAssetWarningDebugHook()
    {
        Application.logMessageReceived -= OnLogMessageReceived;
        Application.logMessageReceived += OnLogMessageReceived;
    }

    private static void OnLogMessageReceived(string condition, string stackTrace, LogType type)
    {
        if (type != LogType.Warning && type != LogType.Error && type != LogType.Exception)
            return;

        if (string.IsNullOrWhiteSpace(condition) ||
            condition.IndexOf("No Font Asset has been assigned", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        string selectedName = Selection.activeGameObject != null ? Selection.activeGameObject.name : string.Empty;
        string selectedType = Selection.activeGameObject != null ? Selection.activeGameObject.GetType().Name : string.Empty;
        bool selectedHasSpatialProxy = Selection.activeGameObject != null &&
                                       Selection.activeGameObject.GetComponent<SpatialProxy>() != null;
        string focusedWindow = EditorWindow.focusedWindow != null ? EditorWindow.focusedWindow.GetType().Name : string.Empty;
        string stackHead = ExtractStackHead(stackTrace, 420);

        // #region agent log
        AppendDebugLog(
            "baseline",
            "H_FONT_1",
            "FontAssetWarningDebugHook.OnLogMessageReceived",
            "Captured missing font asset warning context",
            $"{{\"condition\":\"{EscapeJson(condition)}\",\"focusedWindow\":\"{EscapeJson(focusedWindow)}\",\"selectedName\":\"{EscapeJson(selectedName)}\",\"selectedType\":\"{EscapeJson(selectedType)}\",\"selectedHasSpatialProxy\":{(selectedHasSpatialProxy ? "true" : "false")},\"stackHead\":\"{EscapeJson(stackHead)}\"}}");
        // #endregion
    }

    private static string ExtractStackHead(string stack, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(stack))
            return string.Empty;

        string cleaned = stack.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        if (cleaned.Length > maxChars)
            cleaned = cleaned.Substring(0, maxChars);
        return cleaned;
    }

    private static void AppendDebugLog(string runId, string hypothesisId, string location, string message, string dataJson)
    {
        try
        {
            string safeRunId = EscapeJson(runId ?? "baseline");
            string safeHypothesisId = EscapeJson(hypothesisId ?? string.Empty);
            string safeLocation = EscapeJson(location ?? string.Empty);
            string safeMessage = EscapeJson(message ?? string.Empty);
            string safeDataJson = string.IsNullOrWhiteSpace(dataJson) ? "{}" : dataJson;
            long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string line =
                $"{{\"sessionId\":\"{DebugSessionId}\",\"runId\":\"{safeRunId}\",\"hypothesisId\":\"{safeHypothesisId}\",\"location\":\"{safeLocation}\",\"message\":\"{safeMessage}\",\"data\":{safeDataJson},\"timestamp\":{ts.ToString(CultureInfo.InvariantCulture)}}}";
            File.AppendAllText(DebugLogPath, line + Environment.NewLine);
        }
        catch
        {
            // Ignore logging errors to avoid interrupting editor flow.
        }
    }

    private static string EscapeJson(string value)
    {
        return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
