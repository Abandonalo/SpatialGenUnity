using System;
using System.Collections.Generic;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class RemoteGenerationBackend : IGenerationBackend
{
    private readonly BackendSettings _settings;
    private static Process _comfyProcess;
    private static readonly HttpClient Http = new HttpClient();
    private static bool _preferHistoryPolling;
    private static bool _loggedPollingMode;

    public string Name => "ComfyUI";

    public RemoteGenerationBackend(BackendSettings settings)
    {
        _settings = settings;
    }

    public async Task<GenerationResult> GenerateAsync(BackendRequest request)
    {
        var totalSw = Stopwatch.StartNew();
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        string requestId = string.IsNullOrWhiteSpace(request.request_id)
            ? Guid.NewGuid().ToString("N")
            : request.request_id;

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string runInputDir = Path.Combine(projectRoot, _settings.comfyInputFolder, requestId);
        Directory.CreateDirectory(runInputDir);
        File.WriteAllText(Path.Combine(runInputDir, "request.json"), JsonUtility.ToJson(request, true));

        string depthStagedPath = StageInputFile(request.depthImagePath, runInputDir, "depth");
        string cannyStagedPath = StageInputFile(request.cannyImagePath, runInputDir, "canny");
        List<string> maskStagedPaths = StageMaskFiles(request.maskImagePaths, runInputDir);
        string maskOccupyStagedPath = StageInputFile(request.maskOccupyImagePath, runInputDir, "mask_occupy");
        string maskAvoidStagedPath = StageInputFile(request.maskAvoidImagePath, runInputDir, "mask_avoid");
        string maskFocusStagedPath = StageInputFile(request.maskFocusImagePath, runInputDir, "mask_focus");

        await EnsureComfyRunningAsync();

        string depthName = await UploadImageIfPresentAsync(depthStagedPath, "/upload/image");
        string cannyName = await UploadImageIfPresentAsync(cannyStagedPath, "/upload/image");
        List<string> maskNames = await UploadMaskImagesAsync(maskStagedPaths);
        string maskOccupyName = await UploadMaskIfPresentAsync(maskOccupyStagedPath);
        string maskAvoidName = await UploadMaskIfPresentAsync(maskAvoidStagedPath);
        string maskFocusName = await UploadMaskIfPresentAsync(maskFocusStagedPath);

        // Backward compatibility: if named mask paths are not set, fall back to generic mask array order.
        if (string.IsNullOrWhiteSpace(maskOccupyName) && maskNames.Count > 0) maskOccupyName = maskNames[0];
        if (string.IsNullOrWhiteSpace(maskAvoidName) && maskNames.Count > 1) maskAvoidName = maskNames[1];
        if (string.IsNullOrWhiteSpace(maskFocusName) && maskNames.Count > 2) maskFocusName = maskNames[2];

        // Temporary adapter fallback: legacy generation path may not provide depth/canny images yet.
        if (string.IsNullOrWhiteSpace(depthName))
        {
            depthName = FirstNonEmpty(maskFocusName, maskOccupyName, maskAvoidName);
            if (!string.IsNullOrWhiteSpace(depthName))
                Debug.LogWarning($"ComfyUI depth image missing for request {requestId}; falling back to '{depthName}'.");
        }

        if (string.IsNullOrWhiteSpace(cannyName))
        {
            cannyName = FirstNonEmpty(maskAvoidName, maskOccupyName, maskFocusName, depthName);
            if (!string.IsNullOrWhiteSpace(cannyName))
                Debug.LogWarning($"ComfyUI canny image missing for request {requestId}; falling back to '{cannyName}'.");
        }

        // If depth/canny are still missing, synthesize neutral fallback images so template placeholders are satisfied.
        if (string.IsNullOrWhiteSpace(depthName))
        {
            string fallbackDepthPath = CreateFallbackImage(runInputDir, "depth_fallback.png", 512, 512, new Color(0.5f, 0.5f, 0.5f, 1f));
            depthName = await UploadImageIfPresentAsync(fallbackDepthPath, "/upload/image");
            Debug.LogWarning($"ComfyUI depth image missing for request {requestId}; uploaded generated fallback '{depthName}'.");
        }

        if (string.IsNullOrWhiteSpace(cannyName))
        {
            string fallbackCannyPath = CreateFallbackImage(runInputDir, "canny_fallback.png", 512, 512, Color.black);
            cannyName = await UploadImageIfPresentAsync(fallbackCannyPath, "/upload/image");
            Debug.LogWarning($"ComfyUI canny image missing for request {requestId}; uploaded generated fallback '{cannyName}'.");
        }

        // Ensure named masks exist when workflow expects them.
        if (string.IsNullOrWhiteSpace(maskOccupyName))
        {
            string fallbackMaskOccupyPath = CreateFallbackImage(runInputDir, "mask_occupy_fallback.png", 512, 512, Color.white);
            maskOccupyName = await UploadMaskIfPresentAsync(fallbackMaskOccupyPath);
            Debug.LogWarning($"ComfyUI occupy mask missing for request {requestId}; uploaded generated fallback '{maskOccupyName}'.");
        }

        if (string.IsNullOrWhiteSpace(maskAvoidName))
        {
            string fallbackMaskAvoidPath = CreateFallbackImage(runInputDir, "mask_avoid_fallback.png", 512, 512, Color.black);
            maskAvoidName = await UploadMaskIfPresentAsync(fallbackMaskAvoidPath);
            Debug.LogWarning($"ComfyUI avoid mask missing for request {requestId}; uploaded generated fallback '{maskAvoidName}'.");
        }

        if (string.IsNullOrWhiteSpace(maskFocusName))
        {
            string fallbackMaskFocusPath = CreateFallbackImage(runInputDir, "mask_focus_fallback.png", 512, 512, new Color(0.5f, 0.5f, 0.5f, 1f));
            maskFocusName = await UploadMaskIfPresentAsync(fallbackMaskFocusPath);
            Debug.LogWarning($"ComfyUI focus mask missing for request {requestId}; uploaded generated fallback '{maskFocusName}'.");
        }

        if (!CanRunWorkflowWithProvidedInputs(projectRoot, depthName, cannyName, maskNames, maskOccupyName, maskAvoidName, maskFocusName, out string missingReason))
        {
            Debug.LogWarning(
                $"Skipping ComfyUI execution for request {requestId}: {missingReason}. " +
                "Using proxy constraints to generate scene objects.");
            return ConvertConstraintsToResult(request.constraints);
        }

        string workflowJson = LoadAndBindWorkflow(projectRoot, depthName, cannyName, maskNames, maskOccupyName, maskAvoidName, maskFocusName, request);
        var submitSw = Stopwatch.StartNew();
        string promptId = await SubmitPromptAsync(workflowJson);
        submitSw.Stop();

        var executionSw = Stopwatch.StartNew();
        await WaitForPromptCompletionAsync(promptId);
        executionSw.Stop();

        string outputDir = ResolveOutputDirectory(projectRoot);
        var downloadSw = Stopwatch.StartNew();
        List<string> savedOutputs = await DownloadOutputsAsync(promptId, requestId, outputDir);
        downloadSw.Stop();

        var refreshSw = Stopwatch.StartNew();
        RefreshAssetDatabaseIfNeeded();
        refreshSw.Stop();
        totalSw.Stop();

        if (savedOutputs.Count == 0)
            Debug.LogWarning($"ComfyUI returned no downloadable outputs for prompt_id={promptId}");
        else
            Debug.Log($"ComfyUI saved {savedOutputs.Count} file(s) to {outputDir}");

        Debug.Log(
            $"ComfyUI timings request_id={requestId} prompt_id={promptId} " +
            $"submit_ms={submitSw.ElapsedMilliseconds} " +
            $"execution_ms={executionSw.ElapsedMilliseconds} " +
            $"download_ms={downloadSw.ElapsedMilliseconds} " +
            $"asset_refresh_ms={refreshSw.ElapsedMilliseconds} " +
            $"total_ms={totalSw.ElapsedMilliseconds}");

        return ConvertConstraintsToResult(request.constraints);
    }

    private string StageInputFile(string sourcePath, string runInputDir, string label)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return string.Empty;

        string ext = Path.GetExtension(sourcePath);
        string fileName = $"{label}{ext}";
        string destPath = Path.Combine(runInputDir, fileName);
        File.Copy(sourcePath, destPath, overwrite: true);
        return destPath;
    }

    private List<string> StageMaskFiles(string[] maskPaths, string runInputDir)
    {
        var stagedPaths = new List<string>();
        if (maskPaths == null) return stagedPaths;

        for (int i = 0; i < maskPaths.Length; i++)
        {
            string sourcePath = maskPaths[i];
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                continue;

            string ext = Path.GetExtension(sourcePath);
            string fileName = $"mask_{i}{ext}";
            string destPath = Path.Combine(runInputDir, fileName);
            File.Copy(sourcePath, destPath, overwrite: true);
            stagedPaths.Add(destPath);
        }

        return stagedPaths;
    }

    private async Task<string> UploadImageIfPresentAsync(string localPath, string uploadRoute)
    {
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
            return string.Empty;

        using var form = new MultipartFormDataContent();
        byte[] fileBytes = File.ReadAllBytes(localPath);
        var fileContent = new ByteArrayContent(fileBytes);
        form.Add(fileContent, "image", Path.GetFileName(localPath));
        form.Add(new StringContent("input"), "type");
        form.Add(new StringContent("true"), "overwrite");

        using var cts = new CancellationTokenSource(Math.Max(1000, _settings.remoteTimeoutSeconds * 1000));
        string uploadUrl = $"{GetComfyBaseUrl().TrimEnd('/')}/{uploadRoute.TrimStart('/')}";
        using HttpResponseMessage response = await Http.PostAsync(uploadUrl, form, cts.Token);
        string text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new Exception($"ComfyUI {uploadRoute} failed: {(int)response.StatusCode} {text}");

        // The upload API returns a JSON object with "name" on success.
        string uploadedName = ExtractJsonString(text, "name");
        if (string.IsNullOrWhiteSpace(uploadedName))
            return Path.GetFileName(localPath);
        return uploadedName;
    }

    private async Task<List<string>> UploadMaskImagesAsync(List<string> stagedMaskPaths)
    {
        var names = new List<string>();
        if (stagedMaskPaths == null)
            return names;

        for (int i = 0; i < stagedMaskPaths.Count; i++)
        {
            string uploadedName = await UploadMaskIfPresentAsync(stagedMaskPaths[i]);
            if (!string.IsNullOrWhiteSpace(uploadedName))
                names.Add(uploadedName);
        }

        return names;
    }

    private async Task<string> UploadMaskIfPresentAsync(string localPath)
    {
        try
        {
            return await UploadImageIfPresentAsync(localPath, "/upload/mask");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"ComfyUI /upload/mask failed ({ex.Message}). Retrying via /upload/image.");
            return await UploadImageIfPresentAsync(localPath, "/upload/image");
        }
    }

    private async Task EnsureComfyRunningAsync()
    {
        if (await IsComfyHealthyAsync())
            return;

        if (!_settings.comfyAutoStart)
            throw new Exception("ComfyUI is not reachable and comfyAutoStart is disabled.");

        bool desktopAlreadyRunning = IsComfyDesktopProcessRunning();
        if (!desktopAlreadyRunning)
        {
            StartComfyProcess();
        }
        else
        {
            Debug.Log("ComfyUI desktop process already running; waiting for API to become ready.");
        }

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
        string depthName,
        string cannyName,
        List<string> maskNames,
        string maskOccupyName,
        string maskAvoidName,
        string maskFocusName,
        BackendRequest request)
    {
        string workflowPath = _settings.comfyWorkflowTemplatePath;
        if (!Path.IsPathRooted(workflowPath))
            workflowPath = Path.Combine(projectRoot, workflowPath);

        if (!File.Exists(workflowPath))
            throw new Exception($"ComfyUI workflow file not found: {workflowPath}");

        string workflow = File.ReadAllText(workflowPath);
        workflow = workflow.Replace("__DEPTH_IMAGE__", EscapeJson(depthName));
        workflow = workflow.Replace("__CANNY_IMAGE__", EscapeJson(cannyName));
        workflow = workflow.Replace("__MASK_IMAGE_COUNT__", maskNames.Count.ToString());
        workflow = workflow.Replace("__MASK_OCCUPY_IMAGE__", EscapeJson(maskOccupyName));
        workflow = workflow.Replace("__MASK_AVOID_IMAGE__", EscapeJson(maskAvoidName));
        workflow = workflow.Replace("__MASK_FOCUS_IMAGE__", EscapeJson(maskFocusName));
        workflow = workflow.Replace("__MASK_OCCUPY_WEIGHT__", Mathf.Clamp01(request?.maskOccupyWeight ?? 1f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        workflow = workflow.Replace("__MASK_AVOID_WEIGHT__", Mathf.Clamp01(request?.maskAvoidWeight ?? 1f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        workflow = workflow.Replace("__MASK_FOCUS_WEIGHT__", Mathf.Clamp01(request?.maskFocusWeight ?? 1f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

        for (int i = 0; i < maskNames.Count; i++)
            workflow = workflow.Replace($"__MASK_IMAGE_{i}__", EscapeJson(maskNames[i]));

        return workflow;
    }

    private bool CanRunWorkflowWithProvidedInputs(
        string projectRoot,
        string depthName,
        string cannyName,
        List<string> maskNames,
        string maskOccupyName,
        string maskAvoidName,
        string maskFocusName,
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

        string template = File.ReadAllText(workflowPath);

        if (template.Contains("__DEPTH_IMAGE__") && string.IsNullOrWhiteSpace(depthName))
        {
            reason = "workflow requires __DEPTH_IMAGE__ but request.depthImagePath is empty";
            return false;
        }

        if (template.Contains("__CANNY_IMAGE__") && string.IsNullOrWhiteSpace(cannyName))
        {
            reason = "workflow requires __CANNY_IMAGE__ but request.cannyImagePath is empty";
            return false;
        }

        MatchCollection maskPlaceholders = Regex.Matches(template, "__MASK_IMAGE_\\d+__");
        if (maskPlaceholders.Count > 0 && (maskNames == null || maskNames.Count == 0))
        {
            reason = "workflow requires __MASK_IMAGE_n__ placeholders but request.maskImagePaths is empty";
            return false;
        }

        if (template.Contains("__MASK_OCCUPY_IMAGE__") && string.IsNullOrWhiteSpace(maskOccupyName))
        {
            reason = "workflow requires __MASK_OCCUPY_IMAGE__ but no occupy mask was provided";
            return false;
        }

        if (template.Contains("__MASK_AVOID_IMAGE__") && string.IsNullOrWhiteSpace(maskAvoidName))
        {
            reason = "workflow requires __MASK_AVOID_IMAGE__ but no avoid mask was provided";
            return false;
        }

        if (template.Contains("__MASK_FOCUS_IMAGE__") && string.IsNullOrWhiteSpace(maskFocusName))
        {
            reason = "workflow requires __MASK_FOCUS_IMAGE__ but no focus mask was provided";
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

    private static string FirstNonEmpty(params string[] values)
    {
        if (values == null) return string.Empty;
        for (int i = 0; i < values.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(values[i]))
                return values[i];
        }
        return string.Empty;
    }

    private static string CreateFallbackImage(string outputDir, string fileName, int width, int height, Color color)
    {
        Texture2D texture = null;
        try
        {
            texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            Color[] pixels = new Color[width * height];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
            texture.SetPixels(pixels);
            texture.Apply(false);

            byte[] png = texture.EncodeToPNG();
            string path = Path.Combine(outputDir, fileName);
            File.WriteAllBytes(path, png);
            return path;
        }
        finally
        {
            if (texture != null)
                UnityEngine.Object.DestroyImmediate(texture);
        }
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
                if (msgType == "progress")
                    Debug.Log($"ComfyUI progress: {msg}");

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
            if (!_loggedPollingMode)
            {
                Debug.Log("ComfyUI tracking mode: history polling (websocket disabled after previous transport failures).");
                _loggedPollingMode = true;
            }
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
            Debug.LogWarning(
                $"ComfyUI websocket tracking failed ({ex.Message}). Falling back to history polling for prompt_id={promptId}.");
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
                    if (IsHistoryError(json))
                    {
                        string errorMessage = ExtractJsonString(json, "exception_message");
                        if (string.IsNullOrWhiteSpace(errorMessage))
                            errorMessage = "unknown ComfyUI execution error";
                        throw new Exception($"ComfyUI prompt failed for {promptId}: {errorMessage}");
                    }

                    if (IsHistoryCompleted(json) || HasOutputImageRefs(json))
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

    private async Task<List<string>> DownloadOutputsAsync(string promptId, string requestId, string outputDir)
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

            string finalName = $"{requestId}_{Path.GetFileName(output.filename)}";
            string outPath = Path.Combine(outputDir, finalName);
            File.WriteAllBytes(outPath, bytes);
            saved.Add(outPath);
        }

        return saved;
    }

    private static List<ImageRef> ExtractOutputImageRefs(string historyJson)
    {
        var matches = Regex.Matches(
            historyJson,
            "\"filename\"\\s*:\\s*\"([^\"]+)\"\\s*,\\s*\"subfolder\"\\s*:\\s*\"([^\"]*)\"\\s*,\\s*\"type\"\\s*:\\s*\"([^\"]+)\"");

        var refs = new List<ImageRef>();
        foreach (Match match in matches)
        {
            if (!match.Success || match.Groups.Count < 4)
                continue;

            refs.Add(new ImageRef
            {
                filename = match.Groups[1].Value,
                subfolder = match.Groups[2].Value,
                type = match.Groups[3].Value
            });
        }

        return refs;
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
