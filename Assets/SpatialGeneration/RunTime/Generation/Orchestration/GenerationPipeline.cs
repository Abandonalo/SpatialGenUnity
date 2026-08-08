using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using SpatialGeneration.Generation.Intent;
using SpatialGeneration.Utils;

/// <summary>
/// Drives one Generate action end to end: snapshot the authored scene, render each occupy
/// proxy's conditioning images, and ask the backend for one asset per proxy.
///
/// Everything a run consumed is written to <c>Logs/&lt;session&gt;/&lt;run&gt;/</c> so a
/// generation can be reconstructed after the fact.
/// </summary>
public static class GenerationPipeline
{
    private static readonly string SessionId =
        $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";

    public static async Task<GenerationResult> GenerateAsync(
        string prompt,
        string negativePrompt,
        SceneStage stage = SceneStage.Creation)
    {
        BackendSettings settings = BackendRegistry.Settings;
        SceneIntent sceneIntent = SceneIntentBuilder.Build(stage);
        ConstraintSet constraints = ConstraintTranslator.Translate(sceneIntent);

        ReportValidationProblems(constraints.Validate(sceneIntent), settings.blockOnValidationErrors);

        List<ProxyIntent> occupyProxies = CollectOccupyProxies(sceneIntent);
        if (occupyProxies.Count == 0)
            throw new InvalidOperationException("No Occupy proxies in the scene. Add one before generating.");

        string artifactDir = CreateArtifactDirectory();
        WriteText(Path.Combine(artifactDir, "SceneIntent.json"), IntentJson.SerializeSceneIntent(sceneIntent));
        WriteText(Path.Combine(artifactDir, "ConstraintSet.json"), IntentJson.SerializeConstraintSet(constraints));

        IGenerationBackend backend = BackendRegistry.Current;
        await backend.EnsureReadyAsync();

        var result = new GenerationResult { ArtifactDirectory = artifactDir };
        Dictionary<string, SpatialProxy> liveProxies = BuildLiveProxyLookup();

        foreach (ProxyIntent proxy in occupyProxies)
        {
            AssetGenerationRequest request = BuildAssetRequest(
                proxy, liveProxies, settings, prompt, negativePrompt, artifactDir);

            result.Assets.Add(await backend.GenerateAssetAsync(request));
        }

        Debug.Log($"Spatial Generation: run artifacts saved to {artifactDir} (session={SessionId}).");
        return result;
    }

    private static AssetGenerationRequest BuildAssetRequest(
        ProxyIntent proxy,
        Dictionary<string, SpatialProxy> liveProxies,
        BackendSettings settings,
        string prompt,
        string negativePrompt,
        string artifactDir)
    {
        using ProxyConditioning conditioning = ProxyConditioningRenderer.Render(
            proxy, settings.captureWidth, settings.captureHeight);

        WriteConditioningArtifacts(artifactDir, proxy.Id, conditioning);

        liveProxies.TryGetValue(proxy.Id ?? string.Empty, out SpatialProxy liveProxy);
        Texture2D referenceImage = liveProxy != null && liveProxy.assetImage != null
            ? TextureUtils.MakeReadable(liveProxy.assetImage)
            : null;

        try
        {
            return new AssetGenerationRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                ProxyId = proxy.Id ?? string.Empty,
                // The per-proxy asset prompt leads so it outranks the shared style prompt.
                Prompt = JoinPrompts(proxy.AssetPrompt, prompt),
                NegativePrompt = negativePrompt ?? string.Empty,
                DepthBase64 = TextureUtils.EncodePngBase64(conditioning.Depth),
                EdgesBase64 = TextureUtils.EncodePngBase64(conditioning.Edges),
                MaskBase64 = TextureUtils.EncodePngBase64(conditioning.Mask),
                ReferenceImageBase64 = TextureUtils.EncodePngBase64(referenceImage),
                Volume = ToVolume(proxy),
                Seed = settings.seed,
                Steps = settings.steps,
                Cfg = settings.cfg,
                Sampler = settings.sampler,
                Width = settings.captureWidth,
                Height = settings.captureHeight
            };
        }
        finally
        {
            TextureUtils.Destroy(referenceImage);
        }
    }

    private static void WriteConditioningArtifacts(string artifactDir, string proxyId, ProxyConditioning conditioning)
    {
        string token = SanitizeFileToken(proxyId);
        TextureUtils.WritePng(conditioning.Depth, Path.Combine(artifactDir, $"{token}_depth.png"));
        TextureUtils.WritePng(conditioning.Edges, Path.Combine(artifactDir, $"{token}_edges.png"));
        TextureUtils.WritePng(conditioning.Mask, Path.Combine(artifactDir, $"{token}_mask.png"));
    }

    private static void ReportValidationProblems(List<string> problems, bool blockOnErrors)
    {
        if (problems.Count == 0)
            return;

        foreach (string problem in problems)
            Debug.LogError($"Constraint validation: {problem}");

        if (blockOnErrors)
            throw new InvalidOperationException(
                $"Constraint validation failed with {problems.Count} problem(s). See the console for details.");

        Debug.LogWarning("Continuing despite constraint validation errors (blockOnValidationErrors is off).");
    }

    private static List<ProxyIntent> CollectOccupyProxies(SceneIntent sceneIntent)
    {
        var occupy = new List<ProxyIntent>();
        if (sceneIntent?.Proxies == null)
            return occupy;

        foreach (ProxyIntent proxy in sceneIntent.Proxies)
        {
            if (proxy != null && proxy.Role == ProxyRole.Occupy && !string.IsNullOrWhiteSpace(proxy.Id))
                occupy.Add(proxy);
        }

        return occupy;
    }

    private static Dictionary<string, SpatialProxy> BuildLiveProxyLookup()
    {
        var lookup = new Dictionary<string, SpatialProxy>(StringComparer.Ordinal);
        foreach (SpatialProxy proxy in UnityEngine.Object.FindObjectsByType<SpatialProxy>(FindObjectsSortMode.None))
        {
            if (proxy != null && !string.IsNullOrWhiteSpace(proxy.ProxyId))
                lookup[proxy.ProxyId] = proxy;
        }

        return lookup;
    }

    private static ProxyVolume ToVolume(ProxyIntent proxy) => new()
    {
        Role = proxy.Role,
        Shape = proxy.Shape,
        Label = proxy.Label ?? string.Empty,
        Position = ToVector3(proxy.Pose?.Position, Vector3.zero),
        Rotation = ToQuaternion(proxy.Pose?.Rotation, Quaternion.identity),
        Size = ToVector3(proxy.Pose?.Scale, Vector3.one)
    };

    private static string JoinPrompts(params string[] parts)
    {
        var kept = new List<string>();
        foreach (string part in parts)
        {
            string trimmed = (part ?? string.Empty).Trim().Trim(',');
            if (trimmed.Length > 0)
                kept.Add(trimmed);
        }

        return string.Join(", ", kept);
    }

    private static string CreateArtifactDirectory()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string runTimestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        string artifactDir = Path.Combine(projectRoot, "Logs", "SpatialGeneration", SessionId, runTimestamp);
        Directory.CreateDirectory(artifactDir);
        return artifactDir;
    }

    private static void WriteText(string path, string contents) => File.WriteAllText(path, contents);

    private static string SanitizeFileToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "proxy";

        char[] chars = value.Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_' && chars[i] != '-')
                chars[i] = '_';
        }

        string sanitized = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? "proxy" : sanitized;
    }

    private static Vector3 ToVector3(Vector3Data value, Vector3 fallback) =>
        value == null ? fallback : new Vector3(value.X, value.Y, value.Z);

    private static Quaternion ToQuaternion(QuaternionData value, Quaternion fallback) =>
        value == null ? fallback : new Quaternion(value.X, value.Y, value.Z, value.W);
}
