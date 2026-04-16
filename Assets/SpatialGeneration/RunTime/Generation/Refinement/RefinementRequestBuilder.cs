using System;
using UnityEngine;

public static class RefinementRequestBuilder
{
    public static RefinementRequest Build(
        string globalPrompt,
        string localPrompt,
        Texture2D rgb,
        Texture2D depth,
        Texture2D mask,
        RegionSelection selection,
        string sessionId = null,
        float denoiseStrength = 0.6f,
        int steps = 20,
        float cfgScale = 8f)
    {
        if (rgb == null)
            throw new ArgumentNullException(nameof(rgb));
        if (depth == null)
            throw new ArgumentNullException(nameof(depth));
        if (mask == null)
            throw new ArgumentNullException(nameof(mask));
        if (selection == null)
            throw new ArgumentNullException(nameof(selection));

        ValidateDimensions(depth, rgb.width, rgb.height, nameof(depth));
        ValidateDimensions(mask, rgb.width, rgb.height, nameof(mask));

        return new RefinementRequest
        {
            requestId = Guid.NewGuid().ToString("N"),
            globalPrompt = globalPrompt ?? string.Empty,
            localPrompt = localPrompt ?? string.Empty,
            rgbImageBase64 = EncodeTexture(rgb, nameof(rgb)),
            depthImageBase64 = EncodeTexture(depth, nameof(depth)),
            maskImageBase64 = EncodeTexture(mask, nameof(mask)),
            selection = CloneSelection(selection),
            denoiseStrength = Mathf.Clamp01(denoiseStrength),
            steps = Mathf.Max(1, steps),
            cfgScale = Mathf.Max(0f, cfgScale),
            sessionId = string.IsNullOrWhiteSpace(sessionId)
                ? $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}"
                : sessionId.Trim()
        };
    }

    private static void ValidateDimensions(Texture2D texture, int width, int height, string label)
    {
        if (texture.width != width || texture.height != height)
        {
            throw new ArgumentException(
                $"{label} dimensions {texture.width}x{texture.height} do not match expected {width}x{height}.",
                label);
        }
    }

    private static RegionSelection CloneSelection(RegionSelection selection)
    {
        return new RegionSelection
        {
            selectionId = selection.selectionId,
            center = selection.center,
            size = selection.size,
            rotation = selection.rotation
        };
    }

    private static string EncodeTexture(Texture2D texture, string label)
    {
        byte[] pngBytes;
        try
        {
            pngBytes = texture.EncodeToPNG();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to encode {label} as PNG. {ex.Message}", ex);
        }

        if (pngBytes == null || pngBytes.Length == 0)
            throw new InvalidOperationException($"PNG bytes for {label} are empty.");

        return Convert.ToBase64String(pngBytes);
    }
}
