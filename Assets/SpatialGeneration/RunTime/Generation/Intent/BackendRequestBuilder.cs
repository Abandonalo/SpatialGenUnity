using System;
using UnityEngine;

namespace SpatialGeneration.Generation.Intent
{
    public static class BackendRequestBuilder
    {
        public static BackendRequest Build(
            string prompt,
            string negativePrompt,
            Texture2D depthTexture,
            Texture2D edgesTexture,
            CompiledConstraints compiledConstraints,
            int seed,
            int steps,
            float cfg,
            string sampler,
            ConstraintSet constraintSet = null,
            string requestId = null)
        {
            if (compiledConstraints == null)
                throw new ArgumentNullException(nameof(compiledConstraints));
            if (depthTexture == null)
                throw new ArgumentNullException(nameof(depthTexture));
            if (compiledConstraints.MaskOccupy == null || compiledConstraints.MaskAvoid == null || compiledConstraints.MaskFocus == null)
                throw new ArgumentException("CompiledConstraints masks are required.", nameof(compiledConstraints));

            int width = depthTexture.width;
            int height = depthTexture.height;

            ValidateDimensions(compiledConstraints.MaskOccupy, width, height, "maskOccupy");
            ValidateDimensions(compiledConstraints.MaskAvoid, width, height, "maskAvoid");
            ValidateDimensions(compiledConstraints.MaskFocus, width, height, "maskFocus");

            if (edgesTexture != null && (edgesTexture.width != width || edgesTexture.height != height))
                throw new ArgumentException("edgesTexture dimensions must match depthTexture dimensions.", nameof(edgesTexture));

            string constraintSetJson = ResolveConstraintSetJson(compiledConstraints, constraintSet);
            if (string.IsNullOrWhiteSpace(constraintSetJson))
                throw new ArgumentException("constraintSetJson is empty. Provide constraintSet or compiledConstraints.ConstraintJson.");

            BackendRequest request = new()
            {
                RequestId = string.IsNullOrWhiteSpace(requestId) ? Guid.NewGuid().ToString("N") : requestId,
                Prompt = prompt ?? string.Empty,
                NegativePrompt = negativePrompt ?? string.Empty,
                ConstraintSetJson = constraintSetJson,
                MaskOccupyWeight = ResolveWeight(constraintSet, ConstraintType.OccupyVolume),
                MaskAvoidWeight = ResolveWeight(constraintSet, ConstraintType.KeepEmpty),
                MaskFocusWeight = ResolveWeight(constraintSet, ConstraintType.FocusRegion),
                Payload = new ComfyUIRequestPayload
                {
                    DepthBase64 = EncodePngBase64(depthTexture, nameof(depthTexture)),
                    EdgesBase64 = edgesTexture != null ? EncodePngBase64(edgesTexture, nameof(edgesTexture)) : string.Empty,
                    MaskOccupyBase64 = EncodePngBase64(compiledConstraints.MaskOccupy, "compiledConstraints.MaskOccupy"),
                    MaskAvoidBase64 = EncodePngBase64(compiledConstraints.MaskAvoid, "compiledConstraints.MaskAvoid"),
                    MaskFocusBase64 = EncodePngBase64(compiledConstraints.MaskFocus, "compiledConstraints.MaskFocus"),
                    Generation = new GenerationParams
                    {
                        Seed = seed,
                        Steps = steps,
                        Cfg = cfg,
                        Sampler = string.IsNullOrWhiteSpace(sampler) ? "euler" : sampler,
                        Width = width,
                        Height = height
                    }
                }
            };

            ValidateRequest(request);
            return request;
        }

        public static string ToJson(BackendRequest request, bool prettyPrint = true)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            return JsonUtility.ToJson(request, prettyPrint);
        }

        public static void ValidateRequest(BackendRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (request.Payload == null || request.Payload.Generation == null)
                throw new ArgumentException("Request payload or generation params missing.", nameof(request));
            if (request.Payload.Generation.Width <= 0 || request.Payload.Generation.Height <= 0)
                throw new ArgumentException("Generation width/height must be positive.");

            RequireNonEmptyBase64(request.Payload.DepthBase64, "depth");
            RequireNonEmptyBase64(request.Payload.MaskOccupyBase64, "mask_occupy");
            RequireNonEmptyBase64(request.Payload.MaskAvoidBase64, "mask_avoid");
            RequireNonEmptyBase64(request.Payload.MaskFocusBase64, "mask_focus");
            if (string.IsNullOrWhiteSpace(request.ConstraintSetJson))
                throw new ArgumentException("constraintSetJson must be non-empty.");
        }

        private static void RequireNonEmptyBase64(string value, string label)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"Base64 payload for '{label}' is empty.");
        }

        private static string ResolveConstraintSetJson(CompiledConstraints compiledConstraints, ConstraintSet constraintSet)
        {
            if (constraintSet != null) return IntentJson.SerializeConstraintSet(constraintSet);
            if (!string.IsNullOrWhiteSpace(compiledConstraints.ConstraintJson)) return compiledConstraints.ConstraintJson;
            return string.Empty;
        }

        private static void ValidateDimensions(Texture2D texture, int expectedWidth, int expectedHeight, string label)
        {
            if (texture.width != expectedWidth || texture.height != expectedHeight)
                throw new ArgumentException($"{label} dimensions {texture.width}x{texture.height} do not match expected {expectedWidth}x{expectedHeight}.");
        }

        private static string EncodePngBase64(Texture2D texture, string label)
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

        private static float ResolveWeight(ConstraintSet constraintSet, ConstraintType type)
        {
            if (constraintSet?.Constraints == null || constraintSet.Constraints.Count == 0)
                return 1f;

            float weight = 0f;
            for (int i = 0; i < constraintSet.Constraints.Count; i++)
            {
                Constraint c = constraintSet.Constraints[i];
                if (c == null || c.Type != type)
                    continue;
                weight = Mathf.Max(weight, c.Weight);
            }

            return weight <= 0f ? 1f : Mathf.Clamp01(weight);
        }
    }
}
