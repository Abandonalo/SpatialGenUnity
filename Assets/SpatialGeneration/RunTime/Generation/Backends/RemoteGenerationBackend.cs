using System;
using System.Collections.Generic;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using NewBackendRequest = SpatialGeneration.Generation.Intent.BackendRequest;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class RemoteGenerationBackend : IGenerationBackend
{
    private readonly BackendSettings _settings;
    private static Process _comfyProcess;
    private static readonly HttpClient Http = new HttpClient();
    private static bool _preferHistoryPolling;

    public string Name => "ComfyUI";

    public RemoteGenerationBackend(BackendSettings settings)
    {
        _settings = settings;
    }

    public async Task<GenerationResult> GenerateAsync(NewBackendRequest request)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        string requestId = string.IsNullOrWhiteSpace(request.RequestId)
            ? Guid.NewGuid().ToString("N")
            : request.RequestId;

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string runInputDir = Path.Combine(projectRoot, _settings.comfyInputFolder, requestId);
        Directory.CreateDirectory(runInputDir);
        File.WriteAllText(Path.Combine(runInputDir, "request.json"), JsonUtility.ToJson(request, true));

        await EnsureComfyRunningAsync();

        if (!CanRunWorkflowWithProvidedInputs(projectRoot, out _))
            return ConvertConstraintsToResult(request.LegacyConstraints);

        List<Constraint> occupyConstraints = ExtractConstraintsByType(request?.LegacyConstraints, "occupy");
        int runCount = Mathf.Max(1, occupyConstraints.Count);
        string outputDir = ResolveOutputDirectory(projectRoot);
        var savedOutputs = new List<string>();

        for (int runIndex = 0; runIndex < runCount; runIndex++)
        {
            Constraint occupy = runIndex < occupyConstraints.Count ? occupyConstraints[runIndex] : null;
            string occupyProxyId = !string.IsNullOrWhiteSpace(occupy?.proxy_id)
                ? occupy.proxy_id
                : $"occupy_{runIndex}";
            string runPrompt = ResolveRunPrompt(request, occupyProxyId);
            string meshSourcePrompt = BuildMeshSourcePrompt(runPrompt, occupyProxyId, request);
            string meshSourceNegativePrompt = BuildMeshSourceNegativePrompt(request);
            string perProxyMaskPath = string.Empty;

            if (occupy != null)
            {
                perProxyMaskPath = BuildPerProxyOccupyMaskPath(occupy, request, runInputDir, runIndex);
            }

            string workflowJson = LoadAndBindWorkflow(projectRoot, request, runPrompt, meshSourcePrompt, meshSourceNegativePrompt);
            WriteRunDebugArtifacts(runInputDir, runIndex, occupyProxyId, runPrompt, meshSourcePrompt, meshSourceNegativePrompt, workflowJson);
            string promptId = await SubmitPromptAsync(workflowJson);
            await WaitForPromptCompletionAsync(promptId);

            string runOutputPrefix = $"{requestId}_{SanitizeFileToken(occupyProxyId)}";
            List<string> runSavedOutputs = await DownloadOutputsAsync(promptId, requestId, outputDir, runOutputPrefix);
            string runMeshSourcePath = FindFirstOutputContaining(runSavedOutputs, "spatialgen_mesh_source");
            BuildMaskGuidedMeshSourceImage(runMeshSourcePath, perProxyMaskPath, outputDir, runOutputPrefix);
            savedOutputs.AddRange(runSavedOutputs);
        }

        RefreshAssetDatabaseIfNeeded();

        var result = new GenerationResult();
        result.outputFiles.AddRange(savedOutputs);

        // If ComfyUI finished but produced no downloadable assets, fall back to proxy primitives.
        if (savedOutputs.Count == 0)
        {
            GenerationResult fallback = ConvertConstraintsToResult(request.LegacyConstraints);
            fallback.outputFiles.AddRange(savedOutputs);
            return fallback;
        }

        return result;
    }

    [Serializable]
    private sealed class EffectivePromptDebugArtifact
    {
        public int run_index;
        public string proxy_id;
        public string run_prompt;
        public string mesh_source_prompt;
        public string mesh_source_negative_prompt;
    }

    private static void WriteRunDebugArtifacts(
        string runInputDir,
        int runIndex,
        string occupyProxyId,
        string runPrompt,
        string meshSourcePrompt,
        string meshSourceNegativePrompt,
        string workflowJson)
    {
        if (string.IsNullOrWhiteSpace(runInputDir))
            return;

        try
        {
            string token = SanitizeFileToken(occupyProxyId);
            if (string.IsNullOrWhiteSpace(token))
                token = $"run_{runIndex}";

            var artifact = new EffectivePromptDebugArtifact
            {
                run_index = runIndex,
                proxy_id = occupyProxyId ?? string.Empty,
                run_prompt = runPrompt ?? string.Empty,
                mesh_source_prompt = meshSourcePrompt ?? string.Empty,
                mesh_source_negative_prompt = meshSourceNegativePrompt ?? string.Empty
            };

            string effectivePromptPath = Path.Combine(runInputDir, $"effective_prompt_{token}.json");
            string boundWorkflowPath = Path.Combine(runInputDir, $"bound_workflow_{token}.json");
            File.WriteAllText(effectivePromptPath, JsonUtility.ToJson(artifact, true));
            File.WriteAllText(boundWorkflowPath, workflowJson ?? string.Empty);
        }
        catch
        {
            // Best-effort debug dump; generation must continue.
        }
    }

    private static List<Constraint> ExtractConstraintsByType(Constraint[] constraints, string type)
    {
        var filtered = new List<Constraint>();
        if (constraints == null || string.IsNullOrWhiteSpace(type))
            return filtered;

        string normalizedType = type.Trim().ToLowerInvariant();
        for (int i = 0; i < constraints.Length; i++)
        {
            Constraint c = constraints[i];
            if (c == null)
                continue;
            if ((c.type ?? string.Empty).Trim().ToLowerInvariant() == normalizedType)
                filtered.Add(c);
        }

        return filtered;
    }

    private string BuildPerProxyOccupyMaskPath(Constraint occupyConstraint, NewBackendRequest request, string runInputDir, int runIndex)
    {
        if (occupyConstraint == null || string.IsNullOrWhiteSpace(runInputDir))
            return string.Empty;

        int width = Mathf.Max(64, request?.Payload?.Generation?.Width ?? 512);
        int height = Mathf.Max(64, request?.Payload?.Generation?.Height ?? 512);
        Texture2D texture = null;
        try
        {
            texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            Color[] pixels = new Color[width * height];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = Color.black;

            RectInt pixelRect = new RectInt(0, 0, 0, 0);
            Camera cam = Camera.main;
            bool hasProjectedRect = cam != null && TryProjectConstraintToPixelRect(cam, occupyConstraint, width, height, out pixelRect);
            if (!hasProjectedRect)
            {
                int fallbackW = Mathf.Max(8, width / 6);
                int fallbackH = Mathf.Max(8, height / 6);
                pixelRect = new RectInt((width - fallbackW) / 2, (height - fallbackH) / 2, fallbackW, fallbackH);
            }

            int xMin = Mathf.Clamp(pixelRect.xMin, 0, width - 1);
            int xMax = Mathf.Clamp(pixelRect.xMax, 1, width);
            int yMin = Mathf.Clamp(pixelRect.yMin, 0, height - 1);
            int yMax = Mathf.Clamp(pixelRect.yMax, 1, height);
            for (int y = yMin; y < yMax; y++)
            {
                int row = y * width;
                for (int x = xMin; x < xMax; x++)
                    pixels[row + x] = Color.white;
            }

            texture.SetPixels(pixels);
            texture.Apply(false);
            byte[] png = texture.EncodeToPNG();
            if (png == null || png.Length == 0)
                return string.Empty;

            string proxyToken = SanitizeFileToken(occupyConstraint.proxy_id);
            if (string.IsNullOrWhiteSpace(proxyToken))
                proxyToken = $"occupy_{runIndex}";
            string fileName = $"mask_occupy_{proxyToken}_{runIndex}.png";
            string path = Path.Combine(runInputDir, fileName);
            File.WriteAllBytes(path, png);
            return path;
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            if (texture != null)
                UnityEngine.Object.DestroyImmediate(texture);
        }
    }

    private static bool TryProjectConstraintToPixelRect(Camera cam, Constraint constraint, int width, int height, out RectInt rect)
    {
        rect = new RectInt(0, 0, 0, 0);
        if (cam == null || constraint == null)
            return false;

        Vector3 size = constraint.size;
        Vector3 half = new Vector3(
            Mathf.Max(0.1f, Mathf.Abs(size.x) * 0.5f),
            Mathf.Max(0.1f, Mathf.Abs(size.y) * 0.5f),
            Mathf.Max(0.1f, Mathf.Abs(size.z) * 0.5f));
        Quaternion rotation = constraint.rotation;
        Vector3 center = constraint.position;
        Vector3[] localCorners =
        {
            new Vector3(-half.x, -half.y, -half.z), new Vector3(half.x, -half.y, -half.z),
            new Vector3(-half.x, half.y, -half.z), new Vector3(half.x, half.y, -half.z),
            new Vector3(-half.x, -half.y, half.z), new Vector3(half.x, -half.y, half.z),
            new Vector3(-half.x, half.y, half.z), new Vector3(half.x, half.y, half.z)
        };

        bool projectedAny = false;
        float minX = width - 1;
        float minY = height - 1;
        float maxX = 0f;
        float maxY = 0f;
        for (int i = 0; i < localCorners.Length; i++)
        {
            Vector3 world = center + rotation * localCorners[i];
            Vector3 viewport = cam.WorldToViewportPoint(world);
            if (viewport.z <= 0f)
                continue;

            projectedAny = true;
            float x = Mathf.Clamp(viewport.x * (width - 1), 0f, width - 1);
            float y = Mathf.Clamp(viewport.y * (height - 1), 0f, height - 1);
            minX = Mathf.Min(minX, x);
            minY = Mathf.Min(minY, y);
            maxX = Mathf.Max(maxX, x);
            maxY = Mathf.Max(maxY, y);
        }

        if (!projectedAny)
            return false;

        int pad = Mathf.Max(2, Mathf.RoundToInt(Mathf.Min(width, height) * 0.01f));
        int xMin = Mathf.Clamp(Mathf.FloorToInt(minX) - pad, 0, width - 1);
        int yMin = Mathf.Clamp(Mathf.FloorToInt(minY) - pad, 0, height - 1);
        int xMax = Mathf.Clamp(Mathf.CeilToInt(maxX) + pad, 1, width);
        int yMax = Mathf.Clamp(Mathf.CeilToInt(maxY) + pad, 1, height);
        if (xMax <= xMin || yMax <= yMin)
            return false;

        rect = new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
        return true;
    }

    private static string SanitizeFileToken(string token)
    {
        string value = token ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char ch = value[i];
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-')
                builder.Append(ch);
            else
                builder.Append('_');
        }

        string sanitized = builder.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? string.Empty : sanitized;
    }

    private async Task EnsureComfyRunningAsync()
    {
        if (await IsComfyHealthyAsync())
            return;

        if (!_settings.comfyAutoStart)
            throw new Exception("ComfyUI is not reachable and comfyAutoStart is disabled.");

        bool desktopAlreadyRunning = IsComfyDesktopProcessRunning();
        if (!desktopAlreadyRunning)
            StartComfyProcess();

        float waited = 0f;
        while (waited < _settings.comfyBootTimeoutSeconds)
        {
            await Task.Delay(500);
            waited += 0.5f;
            if (await IsComfyHealthyAsync())
                return;
        }

        throw new Exception($"Timed out waiting for ComfyUI at {GetComfyBaseUrl()}");
    }

    private static bool IsComfyDesktopProcessRunning()
    {
        try
        {
            Process[] processes = Process.GetProcessesByName("ComfyUI");
            return processes != null && processes.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> IsComfyHealthyAsync()
    {
        try
        {
            string url = $"{GetComfyBaseUrl().TrimEnd('/')}/system_stats";
            using var cts = new CancellationTokenSource(Math.Max(1000, _settings.remoteTimeoutSeconds * 1000));
            using HttpResponseMessage response = await Http.GetAsync(url, cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private void StartComfyProcess()
    {
        if (_comfyProcess != null && !_comfyProcess.HasExited)
            return;

        string workingDirectory = ResolveWorkingDirectory();
        if (!string.IsNullOrWhiteSpace(workingDirectory) && !Directory.Exists(workingDirectory))
            throw new Exception($"ComfyUI working directory not found: {workingDirectory}");

        _comfyProcess = TryStartComfyProcess(workingDirectory);
        if (_comfyProcess == null)
            throw new Exception("Failed to start ComfyUI process.");
    }

    private Process TryStartComfyProcess(string workingDirectory)
    {
        var commands = BuildLaunchCommandCandidates(_settings.comfyLaunchCommand);
        Exception lastError = null;

        foreach (string command in commands)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = _settings.comfyLaunchArguments,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                if (!string.IsNullOrWhiteSpace(workingDirectory))
                    psi.WorkingDirectory = workingDirectory;

                Process process = Process.Start(psi);
                if (process != null)
                    return process;
            }
            catch (Win32Exception ex)
            {
                lastError = ex;
                // Continue to fallback candidates if executable is missing.
            }
        }

        string tried = string.Join(", ", commands);
        throw new Exception(
            $"Unable to launch ComfyUI. Tried commands: {tried}. " +
            "Set BackendSettings.comfyLaunchCommand to a valid interpreter path (for macOS usually /usr/bin/python3).",
            lastError);
    }

    private static List<string> BuildLaunchCommandCandidates(string configuredCommand)
    {
        var candidates = new List<string>();
        string comfyDesktopBinary = "/Applications/ComfyUI.app/Contents/MacOS/ComfyUI";
        string primary = string.IsNullOrWhiteSpace(configuredCommand)
            ? (File.Exists(comfyDesktopBinary) ? comfyDesktopBinary : "python3")
            : configuredCommand.Trim();
        candidates.Add(primary);

        // Common fallback on macOS/Linux when "python" is unavailable.
        if (primary.Equals("python", StringComparison.OrdinalIgnoreCase))
        {
            if (!candidates.Contains("python3"))
                candidates.Add("python3");
            if (!candidates.Contains("/usr/bin/python3"))
                candidates.Add("/usr/bin/python3");
        }

        return candidates;
    }

    private string ResolveWorkingDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_settings.comfyWorkingDirectory))
            return _settings.comfyWorkingDirectory;

        string command = (_settings.comfyLaunchCommand ?? string.Empty).Trim();
        if (Path.IsPathRooted(command))
        {
            string commandDir = Path.GetDirectoryName(command);
            if (!string.IsNullOrWhiteSpace(commandDir))
                return commandDir;
        }

        return string.Empty;
    }

    private string LoadAndBindWorkflow(
        string projectRoot,
        NewBackendRequest request,
        string promptOverride = null,
        string meshPromptOverride = null,
        string meshNegativePromptOverride = null)
    {
        string workflowPath = _settings.comfyWorkflowTemplatePath;
        if (!Path.IsPathRooted(workflowPath))
            workflowPath = Path.Combine(projectRoot, workflowPath);

        if (!File.Exists(workflowPath))
            throw new Exception($"ComfyUI workflow file not found: {workflowPath}");

        int seed = request?.Payload?.Generation?.Seed ?? _settings.seed;
        if (seed < 0)
            seed = UnityEngine.Random.Range(0, int.MaxValue);
        int steps = Mathf.Max(1, request?.Payload?.Generation?.Steps ?? _settings.steps);
        float cfg = Mathf.Max(0f, request?.Payload?.Generation?.Cfg ?? _settings.cfg);
        string prompt = string.IsNullOrWhiteSpace(promptOverride)
            ? request?.Prompt ?? string.Empty
            : promptOverride;
        string negativePrompt = request?.NegativePrompt ?? string.Empty;
        string meshPrompt = string.IsNullOrWhiteSpace(meshPromptOverride) ? prompt : meshPromptOverride;
        string meshNegativePrompt = string.IsNullOrWhiteSpace(meshNegativePromptOverride) ? negativePrompt : meshNegativePromptOverride;
        string checkpointName = string.IsNullOrWhiteSpace(_settings.comfyCheckpointName)
            ? "v1-5-pruned-emaonly.safetensors"
            : _settings.comfyCheckpointName.Trim();
        string tripoSrModelName = string.IsNullOrWhiteSpace(_settings.comfyTripoSrModelName)
            ? "TripoSRmodel.ckpt"
            : _settings.comfyTripoSrModelName.Trim();
        int geometryResolution = Mathf.Clamp(_settings.comfyGeometryResolution, 128, 12288);
        float tripoSrThreshold = Mathf.Max(0f, _settings.comfyTripoSrThreshold);

        string workflow = File.ReadAllText(workflowPath);
        workflow = workflow.Replace("__SEED__", seed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        workflow = workflow.Replace("__STEPS__", steps.ToString(System.Globalization.CultureInfo.InvariantCulture));
        workflow = workflow.Replace("__CFG__", cfg.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        workflow = workflow.Replace("__CHECKPOINT__", EscapeJson(checkpointName));
        workflow = workflow.Replace("__MESH_PROMPT__", EscapeJson(meshPrompt));
        workflow = workflow.Replace("__MESH_NEG_PROMPT__", EscapeJson(meshNegativePrompt));
        workflow = workflow.Replace("__TRIPOSR_MODEL__", EscapeJson(tripoSrModelName));
        workflow = workflow.Replace("__GEOMETRY_RESOLUTION__", geometryResolution.ToString(CultureInfo.InvariantCulture));
        workflow = workflow.Replace("__TRIPOSR_THRESHOLD__", tripoSrThreshold.ToString("0.###", CultureInfo.InvariantCulture));

        return workflow;
    }

    private static string ResolveRunPrompt(NewBackendRequest request, string occupyProxyId)
    {
        string basePrompt = NormalizePrompt(request?.Prompt);
        string assetPrompt = NormalizePrompt(ResolvePerProxyAssetPrompt(request, occupyProxyId));
        return JoinPromptParts(new[] { assetPrompt, basePrompt });
    }

    private static string BuildMeshSourcePrompt(string runPrompt, string occupyProxyId, NewBackendRequest request)
    {
        string globalPrompt = NormalizePrompt(runPrompt);
        string assetPrompt = NormalizePrompt(ResolvePerProxyAssetPrompt(request, occupyProxyId));
        string stylePrompt = NormalizePrompt(BuildMeshStylePrompt(runPrompt, assetPrompt));

        var parts = new List<string>();

        // Keep per-asset semantics first so they are higher priority.
        if (!string.IsNullOrWhiteSpace(assetPrompt))
            parts.Add(assetPrompt);

        // Keep the global prompt as secondary context.
        if (!string.IsNullOrWhiteSpace(globalPrompt))
            parts.Add(globalPrompt);

        // Keep style as supplemental only.
        if (!string.IsNullOrWhiteSpace(stylePrompt))
            parts.Add(stylePrompt);

        // Strong object-isolation constraints for TripoSR-friendly source images.
        parts.Add(
            "single main object, single centered asset, close-up, full object in frame, " +
            "clean silhouette, isolated object render, studio cutout, product render, " +
            "plain background, flat white background, solid white background"
        );

        return JoinPromptParts(parts);
    }

    private static string BuildMeshSourceNegativePrompt(NewBackendRequest request)
    {
        string negativePrompt = NormalizePrompt(request?.NegativePrompt);

        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(negativePrompt))
            parts.Add(negativePrompt);

        parts.Add(
            "floor, ground plane, pedestal, stand, platform, base, slab, " +
            "support, support plane, table, shelf, road, trees, sky, shadow, contact shadow, " +
            "environment, landscape, multiple objects, clutter"
        );

        return JoinPromptParts(parts);
    }

    private static string JoinPromptParts(IEnumerable<string> parts)
    {
        var cleaned = parts
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .SelectMany(p => p.Split(','))
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return string.Join(", ", cleaned);
    }

    private static string NormalizePrompt(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return Regex.Replace(text, "\\s+", " ").Trim().Trim(',');
    }

    private static string BuildMeshStylePrompt(string runPrompt, string assetPrompt)
    {
        string value = NormalizePrompt(runPrompt);
        string asset = NormalizePrompt(assetPrompt);

        if (string.IsNullOrWhiteSpace(value))
            return "high quality 3d asset, studio lighting, clean materials";

        // Remove exact asset prompt only if it is substantial enough to avoid duplication,
        // but keep the rest of the global prompt intact.
        if (!string.IsNullOrWhiteSpace(asset) && asset.Length >= 8)
        {
            value = Regex.Replace(
                value,
                Regex.Escape(asset),
                " ",
                RegexOptions.IgnoreCase);
        }

        // Keep style-bearing words. Only soften composition-heavy scene language.
        value = Regex.Replace(value, "\\bwide shot\\b", "close-up", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, "\\bfull scene\\b", "single object render", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, "\\bestablishing shot\\b", "studio render", RegexOptions.IgnoreCase);

        // Remove only strongly scene-composition phrases that tend to add unwanted context.
        string[] bannedPhrases =
        {
        "in a room",
        "in the room",
        "in an environment",
        "in the environment",
        "surrounded by",
        "next to other objects",
        "with other objects",
        "on a table",
        "on the table",
        "on a shelf",
        "on the shelf",
        "on the floor",
        "in a landscape",
        "in the landscape"
    };

        for (int i = 0; i < bannedPhrases.Length; i++)
        {
            value = Regex.Replace(
                value,
                "\\b" + Regex.Escape(bannedPhrases[i]) + "\\b",
                " ",
                RegexOptions.IgnoreCase);
        }

        value = NormalizePrompt(value);

        if (string.IsNullOrWhiteSpace(value))
            return "high quality 3d asset, studio lighting, clean materials";

        // Add mesh-friendly style guidance without changing the object semantics.
        return JoinPromptParts(new[]
        {
        value,
        "high quality 3d asset",
        "studio lighting",
        "clean materials",
        "sharp silhouette"
    });
    }

    private static string ResolvePerProxyAssetPrompt(NewBackendRequest request, string occupyProxyId)
    {
        if (request?.PerProxyAssetPrompts == null || request.PerProxyAssetPrompts.Count == 0 || string.IsNullOrWhiteSpace(occupyProxyId))
            return string.Empty;

        for (int i = 0; i < request.PerProxyAssetPrompts.Count; i++)
        {
            var entry = request.PerProxyAssetPrompts[i];
            if (entry == null || !string.Equals(entry.ProxyId, occupyProxyId, StringComparison.Ordinal))
                continue;

            return (entry.AssetPrompt ?? string.Empty).Trim();
        }

        return string.Empty;
    }

    private bool CanRunWorkflowWithProvidedInputs(
        string projectRoot,
        out string reason)
    {
        reason = string.Empty;
        string workflowPath = _settings.comfyWorkflowTemplatePath;
        if (!Path.IsPathRooted(workflowPath))
            workflowPath = Path.Combine(projectRoot, workflowPath);
        if (!File.Exists(workflowPath))
        {
            reason = $"workflow file not found: {workflowPath}";
            return false;
        }

        return true;
    }

    private async Task<string> SubmitPromptAsync(string workflowJson)
    {
        string body = $"{{\"prompt\":{workflowJson},\"client_id\":\"{EscapeJson(_settings.comfyClientId)}\"}}";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var cts = new CancellationTokenSource(Math.Max(1000, _settings.remoteTimeoutSeconds * 1000));
        using HttpResponseMessage response = await Http.PostAsync($"{GetComfyBaseUrl().TrimEnd('/')}/prompt", content, cts.Token);

        string responseText = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new Exception($"ComfyUI /prompt failed: {(int)response.StatusCode} {responseText}");

        string promptId = ExtractJsonString(responseText, "prompt_id");
        if (string.IsNullOrWhiteSpace(promptId))
            throw new Exception($"ComfyUI /prompt response missing prompt_id: {responseText}");

        return promptId;
    }

    private async Task TrackExecutionViaWebSocketAsync(string promptId)
    {
        Uri wsUri = BuildWsUri();
        using var socket = new ClientWebSocket();
        using var cts = new CancellationTokenSource(Math.Max(1000, _settings.comfyExecutionTimeoutSeconds * 1000));
        int receiveIdleTimeoutMs = 5000;

        await socket.ConnectAsync(wsUri, cts.Token);

        var buffer = new byte[8192];
        while (socket.State == WebSocketState.Open && !cts.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                receiveCts.CancelAfter(receiveIdleTimeoutMs);
                try
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), receiveCts.Token);
                }
                catch (OperationCanceledException) when (!cts.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"ComfyUI websocket idle timeout after {receiveIdleTimeoutMs}ms for prompt_id={promptId}");
                }
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            string msg = Encoding.UTF8.GetString(ms.ToArray());
            string msgPromptId = ExtractJsonString(msg, "prompt_id");
            if (msgPromptId == promptId)
            {
                string msgType = ExtractJsonString(msg, "type");
                // ComfyUI emits executing with node=null when the prompt has completed.
                if (msgType == "executing" && Regex.IsMatch(msg, "\"node\"\\s*:\\s*null"))
                    return;

                if (msgType == "execution_error")
                    throw new Exception($"ComfyUI execution_error: {msg}");
            }
        }

        throw new Exception($"ComfyUI websocket ended before completion for prompt_id={promptId}");
    }

    private async Task WaitForPromptCompletionAsync(string promptId)
    {
        if (_preferHistoryPolling)
        {
            await WaitForCompletionByHistoryPollingAsync(promptId);
            return;
        }

        try
        {
            await TrackExecutionViaWebSocketAsync(promptId);
            return;
        }
        catch (Exception ex) when (IsRecoverableWebSocketFailure(ex))
        {
            _preferHistoryPolling = true;
        }

        await WaitForCompletionByHistoryPollingAsync(promptId);
    }

    private async Task WaitForCompletionByHistoryPollingAsync(string promptId)
    {
        int timeoutMs = Math.Max(1000, _settings.comfyExecutionTimeoutSeconds * 1000);
        using var cts = new CancellationTokenSource(timeoutMs);
        string historyUrl = $"{GetComfyBaseUrl().TrimEnd('/')}/history/{promptId}";

        while (!cts.IsCancellationRequested)
        {
            try
            {
                using HttpResponseMessage response = await Http.GetAsync(historyUrl, cts.Token);
                string json = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode && ContainsPromptInHistory(json, promptId))
                {
                    int refs = ExtractOutputImageRefs(json).Count;
                    bool hasOutputRefs = refs > 0;
                    bool completed = IsHistoryCompleted(json);
                    if (IsHistoryError(json))
                    {
                        string errorMessage = ExtractJsonString(json, "exception_message");
                        if (string.IsNullOrWhiteSpace(errorMessage))
                            errorMessage = "unknown ComfyUI execution error";
                        throw new Exception($"ComfyUI prompt failed for {promptId}: {errorMessage}");
                    }

                    if (completed || hasOutputRefs)
                        return;
                }
            }
            catch (OperationCanceledException) when (!cts.IsCancellationRequested)
            {
                // Per-request timeout from networking hiccup; continue polling.
            }
            catch
            {
                // Keep polling through transient HTTP failures.
            }

            await Task.Delay(500, cts.Token);
        }

        throw new TimeoutException($"ComfyUI history polling timed out for prompt_id={promptId}");
    }

    private static bool ContainsPromptInHistory(string json, string promptId)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(promptId))
            return false;

        return Regex.IsMatch(json, $"\"{Regex.Escape(promptId)}\"\\s*:");
    }

    private static bool IsHistoryError(string json)
    {
        return Regex.IsMatch(json ?? string.Empty, "\"status_str\"\\s*:\\s*\"error\"");
    }

    private static bool IsHistoryCompleted(string json)
    {
        return Regex.IsMatch(json ?? string.Empty, "\"completed\"\\s*:\\s*true");
    }

    private static bool HasOutputImageRefs(string json)
    {
        return ExtractOutputImageRefs(json ?? string.Empty).Count > 0;
    }

    private static bool IsRecoverableWebSocketFailure(Exception ex)
    {
        if (ex is OperationCanceledException)
            return true;

        if (ex is TimeoutException)
            return true;

        if (ex is WebSocketException)
            return true;

        string msg = ex.Message ?? string.Empty;
        return msg.IndexOf("aborted", StringComparison.OrdinalIgnoreCase) >= 0
            || msg.IndexOf("closed", StringComparison.OrdinalIgnoreCase) >= 0
            || msg.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private GenerationResult ConvertConstraintsToResult(Constraint[] constraints)
    {
        var result = new GenerationResult();
        if (constraints == null)
            return result;

        foreach (Constraint c in constraints)
        {
            PrimitiveType primitive = (c?.shape ?? string.Empty).ToLowerInvariant() switch
            {
                "sphere" => PrimitiveType.Sphere,
                "cylinder" => PrimitiveType.Cylinder,
                _ => PrimitiveType.Cube
            };

            result.objects.Add(new GeneratedObject
            {
                primitiveType = primitive,
                position = c.position,
                size = c.size
            });
        }

        return result;
    }

    private async Task<List<string>> DownloadOutputsAsync(string promptId, string requestId, string outputDir, string outputPrefix = null)
    {
        Directory.CreateDirectory(outputDir);
        using var cts = new CancellationTokenSource(Math.Max(1000, _settings.remoteTimeoutSeconds * 1000));
        using HttpResponseMessage response = await Http.GetAsync($"{GetComfyBaseUrl().TrimEnd('/')}/history/{promptId}", cts.Token);
        string historyJson = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new Exception($"ComfyUI /history/{promptId} failed: {(int)response.StatusCode} {historyJson}");

        var outputs = ExtractOutputImageRefs(historyJson);
        var saved = new List<string>();

        foreach (var output in outputs)
        {
            string url =
                $"{GetComfyBaseUrl().TrimEnd('/')}/view?filename={Uri.EscapeDataString(output.filename)}" +
                $"&subfolder={Uri.EscapeDataString(output.subfolder)}&type={Uri.EscapeDataString(output.type)}";
            byte[] bytes = await Http.GetByteArrayAsync(url);

            string prefix = string.IsNullOrWhiteSpace(outputPrefix) ? requestId : outputPrefix;
            string finalName = $"{prefix}_{Path.GetFileName(output.filename)}";
            string outPath = Path.Combine(outputDir, finalName);
            File.WriteAllBytes(outPath, bytes);
            saved.Add(outPath);
        }

        return saved;
    }

    private static string BuildMaskGuidedMeshSourceImage(string meshSourcePath, string maskPath, string outputDir, string outputPrefix)
    {
        if (string.IsNullOrWhiteSpace(meshSourcePath) || string.IsNullOrWhiteSpace(maskPath) || !File.Exists(meshSourcePath) || !File.Exists(maskPath))
            return string.Empty;

        Texture2D source = null;
        Texture2D mask = null;
        Texture2D output = null;
        try
        {
            source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            mask = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            byte[] sourceBytes = File.ReadAllBytes(meshSourcePath);
            byte[] maskBytes = File.ReadAllBytes(maskPath);
            if (!source.LoadImage(sourceBytes) || !mask.LoadImage(maskBytes))
                return string.Empty;
            if (source.width != mask.width || source.height != mask.height)
                return string.Empty;

            int width = source.width;
            int height = source.height;
            Color[] sourcePixels = source.GetPixels();
            Color[] maskPixels = mask.GetPixels();
            Color[] outputPixels = new Color[sourcePixels.Length];

            RectInt maskRect = ComputeMaskBounds(maskPixels, width, height);
            if (maskRect.width <= 0 || maskRect.height <= 0)
                return string.Empty;

            RectInt expandedRect = ExpandRect(maskRect, width, height, 2.5f, 24);
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int idx = row + x;
                    if (!expandedRect.Contains(new Vector2Int(x, y)))
                    {
                        outputPixels[idx] = new Color(0f, 0f, 0f, 0f);
                        continue;
                    }

                    float alpha = maskPixels[idx].maxColorComponent;
                    if (alpha <= 0.05f)
                    {
                        outputPixels[idx] = new Color(0f, 0f, 0f, 0f);
                        continue;
                    }

                    Color color = sourcePixels[idx];
                    color.a = 1f;
                    outputPixels[idx] = color;
                }
            }

            output = new Texture2D(width, height, TextureFormat.RGBA32, false);
            output.SetPixels(outputPixels);
            output.Apply(false);
            Directory.CreateDirectory(outputDir);
            string outPath = Path.Combine(outputDir, $"{outputPrefix}_mesh_source_mask_guided.png");
            File.WriteAllBytes(outPath, output.EncodeToPNG());

            return outPath;
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            if (source != null)
                UnityEngine.Object.DestroyImmediate(source);
            if (mask != null)
                UnityEngine.Object.DestroyImmediate(mask);
            if (output != null)
                UnityEngine.Object.DestroyImmediate(output);
        }
    }

    private static RectInt ComputeMaskBounds(Color[] maskPixels, int width, int height)
    {
        int minX = width;
        int minY = height;
        int maxX = -1;
        int maxY = -1;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (maskPixels[row + x].maxColorComponent <= 0.05f)
                    continue;

                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < minX || maxY < minY)
            return new RectInt(0, 0, 0, 0);

        return new RectInt(minX, minY, (maxX - minX) + 1, (maxY - minY) + 1);
    }

    private static RectInt ExpandRect(RectInt rect, int width, int height, float scale, int minPadding)
    {
        int centerX = rect.x + (rect.width / 2);
        int centerY = rect.y + (rect.height / 2);
        int expandedWidth = Mathf.Clamp(Mathf.CeilToInt(rect.width * scale), rect.width + minPadding, width);
        int expandedHeight = Mathf.Clamp(Mathf.CeilToInt(rect.height * scale), rect.height + minPadding, height);
        int x = Mathf.Clamp(centerX - (expandedWidth / 2), 0, Mathf.Max(0, width - expandedWidth));
        int y = Mathf.Clamp(centerY - (expandedHeight / 2), 0, Mathf.Max(0, height - expandedHeight));
        return new RectInt(x, y, expandedWidth, expandedHeight);
    }

    private static string FindFirstOutputContaining(List<string> paths, string token)
    {
        if (paths == null || paths.Count == 0 || string.IsNullOrWhiteSpace(token))
            return string.Empty;

        for (int i = 0; i < paths.Count; i++)
        {
            string path = paths[i];
            if (!string.IsNullOrWhiteSpace(path) &&
                path.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0 &&
                File.Exists(path))
            {
                return path;
            }
        }

        return string.Empty;
    }

    private static List<ImageRef> ExtractOutputImageRefs(string historyJson)
    {
        var refs = new List<ImageRef>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(historyJson))
            return refs;

        // Support both field orders observed in ComfyUI history payloads.
        var patterns = new[]
        {
            "\"filename\"\\s*:\\s*\"([^\"]+)\"\\s*,\\s*\"subfolder\"\\s*:\\s*\"([^\"]*)\"\\s*,\\s*\"type\"\\s*:\\s*\"([^\"]+)\"",
            "\"filename\"\\s*:\\s*\"([^\"]+)\"\\s*,\\s*\"type\"\\s*:\\s*\"([^\"]+)\"\\s*,\\s*\"subfolder\"\\s*:\\s*\"([^\"]*)\""
        };

        foreach (string pattern in patterns)
        {
            MatchCollection matches = Regex.Matches(historyJson, pattern);
            foreach (Match match in matches)
            {
                if (!match.Success || match.Groups.Count < 4)
                    continue;

                string filename = match.Groups[1].Value;
                string second = match.Groups[2].Value;
                string third = match.Groups[3].Value;
                string subfolder;
                string type;

                // Pattern 1: [filename, subfolder, type], Pattern 2: [filename, type, subfolder]
                if (pattern.Contains("\"subfolder\"\\s*:\\s*\"([^\"]*)\"\\s*,\\s*\"type\""))
                {
                    subfolder = second;
                    type = third;
                }
                else
                {
                    type = second;
                    subfolder = third;
                }

                if (!type.Equals("output", StringComparison.OrdinalIgnoreCase))
                    continue;

                string key = $"{filename}|{subfolder}|{type}";
                if (!seen.Add(key))
                    continue;

                refs.Add(new ImageRef
                {
                    filename = filename,
                    subfolder = subfolder,
                    type = type
                });
            }
        }

        return refs;
    }
    private static bool IsMeshFile(string path)
    {
        string ext = Path.GetExtension(path ?? string.Empty);
        return ext.Equals(".glb", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gltf", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".obj", StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveOutputDirectory(string projectRoot)
    {
        string outputPath = _settings.comfyOutputAssetFolder;
        if (!Path.IsPathRooted(outputPath))
            outputPath = Path.Combine(projectRoot, outputPath);
        return outputPath;
    }

    private string GetComfyBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(_settings.comfyBaseUrl))
            return _settings.comfyBaseUrl;

        if (!string.IsNullOrWhiteSpace(_settings.remoteUrl))
            return _settings.remoteUrl.Replace("/generate", string.Empty);

        return "http://127.0.0.1:8000";
    }

    private Uri BuildWsUri()
    {
        if (!string.IsNullOrWhiteSpace(_settings.comfyWsUrl))
            return new Uri(AppendClientId(_settings.comfyWsUrl));

        Uri baseUri = new Uri(GetComfyBaseUrl());
        string scheme = baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        string ws = $"{scheme}://{baseUri.Host}:{baseUri.Port}/ws";
        ws = AppendClientId(ws);
        return new Uri(ws);
    }

    private string AppendClientId(string wsUrl)
    {
        string sep = wsUrl.Contains("?") ? "&" : "?";
        return $"{wsUrl}{sep}clientId={Uri.EscapeDataString(_settings.comfyClientId)}";
    }

    private static string EscapeJson(string value)
    {
        return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string ExtractJsonString(string json, string fieldName)
    {
        Match m = Regex.Match(json, $"\"{Regex.Escape(fieldName)}\"\\s*:\\s*\"([^\"]+)\"");
        return m.Success && m.Groups.Count > 1 ? m.Groups[1].Value : string.Empty;
    }

    private static void RefreshAssetDatabaseIfNeeded()
    {
#if UNITY_EDITOR
        AssetDatabase.Refresh();
#endif
    }

    private class ImageRef
    {
        public string filename;
        public string subfolder;
        public string type;
    }

}
