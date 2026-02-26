using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialGeneration.Generation.Intent
{
    public static class ConstraintDebugValidator
    {
        public sealed class ValidationReport
        {
            public readonly List<string> Errors = new();
            public readonly List<string> Warnings = new();
            public bool HasErrors => Errors.Count > 0;
        }

        public static ValidationReport Validate(SceneIntent sceneIntent, ConstraintSet constraintSet, CompiledConstraints compiledConstraints, int expectedWidth, int expectedHeight)
        {
            ValidationReport report = new();
            if (sceneIntent == null) { report.Errors.Add("SceneIntent is null."); return report; }
            if (constraintSet == null) { report.Errors.Add("ConstraintSet is null."); return report; }
            if (compiledConstraints == null) { report.Errors.Add("CompiledConstraints is null."); return report; }

            HashSet<string> proxyIds = new(StringComparer.Ordinal);
            if (sceneIntent.Proxies != null)
            {
                for (int i = 0; i < sceneIntent.Proxies.Count; i++)
                {
                    ProxyIntent p = sceneIntent.Proxies[i];
                    if (p == null || string.IsNullOrWhiteSpace(p.Id)) continue;
                    proxyIds.Add(p.Id);
                }
            }

            int occupyCount = 0, avoidCount = 0, focusCount = 0;
            if (constraintSet.Constraints != null)
            {
                for (int i = 0; i < constraintSet.Constraints.Count; i++)
                {
                    Constraint c = constraintSet.Constraints[i];
                    if (c == null) { report.Warnings.Add($"Constraint[{i}] is null."); continue; }
                    if (string.IsNullOrWhiteSpace(c.ProxyId)) report.Errors.Add($"Constraint[{i}] has empty ProxyId.");
                    else if (!proxyIds.Contains(c.ProxyId)) report.Errors.Add($"Constraint[{i}] references unknown ProxyId '{c.ProxyId}'.");
                    if (c.Weight < 0f || c.Weight > 1f) report.Errors.Add($"Constraint[{i}] weight {c.Weight:0.###} is out of range [0..1].");

                    switch (c.Type)
                    {
                        case ConstraintType.OccupyVolume: occupyCount++; break;
                        case ConstraintType.KeepEmpty: avoidCount++; break;
                        case ConstraintType.FocusRegion: focusCount++; break;
                    }
                }
            }

            ValidateMask("mask_occupy", compiledConstraints.MaskOccupy, occupyCount, expectedWidth, expectedHeight, report);
            ValidateMask("mask_avoid", compiledConstraints.MaskAvoid, avoidCount, expectedWidth, expectedHeight, report);
            ValidateMask("mask_focus", compiledConstraints.MaskFocus, focusCount, expectedWidth, expectedHeight, report);
            return report;
        }

        public static void LogToConsole(ValidationReport report)
        {
            if (report == null) return;
            for (int i = 0; i < report.Errors.Count; i++) Debug.LogError($"Constraint validation error: {report.Errors[i]}");
            for (int i = 0; i < report.Warnings.Count; i++) Debug.LogWarning($"Constraint validation warning: {report.Warnings[i]}");
        }

        private static void ValidateMask(string label, Texture2D mask, int constraintCount, int expectedWidth, int expectedHeight, ValidationReport report)
        {
            if (mask == null) { report.Errors.Add($"{label} is null."); return; }
            if (mask.width != expectedWidth || mask.height != expectedHeight)
            {
                report.Errors.Add($"{label} dimensions are {mask.width}x{mask.height}, expected {expectedWidth}x{expectedHeight}.");
                return;
            }

            bool allBlack = IsAllBlack(mask);
            if (constraintCount > 0 && allBlack) report.Errors.Add($"{label} is fully black but {constraintCount} corresponding constraints exist.");
            else if (constraintCount == 0 && !allBlack) report.Warnings.Add($"{label} has non-black pixels but there are zero corresponding constraints.");
        }

        private static bool IsAllBlack(Texture2D texture)
        {
            Color[] pixels = texture.GetPixels();
            const float eps = 1e-4f;
            for (int i = 0; i < pixels.Length; i++)
                if (pixels[i].r > eps || pixels[i].g > eps || pixels[i].b > eps) return false;
            return true;
        }
    }
}
