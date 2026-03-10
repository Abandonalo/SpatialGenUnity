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
    private const string DebugLogPath = "/Users/alo/SpatialGenUnity/.cursor/debug-f611c7.log";
    private const string DebugSessionId = "f611c7";
    private const string AgentDebugLogPath = "/Users/alo/SpatialGenUnity/.cursor/debug-e9b45f.log";
    private const string AgentDebugSessionId = "e9b45f";
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

    public async Task<GenerationResult> GenerateAsync(NewBackendRequest request)
    {
        var totalSw = Stopwatch.StartNew();
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        string requestId = string.IsNullOrWhiteSpace(request.RequestId)
            ? Guid.NewGuid().ToString("N")
            : request.RequestId;

        int legacyConstraintCount = request?.LegacyConstraints?.Length ?? 0;
        int legacyOccupyCount = 0;
        int legacyAttractCount = 0;
        if (request?.LegacyConstraints != null)
        {
            for (int i = 0; i < request.LegacyConstraints.Length; i++)
            {
                Constraint c = request.LegacyConstraints[i];
                string type = (c?.type ?? string.Empty).Trim().ToLowerInvariant();
                if (type == "occupy")
                    legacyOccupyCount++;
                else if (type == "attract")
                    legacyAttractCount++;
            }
        }
        // #region agent log
        AppendAgentDebugLog(
            "pre-fix",
            "H1,H2",
            "RemoteGenerationBackend.cs:64",
            "incoming_prompt_request",
            $"{{\"requestId\":\"{EscapeJson(requestId)}\",\"basePrompt\":\"{EscapeJson(SummarizeTextForDebug(request?.Prompt))}\",\"negativePrompt\":\"{EscapeJson(SummarizeTextForDebug(request?.NegativePrompt))}\",\"perProxyPromptCount\":{(request?.PerProxyAssetPrompts?.Count ?? 0).ToString(CultureInfo.InvariantCulture)},\"perProxyPrompts\":\"{EscapeJson(SummarizePerProxyPromptsForDebug(request))}\",\"legacyOccupyProxyIds\":\"{EscapeJson(SummarizeLegacyOccupyProxyIdsForDebug(request))}\"}}");
        // #endregion
        // #region agent log
        AppendDebugLog(
            "baseline",
            "H4",
            "RemoteGenerationBackend.GenerateAsync:request_constraints",
            "Captured incoming legacy constraints composition",
            $"{{\"requestId\":\"{EscapeJson(requestId)}\",\"legacyConstraintCount\":{legacyConstraintCount},\"legacyOccupyCount\":{legacyOccupyCount},\"legacyAttractCount\":{legacyAttractCount}}}");
        // #endregion

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string runInputDir = Path.Combine(projectRoot, _settings.comfyInputFolder, requestId);
        Directory.CreateDirectory(runInputDir);
        File.WriteAllText(Path.Combine(runInputDir, "request.json"), JsonUtility.ToJson(request, true));

        string depthStagedPath = WriteBase64PngIfPresent(request?.Payload?.DepthBase64, runInputDir, "depth");
        string cannyStagedPath = WriteBase64PngIfPresent(request?.Payload?.EdgesBase64, runInputDir, "canny");
        string maskOccupyStagedPath = WriteBase64PngIfPresent(request?.Payload?.MaskOccupyBase64, runInputDir, "mask_occupy");
        string maskAvoidStagedPath = WriteBase64PngIfPresent(request?.Payload?.MaskAvoidBase64, runInputDir, "mask_avoid");
        string maskFocusStagedPath = WriteBase64PngIfPresent(request?.Payload?.MaskFocusBase64, runInputDir, "mask_focus");
        List<string> maskStagedPaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(maskOccupyStagedPath)) maskStagedPaths.Add(maskOccupyStagedPath);
        if (!string.IsNullOrWhiteSpace(maskAvoidStagedPath)) maskStagedPaths.Add(maskAvoidStagedPath);
        if (!string.IsNullOrWhiteSpace(maskFocusStagedPath)) maskStagedPaths.Add(maskFocusStagedPath);

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
            // #region agent log
            AppendDebugLog(
                "baseline",
                "H2",
                "RemoteGenerationBackend.GenerateAsync:workflow_skipped",
                "Workflow was skipped and fallback conversion is used",
                $"{{\"requestId\":\"{EscapeJson(requestId)}\",\"reason\":\"{EscapeJson(missingReason)}\"}}");
            // #endregion
            Debug.LogWarning(
                $"Skipping ComfyUI execution for request {requestId}: {missingReason}. " +
                "Using proxy constraints to generate scene objects.");
            return ConvertConstraintsToResult(request.LegacyConstraints);
        }

        List<Constraint> occupyConstraints = ExtractConstraintsByType(request?.LegacyConstraints, "occupy");
        int runCount = Mathf.Max(1, occupyConstraints.Count);
        string outputDir = ResolveOutputDirectory(projectRoot);
        var savedOutputs = new List<string>();
        string previousPreviewPath = null;
        string previousPromptOnlyPath = null;
        string previousMeshSourcePath = null;
        string previousMaskGuidedMeshSourcePath = null;
        long submitMsTotal = 0;
        long executionMsTotal = 0;
        long downloadMsTotal = 0;

        // #region agent log
        AppendDebugLog(
            "baseline",
            "H7",
            "RemoteGenerationBackend.GenerateAsync:per_proxy_plan",
            "Prepared per-occupy execution plan",
            $"{{\"requestId\":\"{EscapeJson(requestId)}\",\"occupyConstraintCount\":{occupyConstraints.Count},\"runCount\":{runCount}}}");
        // #endregion

        for (int runIndex = 0; runIndex < runCount; runIndex++)
        {
            Constraint occupy = runIndex < occupyConstraints.Count ? occupyConstraints[runIndex] : null;
            string occupyProxyId = !string.IsNullOrWhiteSpace(occupy?.proxy_id)
                ? occupy.proxy_id
                : $"occupy_{runIndex}";
            string runPrompt = ResolveRunPrompt(request, occupyProxyId);
            string meshSourcePrompt = BuildMeshSourcePrompt(runPrompt, occupyProxyId, request);
            string meshSourceNegativePrompt = BuildMeshSourceNegativePrompt(request);
            string runMaskOccupyName = maskOccupyName;
            string perProxyMaskPath = string.Empty;

            if (occupy != null)
            {
                perProxyMaskPath = BuildPerProxyOccupyMaskPath(occupy, request, runInputDir, runIndex);
                if (!string.IsNullOrWhiteSpace(perProxyMaskPath))
                {
                    string uploadedPerProxyMaskName = await UploadMaskIfPresentAsync(perProxyMaskPath);
                    if (!string.IsNullOrWhiteSpace(uploadedPerProxyMaskName))
                        runMaskOccupyName = uploadedPerProxyMaskName;
                }
            }
            // #region agent log
            AppendAgentDebugLog(
                "pre-fix",
                "H1,H2,H4",
                "RemoteGenerationBackend.cs:206",
                "per_proxy_run_prompt_resolution",
                $"{{\"requestId\":\"{EscapeJson(requestId)}\",\"runIndex\":{runIndex.ToString(CultureInfo.InvariantCulture)},\"occupyProxyId\":\"{EscapeJson(occupyProxyId)}\",\"assetPrompt\":\"{EscapeJson(SummarizeTextForDebug(ResolvePerProxyAssetPrompt(request, occupyProxyId)))}\",\"runPrompt\":\"{EscapeJson(SummarizeTextForDebug(runPrompt))}\",\"meshSourcePrompt\":\"{EscapeJson(SummarizeTextForDebug(meshSourcePrompt))}\",\"meshSourceNegativePrompt\":\"{EscapeJson(SummarizeTextForDebug(meshSourceNegativePrompt))}\",\"runPromptContainsAssetPrompt\":{(string.IsNullOrWhiteSpace(ResolvePerProxyAssetPrompt(request, occupyProxyId)) ? "false" : (runPrompt ?? string.Empty).Contains(ResolvePerProxyAssetPrompt(request, occupyProxyId), StringComparison.Ordinal) ? "true" : "false")},\"perProxyMaskPath\":\"{EscapeJson(perProxyMaskPath)}\",\"perProxyMaskCoverage\":{CalculateMaskCoverage(perProxyMaskPath).ToString("0.######", CultureInfo.InvariantCulture)}}}");
            // #endregion

            // #region agent log
            AppendDebugLog(
                "baseline",
                "H7",
                "RemoteGenerationBackend.GenerateAsync:before_submit_run",
                "Prepared workflow-bound names for per-proxy run",
                $"{{\"requestId\":\"{EscapeJson(requestId)}\",\"runIndex\":{runIndex},\"occupyProxyId\":\"{EscapeJson(occupyProxyId)}\",\"depthName\":\"{EscapeJson(depthName)}\",\"cannyName\":\"{EscapeJson(cannyName)}\",\"maskOccupyName\":\"{EscapeJson(runMaskOccupyName)}\",\"maskAvoidName\":\"{EscapeJson(maskAvoidName)}\",\"maskFocusName\":\"{EscapeJson(maskFocusName)}\"}}");
            // #endregion

            string workflowJson = LoadAndBindWorkflow(projectRoot, depthName, cannyName, maskNames, runMaskOccupyName, maskAvoidName, maskFocusName, request, runPrompt, meshSourcePrompt, meshSourceNegativePrompt);
            var submitSw = Stopwatch.StartNew();
            string promptId = await SubmitPromptAsync(workflowJson);
            submitSw.Stop();
            submitMsTotal += submitSw.ElapsedMilliseconds;

            var executionSw = Stopwatch.StartNew();
            await WaitForPromptCompletionAsync(promptId);
            executionSw.Stop();
            executionMsTotal += executionSw.ElapsedMilliseconds;

            string runOutputPrefix = $"{requestId}_{SanitizeFileToken(occupyProxyId)}";
            var downloadSw = Stopwatch.StartNew();
            List<string> runSavedOutputs = await DownloadOutputsAsync(promptId, requestId, outputDir, runOutputPrefix);
            downloadSw.Stop();
            downloadMsTotal += downloadSw.ElapsedMilliseconds;
            string runPreviewPath = FindFirstPreviewOutput(runSavedOutputs);
            string runPromptOnlyPath = FindFirstOutputContaining(runSavedOutputs, "spatialgen_prompt_only");
            string runMeshSourcePath = FindFirstOutputContaining(runSavedOutputs, "spatialgen_mesh_source");
            string runMaskGuidedMeshSourcePath = BuildMaskGuidedMeshSourceImage(runMeshSourcePath, perProxyMaskPath, outputDir, runOutputPrefix);
            if (!string.IsNullOrWhiteSpace(previousPreviewPath) && !string.IsNullOrWhiteSpace(runPreviewPath))
            {
                PreviewSimilarityStats similarity = ComparePreviewImages(previousPreviewPath, runPreviewPath);
                // #region agent log
                AppendAgentDebugLog(
                    "pre-fix",
                    "H10",
                    "RemoteGenerationBackend.cs:232",
                    "preview_similarity_between_prompt_variants",
                    $"{{\"requestId\":\"{EscapeJson(requestId)}\",\"runIndex\":{runIndex.ToString(CultureInfo.InvariantCulture)},\"previousPreview\":\"{EscapeJson(Path.GetFileName(previousPreviewPath))}\",\"currentPreview\":\"{EscapeJson(Path.GetFileName(runPreviewPath))}\",\"meanAbsoluteDifference\":{similarity.MeanAbsoluteDifference.ToString("0.######", CultureInfo.InvariantCulture)},\"maxChannelDifference\":{similarity.MaxChannelDifference.ToString("0.######", CultureInfo.InvariantCulture)}}}");
                // #endregion
            }
            if (!string.IsNullOrWhiteSpace(runPreviewPath))
                previousPreviewPath = runPreviewPath;
            if (!string.IsNullOrWhiteSpace(previousPromptOnlyPath) && !string.IsNullOrWhiteSpace(runPromptOnlyPath))
            {
                PreviewSimilarityStats promptOnlySimilarity = ComparePreviewImages(previousPromptOnlyPath, runPromptOnlyPath);
                // #region agent log
                AppendAgentDebugLog(
                    "pre-fix",
                    "H12,H13",
                    "RemoteGenerationBackend.cs:244",
                    "prompt_only_similarity_between_prompt_variants",
                    $"{{\"requestId\":\"{EscapeJson(requestId)}\",\"runIndex\":{runIndex.ToString(CultureInfo.InvariantCulture)},\"previousPromptOnly\":\"{EscapeJson(Path.GetFileName(previousPromptOnlyPath))}\",\"currentPromptOnly\":\"{EscapeJson(Path.GetFileName(runPromptOnlyPath))}\",\"meanAbsoluteDifference\":{promptOnlySimilarity.MeanAbsoluteDifference.ToString("0.######", CultureInfo.InvariantCulture)},\"maxChannelDifference\":{promptOnlySimilarity.MaxChannelDifference.ToString("0.######", CultureInfo.InvariantCulture)}}}");
                // #endregion
            }
            if (!string.IsNullOrWhiteSpace(runPromptOnlyPath))
                previousPromptOnlyPath = runPromptOnlyPath;
            if (!string.IsNullOrWhiteSpace(previousMeshSourcePath) && !string.IsNullOrWhiteSpace(runMeshSourcePath))
            {
                PreviewSimilarityStats meshSourceSimilarity = ComparePreviewImages(previousMeshSourcePath, runMeshSourcePath);
                // #region agent log
                AppendAgentDebugLog(
                    "pre-fix",
                    "H14",
                    "RemoteGenerationBackend.cs:256",
                    "mesh_source_similarity_between_prompt_variants",
                    $"{{\"requestId\":\"{EscapeJson(requestId)}\",\"runIndex\":{runIndex.ToString(CultureInfo.InvariantCulture)},\"previousMeshSource\":\"{EscapeJson(Path.GetFileName(previousMeshSourcePath))}\",\"currentMeshSource\":\"{EscapeJson(Path.GetFileName(runMeshSourcePath))}\",\"meanAbsoluteDifference\":{meshSourceSimilarity.MeanAbsoluteDifference.ToString("0.######", CultureInfo.InvariantCulture)},\"maxChannelDifference\":{meshSourceSimilarity.MaxChannelDifference.ToString("0.######", CultureInfo.InvariantCulture)}}}");
                // #endregion
            }
            if (!string.IsNullOrWhiteSpace(runMeshSourcePath))
                previousMeshSourcePath = runMeshSourcePath;
            if (!string.IsNullOrWhiteSpace(previousMaskGuidedMeshSourcePath) && !string.IsNullOrWhiteSpace(runMaskGuidedMeshSourcePath))
            {
                PreviewSimilarityStats maskGuidedSimilarity = ComparePreviewImages(previousMaskGuidedMeshSourcePath, runMaskGuidedMeshSourcePath);
                // #region agent log
                AppendAgentDebugLog(
                    "pre-fix",
                    "H16",
                    "RemoteGenerationBackend.cs:268",
                    "mask_guided_mesh_source_similarity_between_prompt_variants",
                    $"{{\"requestId\":\"{EscapeJson(requestId)}\",\"runIndex\":{runIndex.ToString(CultureInfo.InvariantCulture)},\"previousMaskGuidedMeshSource\":\"{EscapeJson(Path.GetFileName(previousMaskGuidedMeshSourcePath))}\",\"currentMaskGuidedMeshSource\":\"{EscapeJson(Path.GetFileName(runMaskGuidedMeshSourcePath))}\",\"meanAbsoluteDifference\":{maskGuidedSimilarity.MeanAbsoluteDifference.ToString("0.######", CultureInfo.InvariantCulture)},\"maxChannelDifference\":{maskGuidedSimilarity.MaxChannelDifference.ToString("0.######", CultureInfo.InvariantCulture)}}}");
                // #endregion
            }
            if (!string.IsNullOrWhiteSpace(runMaskGuidedMeshSourcePath))
                previousMaskGuidedMeshSourcePath = runMaskGuidedMeshSourcePath;
            // #region agent log
            AppendAgentDebugLog(
                "pre-fix",
                "H5",
                "RemoteGenerationBackend.cs:234",
                "per_proxy_run_outputs",
                $"{{\"requestId\":\"{EscapeJson(requestId)}\",\"runIndex\":{runIndex.ToString(CultureInfo.InvariantCulture)},\"occupyProxyId\":\"{EscapeJson(occupyProxyId)}\",\"promptId\":\"{EscapeJson(promptId)}\",\"runOutputPrefix\":\"{EscapeJson(runOutputPrefix)}\",\"runSavedOutputCount\":{runSavedOutputs.Count.ToString(CultureInfo.InvariantCulture)},\"firstSavedOutput\":\"{EscapeJson(runSavedOutputs.Count > 0 ? Path.GetFileName(runSavedOutputs[0]) : string.Empty)}\"}}");
            // #endregion

            savedOutputs.AddRange(runSavedOutputs);

            // #region agent log
            AppendDebugLog(
                "baseline",
                "H7",
                "RemoteGenerationBackend.GenerateAsync:run_saved_outputs",
                "Completed one per-proxy run",
                $"{{\"requestId\":\"{EscapeJson(requestId)}\",\"runIndex\":{runIndex},\"occupyProxyId\":\"{EscapeJson(occupyProxyId)}\",\"promptId\":\"{EscapeJson(promptId)}\",\"runSavedOutputCount\":{runSavedOutputs.Count},\"runSavedOutputNames\":\"{EscapeJson(string.Join(";", runSavedOutputs))}\"}}");
            // #endregion
        }

        var refreshSw = Stopwatch.StartNew();
        RefreshAssetDatabaseIfNeeded();
        refreshSw.Stop();
        totalSw.Stop();

        if (savedOutputs.Count == 0)
            Debug.LogWarning($"ComfyUI returned no downloadable outputs for request_id={requestId}");
        else
            Debug.Log($"ComfyUI saved {savedOutputs.Count} file(s) to {outputDir}");

        Debug.Log(
            $"ComfyUI timings request_id={requestId} runs={runCount} " +
            $"submit_ms={submitMsTotal} " +
            $"execution_ms={executionMsTotal} " +
            $"download_ms={downloadMsTotal} " +
            $"asset_refresh_ms={refreshSw.ElapsedMilliseconds} " +
            $"total_ms={totalSw.ElapsedMilliseconds}");

        var result = new GenerationResult();
        result.outputFiles.AddRange(savedOutputs);
        result.primaryOutputFile = ChoosePrimaryOutput(savedOutputs);

        // If ComfyUI finished but produced no downloadable assets, fall back to proxy primitives.
        if (savedOutputs.Count == 0)
        {
            GenerationResult fallback = ConvertConstraintsToResult(request.LegacyConstraints);
            fallback.outputFiles.AddRange(savedOutputs);
            fallback.primaryOutputFile = string.Empty;
            // #region agent log
            AppendDebugLog(
                "baseline",
                "H2",
                "RemoteGenerationBackend.GenerateAsync:fallback_result",
                "Returning proxy-converted fallback result",
                $"{{\"requestId\":\"{EscapeJson(requestId)}\",\"fallbackObjectCount\":{fallback.objects.Count},\"fallbackOutputFileCount\":{fallback.outputFiles.Count}}}");
            // #endregion
            return fallback;
        }

        // #region agent log
        AppendDebugLog(
            "baseline",
            "H3",
            "RemoteGenerationBackend.GenerateAsync:final_result",
            "Returning non-fallback result",
            $"{{\"requestId\":\"{EscapeJson(requestId)}\",\"resultOutputFileCount\":{result.outputFiles.Count},\"primaryOutputFile\":\"{EscapeJson(result.primaryOutputFile)}\",\"runCount\":{runCount}}}");
        // #endregion
        return result;
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

    private string WriteBase64PngIfPresent(string base64, string runInputDir, string label)
    {
        if (string.IsNullOrWhiteSpace(base64))
            return string.Empty;

        try
        {
            byte[] bytes = Convert.FromBase64String(base64);
            if (bytes == null || bytes.Length == 0)
                return string.Empty;

            string destPath = Path.Combine(runInputDir, $"{label}.png");
            File.WriteAllBytes(destPath, bytes);
            return destPath;
        }
        catch
        {
            return string.Empty;
        }
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
            // #region agent log
            AppendDebugLog(
                "baseline",
                "H1",
                "RemoteGenerationBackend.UploadMaskIfPresentAsync:mask_upload_failed",
                "Mask upload endpoint failed; retrying image endpoint",
                $"{{\"localPath\":\"{EscapeJson(localPath ?? string.Empty)}\",\"fileExists\":{(File.Exists(localPath) ? "true" : "false")},\"fileBytes\":{(File.Exists(localPath) ? new FileInfo(localPath).Length.ToString(CultureInfo.InvariantCulture) : "0")},\"error\":\"{EscapeJson(ex.Message ?? string.Empty)}\"}}");
            // #endregion
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
            ? request?.Prompt ?? _settings.prompt ?? string.Empty
            : promptOverride;
        string negativePrompt = request?.NegativePrompt ?? _settings.negativePrompt ?? string.Empty;
        string meshPrompt = string.IsNullOrWhiteSpace(meshPromptOverride) ? prompt : meshPromptOverride;
        string meshNegativePrompt = string.IsNullOrWhiteSpace(meshNegativePromptOverride) ? negativePrompt : meshNegativePromptOverride;
        string checkpointName = string.IsNullOrWhiteSpace(_settings.comfyCheckpointName)
            ? "motiondesignv13dartC4D_v10.safetensors"
            : _settings.comfyCheckpointName.Trim();

        string workflow = File.ReadAllText(workflowPath);
        bool templateHasPromptToken = workflow.Contains("__PROMPT__", StringComparison.Ordinal);
        bool templateHasNegativePromptToken = workflow.Contains("__NEG_PROMPT__", StringComparison.Ordinal);
        workflow = workflow.Replace("__SEED__", seed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        workflow = workflow.Replace("__STEPS__", steps.ToString(System.Globalization.CultureInfo.InvariantCulture));
        workflow = workflow.Replace("__CFG__", cfg.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        workflow = workflow.Replace("__CHECKPOINT__", EscapeJson(checkpointName));
        workflow = workflow.Replace("__PROMPT__", EscapeJson(prompt));
        workflow = workflow.Replace("__NEG_PROMPT__", EscapeJson(negativePrompt));
        workflow = workflow.Replace("__MESH_PROMPT__", EscapeJson(meshPrompt));
        workflow = workflow.Replace("__MESH_NEG_PROMPT__", EscapeJson(meshNegativePrompt));
        workflow = workflow.Replace("__DEPTH_IMAGE__", EscapeJson(depthName));
        workflow = workflow.Replace("__CANNY_IMAGE__", EscapeJson(cannyName));
        workflow = workflow.Replace("__MASK_IMAGE_COUNT__", maskNames.Count.ToString());
        workflow = workflow.Replace("__MASK_OCCUPY_IMAGE__", EscapeJson(maskOccupyName));
        workflow = workflow.Replace("__MASK_AVOID_IMAGE__", EscapeJson(maskAvoidName));
        workflow = workflow.Replace("__MASK_FOCUS_IMAGE__", EscapeJson(maskFocusName));
        workflow = workflow.Replace("__MASK_OCCUPY_WEIGHT__", Mathf.Clamp01(request?.MaskOccupyWeight ?? 1f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        workflow = workflow.Replace("__MASK_AVOID_WEIGHT__", Mathf.Clamp01(request?.MaskAvoidWeight ?? 1f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        workflow = workflow.Replace("__MASK_FOCUS_WEIGHT__", Mathf.Clamp01(request?.MaskFocusWeight ?? 1f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

        for (int i = 0; i < maskNames.Count; i++)
            workflow = workflow.Replace($"__MASK_IMAGE_{i}__", EscapeJson(maskNames[i]));

        // #region agent log
        AppendAgentDebugLog(
            "pre-fix",
            "H3",
            "RemoteGenerationBackend.cs:764",
            "workflow_prompt_binding",
            $"{{\"workflowPath\":\"{EscapeJson(workflowPath)}\",\"resolvedSeed\":{seed.ToString(CultureInfo.InvariantCulture)},\"templateHasPromptToken\":{(templateHasPromptToken ? "true" : "false")},\"templateHasNegativePromptToken\":{(templateHasNegativePromptToken ? "true" : "false")},\"resolvedPrompt\":\"{EscapeJson(SummarizeTextForDebug(prompt))}\",\"resolvedNegativePrompt\":\"{EscapeJson(SummarizeTextForDebug(negativePrompt))}\",\"resolvedMeshPrompt\":\"{EscapeJson(SummarizeTextForDebug(meshPrompt))}\",\"resolvedMeshNegativePrompt\":\"{EscapeJson(SummarizeTextForDebug(meshNegativePrompt))}\",\"depthName\":\"{EscapeJson(depthName)}\",\"cannyName\":\"{EscapeJson(cannyName)}\",\"maskOccupyName\":\"{EscapeJson(maskOccupyName)}\",\"maskAvoidName\":\"{EscapeJson(maskAvoidName)}\",\"maskFocusName\":\"{EscapeJson(maskFocusName)}\",\"boundWorkflowContainsPromptText\":{(!string.IsNullOrWhiteSpace(prompt) && workflow.Contains(EscapeJson(prompt), StringComparison.Ordinal) ? "true" : "false")},\"boundWorkflowContainsNegativePromptText\":{(!string.IsNullOrWhiteSpace(negativePrompt) && workflow.Contains(EscapeJson(negativePrompt), StringComparison.Ordinal) ? "true" : "false")}}}");
        // #endregion

        return workflow;
    }

    private static string ResolveRunPrompt(NewBackendRequest request, string occupyProxyId)
    {
        string basePrompt = request?.Prompt ?? string.Empty;
        string assetPrompt = ResolvePerProxyAssetPrompt(request, occupyProxyId);
        if (string.IsNullOrWhiteSpace(assetPrompt))
            return basePrompt;
        if (string.IsNullOrWhiteSpace(basePrompt))
            return assetPrompt;
        return $"{basePrompt}, {assetPrompt}";
    }

    private static string BuildMeshSourcePrompt(string runPrompt, string occupyProxyId, NewBackendRequest request)
    {
        string assetPrompt = ResolvePerProxyAssetPrompt(request, occupyProxyId);
        string primaryPrompt = string.IsNullOrWhiteSpace(assetPrompt) ? (runPrompt ?? string.Empty) : assetPrompt;
        string stylePrompt = BuildMeshStylePrompt(runPrompt, assetPrompt);
        string combined =
            $"{primaryPrompt}, {stylePrompt}, single main object, single centered asset, full object in frame, isolated object render, transparent background, alpha background, no floor, no pedestal, no support, no platform, no environment, no scenery, no architecture, no extra objects, product render, studio cutout";
        return Regex.Replace(combined, "\\s+", " ").Trim().Trim(',');
    }

    private static string BuildMeshSourceNegativePrompt(NewBackendRequest request)
    {
        string negativePrompt = request?.NegativePrompt ?? string.Empty;
        string combined =
            $"{negativePrompt}, scene, environment, scenery, landscape, architecture, building, room, platform, pedestal, floor, ground plane, support structure, extra objects, attached objects, multiple objects, background clutter, close-up crop, partial object, frame edge crop, occluders";
        return Regex.Replace(combined, "\\s+", " ").Trim().Trim(',');
    }

    private static string BuildMeshStylePrompt(string runPrompt, string assetPrompt)
    {
        string value = runPrompt ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(assetPrompt))
            value = Regex.Replace(value, $"\\b{Regex.Escape(assetPrompt)}\\b", " ", RegexOptions.IgnoreCase);

        value = Regex.Replace(value, "\\bscene\\b", "asset", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, "\\benvironment\\b", " ", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, "\\bscenery\\b", " ", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, "\\blandscape\\b", " ", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, "\\bbackground\\b", " ", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, "\\s+", " ").Trim().Trim(',');

        if (string.IsNullOrWhiteSpace(value))
            return "high quality 3d asset";

        return value;
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
                    int refs = ExtractOutputImageRefs(json).Count;
                    bool hasOutputRefs = refs > 0;
                    bool completed = IsHistoryCompleted(json);
                    // #region agent log
                    AppendDebugLog(
                        "baseline",
                        "H4",
                        "RemoteGenerationBackend.WaitForCompletionByHistoryPollingAsync:history_observed",
                        "History polling state observed",
                        $"{{\"promptId\":\"{EscapeJson(promptId)}\",\"completed\":{(completed ? "true" : "false")},\"hasOutputRefs\":{(hasOutputRefs ? "true" : "false")},\"outputRefCount\":{refs},\"historyChars\":{json.Length.ToString(CultureInfo.InvariantCulture)}}}");
                    // #endregion
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
        {
            // #region agent log
            AppendDebugLog(
                "baseline",
                "H2",
                "RemoteGenerationBackend.ConvertConstraintsToResult:null_constraints",
                "Fallback conversion had no constraints",
                "{\"constraintCount\":0,\"convertedObjectCount\":0}");
            // #endregion
            return result;
        }

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
        // #region agent log
        AppendDebugLog(
            "baseline",
            "H2",
            "RemoteGenerationBackend.ConvertConstraintsToResult:converted",
            "Converted proxy constraints to generated primitives",
            $"{{\"constraintCount\":{constraints.Length},\"convertedObjectCount\":{result.objects.Count}}}");
        // #endregion

        return result;
    }

    private static string ChoosePrimaryOutput(List<string> outputPaths)
    {
        if (outputPaths == null || outputPaths.Count == 0)
            return string.Empty;

        for (int i = 0; i < outputPaths.Count; i++)
        {
            string path = outputPaths[i];
            if (string.IsNullOrWhiteSpace(path))
                continue;
            string ext = Path.GetExtension(path);
            if (ext.Equals(".glb", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".gltf", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".obj", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        for (int i = 0; i < outputPaths.Count; i++)
        {
            string path = outputPaths[i];
            if (string.IsNullOrWhiteSpace(path))
                continue;
            if (Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
                return path;
        }

        return outputPaths[0] ?? string.Empty;
    }

    private async Task<List<string>> DownloadOutputsAsync(string promptId, string requestId, string outputDir, string outputPrefix = null)
    {
        Directory.CreateDirectory(outputDir);
        using var cts = new CancellationTokenSource(Math.Max(1000, _settings.remoteTimeoutSeconds * 1000));
        using HttpResponseMessage response = await Http.GetAsync($"{GetComfyBaseUrl().TrimEnd('/')}/history/{promptId}", cts.Token);
        string historyJson = await response.Content.ReadAsStringAsync();
        int firstFilenameIndex = (historyJson ?? string.Empty).IndexOf("\"filename\"", StringComparison.Ordinal);
        int firstOutputTypeIndex = (historyJson ?? string.Empty).IndexOf("\"type\":\"output\"", StringComparison.Ordinal);
        string filenameSnippet = BuildSanitizedSnippet(historyJson, firstFilenameIndex, 220);
        string outputTypeSnippet = BuildSanitizedSnippet(historyJson, firstOutputTypeIndex, 220);
        int genericFilenameCount = Regex.Matches(historyJson ?? string.Empty, "\"filename\"\\s*:\\s*\"([^\"]+)\"").Count;
        int outputTypeCount = Regex.Matches(historyJson ?? string.Empty, "\"type\"\\s*:\\s*\"output\"").Count;
        int orderedFilenameSubfolderTypeCount = Regex.Matches(
            historyJson ?? string.Empty,
            "\"filename\"\\s*:\\s*\"([^\"]+)\"\\s*,\\s*\"subfolder\"\\s*:\\s*\"([^\"]*)\"\\s*,\\s*\"type\"\\s*:\\s*\"([^\"]+)\"").Count;
        int filenameSubfolderNullTypeCount = Regex.Matches(
            historyJson ?? string.Empty,
            "\"filename\"\\s*:\\s*\"([^\"]+)\"\\s*,\\s*\"subfolder\"\\s*:\\s*null\\s*,\\s*\"type\"\\s*:\\s*\"([^\"]+)\"").Count;
        int typeBeforeFilenameCount = Regex.Matches(
            historyJson ?? string.Empty,
            "\"type\"\\s*:\\s*\"output\"[\\s\\S]{0,120}\"filename\"\\s*:\\s*\"([^\"]+)\"").Count;
        // #region agent log
        AppendDebugLog(
            "baseline",
            "H3",
            "RemoteGenerationBackend.DownloadOutputsAsync:history_downloaded",
            "Fetched prompt history for output extraction",
            $"{{\"promptId\":\"{EscapeJson(promptId)}\",\"requestId\":\"{EscapeJson(requestId)}\",\"statusCode\":{((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)},\"historyChars\":{historyJson.Length.ToString(CultureInfo.InvariantCulture)},\"genericFilenameCount\":{genericFilenameCount},\"outputTypeCount\":{outputTypeCount},\"orderedFilenameSubfolderTypeCount\":{orderedFilenameSubfolderTypeCount},\"filenameSubfolderNullTypeCount\":{filenameSubfolderNullTypeCount},\"typeBeforeFilenameCount\":{typeBeforeFilenameCount},\"firstFilenameIndex\":{firstFilenameIndex},\"firstOutputTypeIndex\":{firstOutputTypeIndex}}}");
        // #endregion
        // #region agent log
        AppendDebugLog(
            "baseline",
            "H6",
            "RemoteGenerationBackend.DownloadOutputsAsync:history_shape_snippets",
            "Sanitized history snippets around filename/output markers",
            $"{{\"promptId\":\"{EscapeJson(promptId)}\",\"filenameSnippet\":\"{EscapeJson(filenameSnippet)}\",\"outputTypeSnippet\":\"{EscapeJson(outputTypeSnippet)}\"}}");
        // #endregion
        if (!response.IsSuccessStatusCode)
            throw new Exception($"ComfyUI /history/{promptId} failed: {(int)response.StatusCode} {historyJson}");

        var outputs = ExtractOutputImageRefs(historyJson);
        // #region agent log
        AppendDebugLog(
            "baseline",
            "H5",
            "RemoteGenerationBackend.DownloadOutputsAsync:extraction_result",
            "Parsed output references using current extractor",
            $"{{\"promptId\":\"{EscapeJson(promptId)}\",\"extractedCount\":{outputs.Count},\"genericFilenameCount\":{genericFilenameCount},\"outputTypeCount\":{outputTypeCount}}}");
        // #endregion
        // #region agent log
        AppendDebugLog(
            "baseline",
            "H1",
            "RemoteGenerationBackend.DownloadOutputsAsync:extracted_refs",
            "Extracted output references details",
            $"{{\"promptId\":\"{EscapeJson(promptId)}\",\"requestId\":\"{EscapeJson(requestId)}\",\"extractedRefs\":\"{EscapeJson(string.Join(";", outputs.ConvertAll(o => $"{o.filename}|{o.subfolder}|{o.type}")))}\"}}");
        // #endregion
        // #region agent log
        AppendAgentDebugLog(
            "pre-fix",
            "H6,H7",
            "RemoteGenerationBackend.cs:1254",
            "download_output_refs",
            $"{{\"promptId\":\"{EscapeJson(promptId)}\",\"requestId\":\"{EscapeJson(requestId)}\",\"outputRefCount\":{outputs.Count.ToString(CultureInfo.InvariantCulture)},\"outputRefs\":\"{EscapeJson(string.Join(";", outputs.ConvertAll(o => $"{o.filename}|{o.subfolder}|{o.type}")))}\"}}");
        // #endregion
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

            if (finalName.IndexOf("spatialgen_preview", StringComparison.OrdinalIgnoreCase) >= 0 ||
                finalName.IndexOf("spatialgen_prompt_only", StringComparison.OrdinalIgnoreCase) >= 0 ||
                finalName.IndexOf("spatialgen_mesh_source", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                PreviewOccupancyStats stats = AnalyzePreviewOccupancy(outPath);
                string kind = finalName.IndexOf("spatialgen_prompt_only", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "prompt_only"
                    : finalName.IndexOf("spatialgen_mesh_source", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "mesh_source"
                    : "conditioned_preview";
                // #region agent log
                AppendAgentDebugLog(
                    "pre-fix",
                    "H8,H9",
                    "RemoteGenerationBackend.cs:1274",
                    "preview_image_occupancy",
                    $"{{\"promptId\":\"{EscapeJson(promptId)}\",\"requestId\":\"{EscapeJson(requestId)}\",\"kind\":\"{EscapeJson(kind)}\",\"savedPreview\":\"{EscapeJson(Path.GetFileName(outPath))}\",\"occupiedPixelRatio\":{stats.OccupiedPixelRatio.ToString("0.######", CultureInfo.InvariantCulture)},\"occupiedBoundsRatio\":{stats.OccupiedBoundsRatio.ToString("0.######", CultureInfo.InvariantCulture)},\"bounds\":\"{EscapeJson(stats.BoundsSummary)}\"}}");
                // #endregion
            }
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

            PreviewOccupancyStats stats = AnalyzePreviewOccupancy(outPath);
            // #region agent log
            AppendAgentDebugLog(
                "pre-fix",
                "H16",
                "RemoteGenerationBackend.cs:1370",
                "mask_guided_mesh_source_occupancy",
                $"{{\"meshSource\":\"{EscapeJson(Path.GetFileName(meshSourcePath))}\",\"maskPath\":\"{EscapeJson(Path.GetFileName(maskPath))}\",\"savedPreview\":\"{EscapeJson(Path.GetFileName(outPath))}\",\"occupiedPixelRatio\":{stats.OccupiedPixelRatio.ToString("0.######", CultureInfo.InvariantCulture)},\"occupiedBoundsRatio\":{stats.OccupiedBoundsRatio.ToString("0.######", CultureInfo.InvariantCulture)},\"bounds\":\"{EscapeJson(stats.BoundsSummary)}\"}}");
            // #endregion

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

    private static PreviewOccupancyStats AnalyzePreviewOccupancy(string imagePath)
    {
        var stats = new PreviewOccupancyStats();
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return stats;

        Texture2D texture = null;
        try
        {
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            byte[] bytes = File.ReadAllBytes(imagePath);
            if (bytes == null || bytes.Length == 0 || !texture.LoadImage(bytes))
                return stats;

            int width = texture.width;
            int height = texture.height;
            if (width <= 0 || height <= 0)
                return stats;

            Color[] pixels = texture.GetPixels();
            Color background = EstimateBackgroundColor(texture);
            const float threshold = 0.06f;

            int minX = width;
            int minY = height;
            int maxX = -1;
            int maxY = -1;
            int occupiedPixels = 0;
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    Color pixel = pixels[row + x];
                    if (ColorDistance(pixel, background) <= threshold)
                        continue;

                    occupiedPixels++;
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }

            stats.OccupiedPixelRatio = occupiedPixels / (float)(width * height);
            if (maxX >= minX && maxY >= minY)
            {
                int boundsWidth = (maxX - minX) + 1;
                int boundsHeight = (maxY - minY) + 1;
                stats.OccupiedBoundsRatio = (boundsWidth * boundsHeight) / (float)(width * height);
                stats.BoundsSummary = $"{minX},{minY},{boundsWidth},{boundsHeight}";
            }

            return stats;
        }
        catch
        {
            return stats;
        }
        finally
        {
            if (texture != null)
                UnityEngine.Object.DestroyImmediate(texture);
        }
    }

    private static string FindFirstPreviewOutput(List<string> paths)
    {
        return FindFirstOutputContaining(paths, "spatialgen_preview");
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

    private static PreviewSimilarityStats ComparePreviewImages(string firstPath, string secondPath)
    {
        var stats = new PreviewSimilarityStats();
        if (string.IsNullOrWhiteSpace(firstPath) || string.IsNullOrWhiteSpace(secondPath) || !File.Exists(firstPath) || !File.Exists(secondPath))
            return stats;

        Texture2D first = null;
        Texture2D second = null;
        try
        {
            first = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            second = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            byte[] firstBytes = File.ReadAllBytes(firstPath);
            byte[] secondBytes = File.ReadAllBytes(secondPath);
            if (!first.LoadImage(firstBytes) || !second.LoadImage(secondBytes))
                return stats;
            if (first.width != second.width || first.height != second.height)
                return stats;

            Color[] firstPixels = first.GetPixels();
            Color[] secondPixels = second.GetPixels();
            if (firstPixels == null || secondPixels == null || firstPixels.Length != secondPixels.Length || firstPixels.Length == 0)
                return stats;

            float totalDifference = 0f;
            float maxDifference = 0f;
            for (int i = 0; i < firstPixels.Length; i++)
            {
                Color a = firstPixels[i];
                Color b = secondPixels[i];
                float diff =
                    (Mathf.Abs(a.r - b.r) +
                     Mathf.Abs(a.g - b.g) +
                     Mathf.Abs(a.b - b.b)) / 3f;
                totalDifference += diff;
                if (diff > maxDifference)
                    maxDifference = diff;
            }

            stats.MeanAbsoluteDifference = totalDifference / firstPixels.Length;
            stats.MaxChannelDifference = maxDifference;
            return stats;
        }
        catch
        {
            return stats;
        }
        finally
        {
            if (first != null)
                UnityEngine.Object.DestroyImmediate(first);
            if (second != null)
                UnityEngine.Object.DestroyImmediate(second);
        }
    }

    private static Color EstimateBackgroundColor(Texture2D texture)
    {
        int lastX = Mathf.Max(0, texture.width - 1);
        int lastY = Mathf.Max(0, texture.height - 1);
        Color c1 = texture.GetPixel(0, 0);
        Color c2 = texture.GetPixel(lastX, 0);
        Color c3 = texture.GetPixel(0, lastY);
        Color c4 = texture.GetPixel(lastX, lastY);
        return new Color(
            (c1.r + c2.r + c3.r + c4.r) * 0.25f,
            (c1.g + c2.g + c3.g + c4.g) * 0.25f,
            (c1.b + c2.b + c3.b + c4.b) * 0.25f,
            (c1.a + c2.a + c3.a + c4.a) * 0.25f);
    }

    private static float ColorDistance(Color a, Color b)
    {
        float dr = a.r - b.r;
        float dg = a.g - b.g;
        float db = a.b - b.b;
        return Mathf.Sqrt((dr * dr) + (dg * dg) + (db * db));
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
            // Never interrupt generation flow due to debug logging failures.
        }
    }

    private static void AppendAgentDebugLog(string runId, string hypothesisId, string location, string message, string dataJson)
    {
        try
        {
            string safeRunId = EscapeJson(runId ?? "pre-fix");
            string safeHypothesisId = EscapeJson(hypothesisId ?? string.Empty);
            string safeLocation = EscapeJson(location ?? string.Empty);
            string safeMessage = EscapeJson(message ?? string.Empty);
            string safeDataJson = string.IsNullOrWhiteSpace(dataJson) ? "{}" : dataJson;
            long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string line =
                $"{{\"sessionId\":\"{AgentDebugSessionId}\",\"runId\":\"{safeRunId}\",\"hypothesisId\":\"{safeHypothesisId}\",\"location\":\"{safeLocation}\",\"message\":\"{safeMessage}\",\"data\":{safeDataJson},\"timestamp\":{ts.ToString(CultureInfo.InvariantCulture)}}}";
            File.AppendAllText(AgentDebugLogPath, line + Environment.NewLine);
        }
        catch
        {
            // Never interrupt generation flow due to debug logging failures.
        }
    }

    private static string SummarizeTextForDebug(string value, int maxChars = 160)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string normalized = value.Trim().Replace("\r", " ").Replace("\n", " ");
        normalized = Regex.Replace(normalized, "\\s+", " ");
        return normalized.Length <= maxChars ? normalized : normalized.Substring(0, maxChars) + "...";
    }

    private static string SummarizePerProxyPromptsForDebug(NewBackendRequest request)
    {
        if (request?.PerProxyAssetPrompts == null || request.PerProxyAssetPrompts.Count == 0)
            return string.Empty;

        var parts = new List<string>();
        for (int i = 0; i < request.PerProxyAssetPrompts.Count; i++)
        {
            var entry = request.PerProxyAssetPrompts[i];
            if (entry == null)
                continue;

            parts.Add($"{entry.ProxyId}:{SummarizeTextForDebug(entry.AssetPrompt, 60)}");
        }

        return string.Join(";", parts);
    }

    private static string SummarizeLegacyOccupyProxyIdsForDebug(NewBackendRequest request)
    {
        if (request?.LegacyConstraints == null || request.LegacyConstraints.Length == 0)
            return string.Empty;

        var proxyIds = new List<string>();
        for (int i = 0; i < request.LegacyConstraints.Length; i++)
        {
            Constraint constraint = request.LegacyConstraints[i];
            if (!string.Equals((constraint?.type ?? string.Empty).Trim(), "occupy", StringComparison.OrdinalIgnoreCase))
                continue;

            proxyIds.Add(constraint?.proxy_id ?? string.Empty);
        }

        return string.Join(";", proxyIds);
    }

    private static float CalculateMaskCoverage(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return 0f;

        Texture2D texture = null;
        try
        {
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            byte[] bytes = File.ReadAllBytes(imagePath);
            if (bytes == null || bytes.Length == 0 || !texture.LoadImage(bytes))
                return 0f;

            Color[] pixels = texture.GetPixels();
            if (pixels == null || pixels.Length == 0)
                return 0f;

            int activePixels = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i].maxColorComponent > 0.5f)
                    activePixels++;
            }

            return (float)activePixels / pixels.Length;
        }
        catch
        {
            return 0f;
        }
        finally
        {
            if (texture != null)
                UnityEngine.Object.DestroyImmediate(texture);
        }
    }

    private static void RefreshAssetDatabaseIfNeeded()
    {
#if UNITY_EDITOR
        AssetDatabase.Refresh();
#endif
    }

    private static string BuildSanitizedSnippet(string text, int centerIndex, int radius)
    {
        if (string.IsNullOrEmpty(text) || centerIndex < 0)
            return string.Empty;

        int start = Math.Max(0, centerIndex - Math.Max(10, radius));
        int length = Math.Min(text.Length - start, Math.Max(20, radius * 2));
        string snippet = text.Substring(start, length);
        snippet = snippet.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        snippet = Regex.Replace(snippet, "\\s+", " ");
        return snippet;
    }

    private class ImageRef
    {
        public string filename;
        public string subfolder;
        public string type;
    }

    private class PreviewOccupancyStats
    {
        public float OccupiedPixelRatio;
        public float OccupiedBoundsRatio;
        public string BoundsSummary = string.Empty;
    }

    private class PreviewSimilarityStats
    {
        public float MeanAbsoluteDifference;
        public float MaxChannelDifference;
    }
}
