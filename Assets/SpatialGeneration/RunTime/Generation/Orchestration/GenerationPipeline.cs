using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using NewBackendRequest = SpatialGeneration.Generation.Intent.BackendRequest;
using NewBackendRequestBuilder = SpatialGeneration.Generation.Intent.BackendRequestBuilder;
using NewCompiledConstraints = SpatialGeneration.Generation.Intent.CompiledConstraints;
using NewConstraintSet = SpatialGeneration.Generation.Intent.ConstraintSet;
using NewConstraintTranslator = SpatialGeneration.Generation.Intent.ConstraintTranslator;
using NewIntentJson = SpatialGeneration.Generation.Intent.IntentJson;
using NewSceneIntent = SpatialGeneration.Generation.Intent.SceneIntent;
using NewSceneIntentBuilder = SpatialGeneration.Generation.Intent.SceneIntentBuilder;
using NewSceneStage = SpatialGeneration.Generation.Intent.SceneStage;

public static class GenerationPipeline
{
    private static readonly string SessionId = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";

    public static async Task<GenerationResult> GenerateAsync(SceneIntent intent)
    {
        BackendRequest request = ConstraintTranslator.Build(intent);
        IGenerationBackend backend = BackendRegistry.Current;
        return await backend.GenerateAsync(request);
    }

    public static async Task<GenerationResult> GenerateAsync(
        Camera captureCamera,
        int width,
        int height,
        string prompt,
        string negativePrompt,
        int seed,
        int steps,
        float cfg,
        string sampler,
        NewSceneStage stage = NewSceneStage.Creation)
    {
        if (captureCamera == null)
            throw new ArgumentNullException(nameof(captureCamera));

        int safeWidth = Mathf.Max(64, width);
        int safeHeight = Mathf.Max(64, height);

        NewSceneIntent sceneIntent = NewSceneIntentBuilder.Build(stage);
        NewConstraintSet constraintSet = NewConstraintTranslator.Translate(sceneIntent);
        NewCompiledConstraints compiledConstraints =
            SpatialGeneration.Generation.Intent.ConstraintCompiler.Compile(sceneIntent, constraintSet, captureCamera, safeWidth, safeHeight);

        var validationReport = SpatialGeneration.Generation.Intent.ConstraintDebugValidator.Validate(
            sceneIntent, constraintSet, compiledConstraints, safeWidth, safeHeight);
        SpatialGeneration.Generation.Intent.ConstraintDebugValidator.LogToConsole(validationReport);

        bool blockOnValidationErrors = BackendRegistry.Settings != null && BackendRegistry.Settings.blockOnValidationErrors;
        if (validationReport.HasErrors && blockOnValidationErrors)
            throw new Exception("Constraint validation failed. See console errors for details.");
        if (validationReport.HasErrors && !blockOnValidationErrors)
            Debug.LogWarning("Constraint validation reported errors, but generation will continue (blockOnValidationErrors=false).");

        HashSet<string> depthProxyIds = new();
        if (constraintSet?.Constraints != null)
        {
            for (int i = 0; i < constraintSet.Constraints.Count; i++)
            {
                var c = constraintSet.Constraints[i];
                if (c != null && !string.IsNullOrEmpty(c.ProxyId))
                    depthProxyIds.Add(c.ProxyId);
            }
        }

        Shader depthShader = Shader.Find("Hidden/SpatialGen/EncodeLinearDepth");
        if (depthShader == null)
            throw new Exception("Depth shader not found. Add Hidden/SpatialGen/EncodeLinearDepth.");

        Material depthMaterial = new(depthShader);
        depthMaterial.SetFloat("_MaxDepth", Mathf.Max(0.01f, captureCamera.farClipPlane));

        Texture2D depthTexture;
        try
        {
            depthTexture = SpatialGeneration.Generation.Intent.ConstraintCompiler.RenderProxyPass(
                sceneIntent, captureCamera, safeWidth, safeHeight, depthProxyIds, depthMaterial, 1f);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(depthMaterial);
        }

        Texture2D edgesTexture = BuildSimpleEdgeMap(depthTexture);

        NewBackendRequest request = NewBackendRequestBuilder.Build(
            prompt, negativePrompt, depthTexture, edgesTexture, compiledConstraints,
            seed, steps, cfg, sampler, constraintSet);

        string artifactDir = EnsureRunArtifactDirectory();
        string requestId = string.IsNullOrWhiteSpace(request.RequestId) ? Guid.NewGuid().ToString("N") : request.RequestId;

        string sceneIntentJsonPath = Path.Combine(artifactDir, "SceneIntent.json");
        string constraintSetJsonPath = Path.Combine(artifactDir, "ConstraintSet.json");
        string backendRequestJsonPath = Path.Combine(artifactDir, "BackendRequest.json");
        string generationParamsJsonPath = Path.Combine(artifactDir, "GenerationParams.json");
        string depthPath = Path.Combine(artifactDir, "depth.png");
        string edgesPath = Path.Combine(artifactDir, "edges.png");
        string occupyPath = Path.Combine(artifactDir, "mask_occupy.png");
        string avoidPath = Path.Combine(artifactDir, "mask_avoid.png");
        string focusPath = Path.Combine(artifactDir, "mask_focus.png");

        File.WriteAllText(sceneIntentJsonPath, NewIntentJson.SerializeSceneIntent(sceneIntent));
        File.WriteAllText(constraintSetJsonPath, NewIntentJson.SerializeConstraintSet(constraintSet));
        File.WriteAllText(backendRequestJsonPath, NewBackendRequestBuilder.ToJson(request, true));
        File.WriteAllText(generationParamsJsonPath, JsonUtility.ToJson(new GenerationRunMetadata
        {
            SessionId = SessionId,
            RequestId = requestId,
            TimestampUtc = DateTime.UtcNow.ToString("O"),
            Stage = stage.ToString(),
            Prompt = prompt ?? string.Empty,
            NegativePrompt = negativePrompt ?? string.Empty,
            Seed = seed,
            Steps = steps,
            Cfg = cfg,
            Sampler = sampler ?? string.Empty,
            Width = safeWidth,
            Height = safeHeight,
            CameraName = captureCamera.name,
            ConflictPolicy = constraintSet?.ConflictPolicy ?? string.Empty,
            ValidationErrorCount = validationReport.Errors.Count,
            ValidationWarningCount = validationReport.Warnings.Count,
            BlockOnValidationErrors = blockOnValidationErrors
        }, true));

        WriteTexturePng(depthTexture, depthPath);
        WriteTexturePng(edgesTexture, edgesPath);
        WriteTexturePng(compiledConstraints.MaskOccupy, occupyPath);
        WriteTexturePng(compiledConstraints.MaskAvoid, avoidPath);
        WriteTexturePng(compiledConstraints.MaskFocus, focusPath);

        SceneIntent legacyIntent = SceneIntentBuilder.Build();
        BackendRequest legacyRequest = ConstraintTranslator.Build(legacyIntent);
        legacyRequest.request_id = requestId;
        legacyRequest.prompt = request.Prompt;
        legacyRequest.negativePrompt = request.NegativePrompt;
        legacyRequest.constraintSetJson = request.ConstraintSetJson;
        legacyRequest.depthImagePath = depthPath;
        legacyRequest.cannyImagePath = edgesPath;
        legacyRequest.maskImagePaths = new[] { occupyPath, avoidPath, focusPath };
        legacyRequest.maskOccupyImagePath = occupyPath;
        legacyRequest.maskAvoidImagePath = avoidPath;
        legacyRequest.maskFocusImagePath = focusPath;
        legacyRequest.maskOccupyWeight = ResolveMaskWeight(constraintSet, SpatialGeneration.Generation.Intent.ConstraintType.OccupyVolume);
        legacyRequest.maskAvoidWeight = ResolveMaskWeight(constraintSet, SpatialGeneration.Generation.Intent.ConstraintType.KeepEmpty);
        legacyRequest.maskFocusWeight = ResolveMaskWeight(constraintSet, SpatialGeneration.Generation.Intent.ConstraintType.FocusRegion);
        File.WriteAllText(Path.Combine(artifactDir, "LegacyBackendRequest.json"), JsonUtility.ToJson(legacyRequest, true));

        IGenerationBackend backend = BackendRegistry.Current;
        Debug.Log($"Spatial Generation artifacts saved to {artifactDir} (session={SessionId}, request_id={requestId})");
        return await backend.GenerateAsync(legacyRequest);
    }

    private static string EnsureRunArtifactDirectory()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string runTimestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        string artifactDir = Path.Combine(projectRoot, "Logs", SessionId, runTimestamp);
        Directory.CreateDirectory(artifactDir);
        return artifactDir;
    }

    private static void WriteTexturePng(Texture2D texture, string path)
    {
        if (texture == null) return;
        byte[] bytes = texture.EncodeToPNG();
        File.WriteAllBytes(path, bytes);
    }

    private static Texture2D BuildSimpleEdgeMap(Texture2D source)
    {
        if (source == null) return null;
        int width = source.width;
        int height = source.height;
        Texture2D edges = new(width, height, TextureFormat.RGBA32, false);
        Color[] src = source.GetPixels();
        Color[] dst = new Color[src.Length];

        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int i = y * width + x;
                float gx =
                    -src[(y - 1) * width + (x - 1)].r + src[(y - 1) * width + (x + 1)].r +
                    -2f * src[y * width + (x - 1)].r + 2f * src[y * width + (x + 1)].r +
                    -src[(y + 1) * width + (x - 1)].r + src[(y + 1) * width + (x + 1)].r;
                float gy =
                    src[(y - 1) * width + (x - 1)].r + 2f * src[(y - 1) * width + x].r + src[(y - 1) * width + (x + 1)].r -
                    src[(y + 1) * width + (x - 1)].r - 2f * src[(y + 1) * width + x].r - src[(y + 1) * width + (x + 1)].r;
                float edge = Mathf.Clamp01(Mathf.Sqrt(gx * gx + gy * gy));
                dst[i] = new Color(edge, edge, edge, 1f);
            }
        }

        for (int x = 0; x < width; x++)
        {
            dst[x] = Color.black;
            dst[(height - 1) * width + x] = Color.black;
        }
        for (int y = 0; y < height; y++)
        {
            dst[y * width] = Color.black;
            dst[y * width + (width - 1)] = Color.black;
        }

        edges.SetPixels(dst);
        edges.Apply(false);
        return edges;
    }

    private static float ResolveMaskWeight(NewConstraintSet constraintSet, SpatialGeneration.Generation.Intent.ConstraintType type)
    {
        if (constraintSet?.Constraints == null || constraintSet.Constraints.Count == 0) return 1f;
        float weight = 0f;
        for (int i = 0; i < constraintSet.Constraints.Count; i++)
        {
            var c = constraintSet.Constraints[i];
            if (c == null || c.Type != type) continue;
            weight = Mathf.Max(weight, c.Weight);
        }
        return weight <= 0f ? 1f : Mathf.Clamp01(weight);
    }

    [Serializable]
    private class GenerationRunMetadata
    {
        public string SessionId;
        public string RequestId;
        public string TimestampUtc;
        public string Stage;
        public string Prompt;
        public string NegativePrompt;
        public int Seed;
        public int Steps;
        public float Cfg;
        public string Sampler;
        public int Width;
        public int Height;
        public string CameraName;
        public string ConflictPolicy;
        public int ValidationErrorCount;
        public int ValidationWarningCount;
        public bool BlockOnValidationErrors;
    }
}
