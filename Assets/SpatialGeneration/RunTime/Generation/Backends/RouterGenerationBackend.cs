using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using SpatialGeneration.Generation.Backends;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Talks to the FastAPI router in <c>tools/comfy_router_backend</c>, which owns the ComfyUI
/// graphs and the choice of 3D lifter. Unity's job here is only to submit conditioning
/// images, wait, and pull the resulting files into the project.
/// </summary>
public sealed class RouterGenerationBackend : IGenerationBackend
{
    private static readonly string[] MeshExtensions = { ".glb", ".gltf", ".obj", ".fbx" };
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".webp" };

    private readonly BackendSettings _settings;

    public RouterGenerationBackend(BackendSettings settings) => _settings = settings;

    public string Name => _settings.backendPreset == BackendPreset.Colab ? "ComfyUI Router (Colab)" : "ComfyUI Router (local)";

    public async Task EnsureReadyAsync()
    {
        RouterClient.BackendHealth health = await RouterClient.CheckHealthAsync(_settings);

        // Router first: it is the only thing that knows where ComfyUI is meant to be, and
        // tools/start_backend.sh probes for a running ComfyUI when it boots.
        if (!health.RouterReachable && CanAutoStart())
        {
            await StartRouterAndWaitAsync();
            health = await RouterClient.CheckHealthAsync(_settings);
        }

        if (health.IsReady)
            return;

        if (health.RouterReachable && !health.ComfyReachable && CanAutoStart() && _settings.autoStartComfy)
        {
            await StartComfyAndWaitAsync(health.ComfyUrl);
            return;
        }

        throw new InvalidOperationException(RouterClient.DescribeProblem(_settings, health));
    }

    /// <summary>Only the Local preset runs processes we own; Colab's live in the notebook.</summary>
    private bool CanAutoStart() => _settings.backendPreset == BackendPreset.Local;

    private async Task StartRouterAndWaitAsync()
    {
        if (!_settings.autoStartRouter)
            return;

        Debug.Log($"Spatial Generation: router not running at {_settings.RouterBaseUrl}; starting it.");
        RouterProcessLauncher.Start();

        // A uvicorn boot is seconds, not minutes; the venv import dominates.
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000);

            int? exitCode = RouterProcessLauncher.ExitCodeIfDead;
            if (exitCode.HasValue)
            {
                throw new InvalidOperationException(
                    $"The router exited with code {exitCode.Value} while starting. " +
                    "Run ./tools/start_backend.sh in a terminal to see why.");
            }

            if ((await RouterClient.CheckHealthAsync(_settings)).RouterReachable)
            {
                Debug.Log("Spatial Generation: router is up.");
                return;
            }
        }

        throw new TimeoutException(
            $"The router did not answer at {_settings.RouterBaseUrl} within 60s. " +
            "Run ./tools/start_backend.sh in a terminal to see its output.");
    }

    private async Task StartComfyAndWaitAsync(string comfyUrl)
    {
        Debug.Log($"Spatial Generation: ComfyUI is not running at {comfyUrl}; starting it.");
        ComfyProcessLauncher.Start(comfyUrl, _settings.comfyLaunchCommand, _settings.comfyWorkingDirectory);

        int timeoutSeconds = Math.Max(10, _settings.comfyBootTimeoutSeconds);
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000);

            // A crashed launch will never become healthy, so fail on it immediately rather
            // than making the user wait out the whole timeout.
            int? exitCode = ComfyProcessLauncher.ExitCodeIfDead;
            if (exitCode.HasValue)
            {
                throw new InvalidOperationException(
                    $"ComfyUI exited with code {exitCode.Value} while starting up. " +
                    "Launch it manually to see why.");
            }

            RouterClient.BackendHealth health = await RouterClient.CheckHealthAsync(_settings);
            if (health.IsReady)
            {
                Debug.Log("Spatial Generation: ComfyUI is up.");
                return;
            }
        }

        throw new TimeoutException(
            $"ComfyUI did not become reachable at {comfyUrl} within {timeoutSeconds}s. " +
            "First launches load models and can be slow; raise comfyBootTimeoutSeconds or start it manually.");
    }

    public async Task<AssetGenerationResult> GenerateAssetAsync(AssetGenerationRequest request)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        await EnsureReadyAsync();

        string promptId = await SubmitAsync(request);
        await WaitForCompletionAsync(promptId);
        List<string> downloaded = await DownloadOutputsAsync(promptId, request);

        RefreshAssetDatabase();

        var result = new AssetGenerationResult
        {
            ProxyId = request.ProxyId,
            OutputFiles = downloaded,
            MeshPath = FindFirstWithExtension(downloaded, MeshExtensions)
        };

        // A run that finishes without geometry still needs to put *something* where the
        // proxy is, otherwise the user sees an empty scene and no explanation.
        if (!result.HasMesh)
        {
            result.FallbackPrimitive = request.Volume.ToPrimitive();
            Debug.LogWarning(
                $"Spatial Generation: run '{promptId}' for proxy '{request.ProxyId}' produced no mesh. " +
                "Placing the proxy primitive instead.");
        }

        return result;
    }

    private async Task<string> SubmitAsync(AssetGenerationRequest request)
    {
        string body = JsonUtility.ToJson(BuildRequestBody(request));
        string response = await RouterClient.PostJsonAsync(
            _settings.Endpoint("generate"), body, _settings.requestTimeoutSeconds);

        SubmitResponseBody parsed = JsonUtility.FromJson<SubmitResponseBody>(response);
        if (parsed == null || string.IsNullOrWhiteSpace(parsed.prompt_id))
            throw new InvalidOperationException($"Router /generate did not return a prompt_id:\n{response}");

        return parsed.prompt_id;
    }

    private GenerateRequestBody BuildRequestBody(AssetGenerationRequest request)
    {
        return new GenerateRequestBody
        {
            request_id = request.RequestId,
            mode = "generate",
            prompt = request.Prompt,
            negative_prompt = request.NegativePrompt,
            rgb_image = request.ReferenceImageBase64,
            depth_image = request.DepthBase64,
            edges_image = request.EdgesBase64,
            mask_image = request.MaskBase64,
            generation_model = _settings.generationModel == GenerationModel.TripoSR ? "tripo_sr" : "hunyuan_2_1",
            geometry_resolution = _settings.geometryResolution,
            tripo_threshold = _settings.tripoSrThreshold,
            proxy = new ProxyBody
            {
                id = request.ProxyId,
                role = request.Volume.Role.ToString().ToLowerInvariant(),
                shape = request.Volume.Shape.ToString().ToLowerInvariant(),
                label = request.Volume.Label,
                position = Vector3Body.From(request.Volume.Position),
                rotation = QuaternionBody.From(request.Volume.Rotation),
                size = Vector3Body.From(request.Volume.Size)
            },
            generation = new GenerationParamsBody
            {
                seed = request.Seed,
                steps = request.Steps,
                cfg = request.Cfg,
                sampler = request.Sampler,
                width = request.Width,
                height = request.Height
            }
        };
    }

    private async Task WaitForCompletionAsync(string promptId)
    {
        string url = _settings.Endpoint($"result/{Uri.EscapeDataString(promptId)}");
        DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(30, _settings.executionTimeoutSeconds));

        while (DateTime.UtcNow < deadline)
        {
            RunResultBody result = await TryPollAsync(url);
            if (result != null)
            {
                if (result.IsError)
                {
                    string detail = string.IsNullOrWhiteSpace(result.message) ? "no detail reported" : result.message;
                    throw new InvalidOperationException($"Backend run '{promptId}' failed: {detail}");
                }

                if (result.IsFinished)
                    return;
            }

            await Task.Delay(1000);
        }

        throw new TimeoutException(
            $"Backend run '{promptId}' did not finish within {_settings.executionTimeoutSeconds}s.");
    }

    /// <summary>Returns null for transient failures so polling survives a flaky tunnel.</summary>
    private async Task<RunResultBody> TryPollAsync(string url)
    {
        try
        {
            string json = await RouterClient.GetStringAsync(url, _settings.requestTimeoutSeconds);
            return JsonUtility.FromJson<RunResultBody>(json);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<List<string>> DownloadOutputsAsync(string promptId, AssetGenerationRequest request)
    {
        string json = await RouterClient.GetStringAsync(
            _settings.Endpoint($"result/{Uri.EscapeDataString(promptId)}"), _settings.requestTimeoutSeconds);
        RunResultBody result = JsonUtility.FromJson<RunResultBody>(json);

        var saved = new List<string>();
        if (result?.files == null || result.files.Count == 0)
            return saved;

        string outputDir = ResolveOutputDirectory();
        Directory.CreateDirectory(outputDir);
        string prefix = $"{Sanitize(request.RequestId)}_{Sanitize(request.ProxyId)}";

        // Meshes first so the caller's "first mesh wins" pick is deterministic.
        result.files.Sort(CompareByImportPriority);

        foreach (OutputFileBody file in result.files)
        {
            string url = _settings.Endpoint(
                $"view?filename={Uri.EscapeDataString(file.filename)}" +
                $"&subfolder={Uri.EscapeDataString(file.subfolder ?? string.Empty)}" +
                $"&type={Uri.EscapeDataString(file.type ?? "output")}");

            byte[] bytes = await RouterClient.GetBytesAsync(url, _settings.requestTimeoutSeconds);
            string path = Path.Combine(outputDir, $"{prefix}_{Path.GetFileName(file.filename)}");
            File.WriteAllBytes(path, bytes);
            saved.Add(path);
        }

        return saved;
    }

    private string ResolveOutputDirectory()
    {
        string configured = _settings.outputFolder;
        if (Path.IsPathRooted(configured))
            return configured;

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        return Path.Combine(projectRoot, configured);
    }

    /// <summary>
    /// Ranks the final textured mesh above intermediate meshes, and meshes above previews,
    /// so <see cref="FindFirstWithExtension"/> picks the asset the user actually wants.
    /// </summary>
    private static int CompareByImportPriority(OutputFileBody a, OutputFileBody b)
    {
        int rank = Rank(a).CompareTo(Rank(b));
        return rank != 0 ? rank : string.Compare(a?.filename, b?.filename, StringComparison.OrdinalIgnoreCase);

        static int Rank(OutputFileBody file)
        {
            string name = file?.filename ?? string.Empty;
            bool isMesh = HasExtension(name, MeshExtensions);
            if (isMesh && name.IndexOf("Final_Output", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
            if (isMesh) return 1;
            return HasExtension(name, ImageExtensions) ? 2 : 3;
        }
    }

    private static string FindFirstWithExtension(List<string> paths, string[] extensions)
    {
        foreach (string path in paths)
        {
            if (HasExtension(path, extensions) && File.Exists(path))
                return path;
        }
        return string.Empty;
    }

    private static bool HasExtension(string path, string[] extensions)
    {
        string ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
        return Array.IndexOf(extensions, ext) >= 0;
    }

    private static string Sanitize(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return "run";

        char[] chars = token.Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_' && chars[i] != '-')
                chars[i] = '_';
        }

        string sanitized = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? "run" : sanitized;
    }

    private static void RefreshAssetDatabase()
    {
#if UNITY_EDITOR
        AssetDatabase.Refresh();
#endif
    }
}
