using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialGeneration.Agent
{
    // What the current job may touch. Every tool call goes through these checks, so the study
    // rules hold whatever the prompt says.
    public static partial class AgentSceneOps
    {
        // ------------------------------------------------------------------ rules

        private static bool TryJob(out AgentJob job, out OpResult error)
        {
            job = AgentJobState.instance.current;
            if (!string.IsNullOrEmpty(job?.previewToken) && RefinementPreviewStage.ForJob(job) == null)
            {
                error = OpResult.Fail("This preview has ended. Its draft is closed.");
                return false;
            }
            if (job == null || !job.running || string.IsNullOrEmpty(job.jobId))
            {
                error = OpResult.Fail(
                    "No Spatial Generation run is active. These tools only work while the Spatial Generation window runs a task.");
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>
        /// The one asset-level scope rule: may this job change this asset at all? Part-level
        /// region checks (<see cref="IsPartEditable"/>, <see cref="CheckPlacement"/>) come on top.
        /// </summary>
        internal static bool InScope(AgentJob job, AgentAsset asset)
        {
            if (job.mode == AgentJobMode.Generate)
                return job.condition == StudyCondition.SpatialControls
                    ? job.assetIds.Contains(asset.assetId)
                    : asset.createdByJob == job.jobId;
            if (job.HasPartScope)
                return AssetInSelection(job, asset);
            if (job.hasRegion)
                return true;
            // Condition A edit: one asset, fixed by the first change.
            return string.IsNullOrEmpty(job.lockedAssetId) || job.lockedAssetId == asset.assetId;
        }

        /// <summary>
        /// Condition A is the text-only baseline: an edit regenerates one object as a whole. An existing
        /// object may only be rebuilt after spatialgen_clear_asset; adding to it, updating or removing
        /// single parts first would be the local edit that only condition B's region selection offers.
        /// Objects the edit itself creates are the documented exception.
        /// </summary>
        internal static bool MustClearFirst(AgentJob job, AgentAsset asset) =>
            job.mode == AgentJobMode.Edit && job.condition == StudyCondition.TextOnly && !job.hasRegion &&
            asset.createdByJob != job.jobId && !job.clearedAssetIds.Contains(asset.assetId);

        /// <summary>
        /// In condition A the agent declared each volume itself when it created the asset, so after an
        /// edit rebuilds an object the volume should describe what is there now (a barrel made 1 m
        /// tall is a 1 m barrel). In condition B the volume is the designer's proxy and never moves.
        /// </summary>
        internal static bool VolumeFollowsParts(AgentJob job) =>
            job.mode == AgentJobMode.Edit && job.condition == StudyCondition.TextOnly && !job.hasRegion;

        /// <summary>
        /// Sets the volume of every asset this condition A edit cleared or created to the extent of its
        /// parts, in the volume convention (x and z centred on the origin, y from the ground up).
        /// Returns "label: old -> new" for each changed volume.
        /// </summary>
        internal static List<string> RefitRebuiltVolumes(AgentJob job)
        {
            var changes = new List<string>();
            if (job == null || !VolumeFollowsParts(job))
                return changes;
            foreach (AgentAsset asset in AllAssets(job))
            {
                if (!job.clearedAssetIds.Contains(asset.assetId) && asset.createdByJob != job.jobId)
                    continue;
                if (!TryFitVolume(asset, out Vector3 fitted) || (fitted - asset.volumeSize).sqrMagnitude < 1e-8f)
                    continue;
                changes.Add($"{asset.label}: {asset.volumeSize:0.###} -> {fitted:0.###}");
                asset.volumeSize = fitted;
            }
            return changes;
        }

        /// <summary>Volume that just encloses the asset's parts: [2·max|x|, max y, 2·max|z|] in the asset frame.</summary>
        internal static bool TryFitVolume(AgentAsset asset, out Vector3 volume)
        {
            volume = Vector3.zero;
            bool any = false;
            Matrix4x4 toAsset = asset.transform.worldToLocalMatrix;
            foreach (AgentPart part in PartsOf(asset))
            {
                // Explicit null tests: Unity's fake-null components defeat ?. (see GenerationRunner history).
                MeshFilter filter = part.GetComponent<MeshFilter>();
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null) continue;
                Matrix4x4 toLocal = toAsset * part.transform.localToWorldMatrix;
                foreach (Vector3 v in mesh.vertices)
                {
                    Vector3 p = toLocal.MultiplyPoint3x4(v);
                    volume = new Vector3(Mathf.Max(volume.x, 2f * Mathf.Abs(p.x)), Mathf.Max(volume.y, p.y),
                        Mathf.Max(volume.z, 2f * Mathf.Abs(p.z)));
                    any = true;
                }
            }
            if (any)
                volume = ClampSize(volume);
            return any;
        }

        private const string ClearFirstMessage =
            "In this condition an edit regenerates the whole object: call spatialgen_clear_asset on it first, " +
            "then add all of its parts again with the change applied.";

        /// <summary><see cref="InScope"/> as an error, locking the asset for a condition A edit.</summary>
        private static bool TryClaimAsset(AgentJob job, AgentAsset asset, out OpResult error)
        {
            error = null;
            if (!InScope(job, asset))
            {
                error = OpResult.Fail(job.mode == AgentJobMode.Generate
                    ? "This asset is not part of the current generation run."
                    : job.HasPartScope
                        ? "This object is outside the selection."
                        : $"This edit already changes asset '{job.lockedAssetId}'. One edit may change only one asset.");
                return false;
            }

            if (job.mode == AgentJobMode.Edit && !job.hasRegion && string.IsNullOrEmpty(job.lockedAssetId))
                job.lockedAssetId = asset.assetId;
            return true;
        }

        private static bool IsPartEditable(AgentJob job, AgentAsset asset, AgentPart part)
        {
            if (!InScope(job, asset))
                return false;
            if (job.mode == AgentJobMode.Edit && job.HasPartScope)
                return job.addedPartIds.Contains(part.partId) || job.selection.wholeParts.Contains(part.partId) ||
                       job.selection.partialParts.Contains(part.partId);
            if (job.mode == AgentJobMode.Edit && job.hasRegion)
                return OverlapWithRegion(job, part.transform) >= EditableOverlap;
            return !MustClearFirst(job, asset);
        }

        private static bool AssetInSelection(AgentJob job, AgentAsset asset) =>
            asset.assetId == job.selection.assetId ||
            (string.IsNullOrEmpty(job.selection.assetId) && asset.createdByJob == job.jobId);

        /// <summary>
        /// Hard rule for region edits (geometry must stay inside the region); soft rule for
        /// generation (a warning when a part leaves the asset volume, so the agent can correct it).
        /// </summary>
        private static bool CheckPlacement(
            AgentJob job, AgentAsset asset, Vector3 center, Quaternion rotation, Vector3 size,
            out string problem, out string warning, string partId = null)
        {
            problem = null;
            warning = null;

            if (job.mode == AgentJobMode.Edit && job.hasRegion)
            {
                if (job.HasPartScope)
                {
                    if (job.selection.pickedWholeParts)
                    {
                        if (partId != null && job.selection.wholeParts.Contains(partId)) return true;
                        // Direct picks may grow an attachment beyond the fitted selection box.
                        var toAttachment = Matrix4x4.TRS(center, rotation, Vector3.one).inverse *
                                           asset.transform.worldToLocalMatrix;
                        foreach (AgentPart anchor in PartsOf(asset))
                        {
                            if (!job.selection.wholeParts.Contains(anchor.partId)) continue;
                            if (PartGeometry.Cut(anchor, toAttachment * anchor.transform.localToWorldMatrix,
                                    size * 0.5f + Vector3.one * 0.0005f).coverage > 0) return true;
                        }
                    }
                    RegionSelection limit = partId != null && job.selection.partialParts.Contains(partId)
                        ? job.selection.Crop() : job.selection;
                    Matrix4x4 toRegion = limit.WorldToLocal() * asset.transform.localToWorldMatrix *
                                      Matrix4x4.TRS(center, rotation, size);
                    Bounds allowed = new(Vector3.zero, limit.size + Vector3.one * 0.0002f);
                    foreach (Vector3 corner in UnitCorners)
                    {
                        if (allowed.Contains(toRegion.MultiplyPoint3x4(corner))) continue;
                        problem = "the new geometry would extend outside the selected region";
                        return false;
                    }
                    return true;
                }
                if (!CornersInsideRegion(job, asset.transform.localToWorldMatrix, center, rotation, size))
                {
                    problem = "the part would extend outside the selected edit region";
                    return false;
                }

                return true;
            }

            // A condition A edit rebuilds the object freely; its volume is refitted afterwards
            // (RefitRebuiltVolumes), so it is no constraint to warn about.
            if (VolumeFollowsParts(job))
                return true;

            Vector3 half = asset.volumeSize * 0.5f;
            Vector3 tolerance = asset.volumeSize * 0.1f + Vector3.one * 0.02f;
            Matrix4x4 local = Matrix4x4.TRS(center, rotation, size);
            foreach (Vector3 corner in UnitCorners)
            {
                Vector3 p = local.MultiplyPoint3x4(corner);
                if (Mathf.Abs(p.x) > half.x + tolerance.x ||
                    Mathf.Abs(p.z) > half.z + tolerance.z ||
                    p.y < -tolerance.y ||
                    p.y > asset.volumeSize.y + tolerance.y)
                {
                    warning = $"extends outside the asset volume (x ±{half.x:0.##}, y 0–{asset.volumeSize.y:0.##}, z ±{half.z:0.##}); move or shrink it";
                    break;
                }
            }

            return true;
        }

        private static bool CornersInsideRegion(AgentJob job, Matrix4x4 frame, Vector3 center, Quaternion rotation, Vector3 size)
        {
            Matrix4x4 toWorld = frame * Matrix4x4.TRS(center, rotation, size);
            foreach (Vector3 corner in UnitCorners)
            {
                if (!InsideRegion(job, toWorld.MultiplyPoint3x4(corner), RegionTolerance(job)))
                    return false;
            }

            return true;
        }

        /// <summary>Fraction of the part's box inside the region, from a 4×4×4 sample grid.</summary>
        private static float OverlapWithRegion(AgentJob job, Transform part)
        {
            int inside = 0, total = 0;
            for (int x = 0; x < 4; x++)
            for (int y = 0; y < 4; y++)
            for (int z = 0; z < 4; z++)
            {
                Vector3 local = new((x + 0.5f) / 4f - 0.5f, (y + 0.5f) / 4f - 0.5f, (z + 0.5f) / 4f - 0.5f);
                if (InsideRegion(job, part.TransformPoint(local), 0f))
                    inside++;
                total++;
            }

            return inside / (float)total;
        }

        private static bool InsideRegion(AgentJob job, Vector3 world, float tolerance)
        {
            Vector3 local = Quaternion.Inverse(job.regionRotation) * (world - job.regionCenter);
            Vector3 half = job.regionSize * 0.5f;
            return Mathf.Abs(local.x) <= half.x + tolerance &&
                   Mathf.Abs(local.y) <= half.y + tolerance &&
                   Mathf.Abs(local.z) <= half.z + tolerance;
        }

        private static float RegionTolerance(AgentJob job)
        {
            float largest = Mathf.Max(job.regionSize.x, Mathf.Max(job.regionSize.y, job.regionSize.z));
            return Mathf.Max(0.02f, largest * 0.05f);
        }

        private static readonly Vector3[] UnitCorners =
        {
            new(-0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, -0.5f), new(-0.5f, 0.5f, -0.5f), new(0.5f, 0.5f, -0.5f),
            new(-0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, 0.5f), new(-0.5f, 0.5f, 0.5f), new(0.5f, 0.5f, 0.5f)
        };
    }
}
