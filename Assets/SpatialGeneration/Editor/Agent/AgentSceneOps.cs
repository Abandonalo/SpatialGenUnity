using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SpatialGeneration.Agent
{
    /// <summary>A part as the agent describes it. Null fields mean "leave unchanged" on update.</summary>
    public sealed class PartSpec
    {
        public string id;
        public string name;
        public string shape;
        public float[] center;
        public float[] size;
        public float[] rotation;
        public string color;
        public int? segments;
    }

    public sealed class OpResult
    {
        public bool ok;
        public string message;
        public object data;

        public static OpResult Ok(string message, object data = null) => new() { ok = true, message = message, data = data };
        public static OpResult Fail(string message, object data = null) => new() { ok = false, message = message, data = data };
    }

    /// <summary>
    /// Everything the MCP agent may do to the scene, with the rules of the current job enforced
    /// here rather than trusted to the prompt:
    /// <list type="bullet">
    /// <item>Generation, condition B: only the assets prepared from the proxies.</item>
    /// <item>Generation, condition A: assets the agent creates in this run.</item>
    /// <item>Edit, condition B: only parts mostly inside the selected region, and new geometry
    /// must stay inside it. Content outside the region is unchanged by construction.</item>
    /// <item>Edit, condition A: one asset, rebuilt as a whole, matching whole-object regeneration.</item>
    /// </list>
    /// Kept free of MCP types so the editor assembly compiles without the MCP package; the thin
    /// tool wrappers live in SpatialGeneration.Editor.Mcp.
    /// </summary>
    public static class AgentSceneOps
    {
        private const float MinPartSize = 0.005f;
        private const float MaxPartSize = 200f;
        private const float EditableOverlap = 0.5f;
        private static readonly Color DefaultColor = new(0.8f, 0.8f, 0.8f);

        private static BackendSettings Settings => BackendRegistry.Settings;

        // ------------------------------------------------------------------ queries

        public static OpResult GetTask()
        {
            if (!TryJob(out AgentJob job, out OpResult error))
                return error;

            Count(job, "spatialgen_get_task", true, string.Empty);
            return OpResult.Ok("Current Spatial Generation task.", new Dictionary<string, object>
            {
                ["job_id"] = job.jobId,
                ["mode"] = job.mode == AgentJobMode.Generate ? "generate" : "edit",
                ["condition"] = job.condition == StudyCondition.TextOnly ? "A_text_only" : "B_spatial_controls",
                ["request"] = job.request ?? string.Empty,
                ["style_prompt"] = job.stylePrompt ?? string.Empty,
                ["region"] = job.hasRegion ? RegionData(job) : null,
                ["max_parts_per_asset"] = Settings.maxPartsPerAsset,
                ["conventions"] = AgentPromptBuilder.FrameConventions,
                ["assets"] = AssetList(job)
            });
        }

        public static OpResult ListAssets()
        {
            if (!TryJob(out AgentJob job, out OpResult error))
                return error;

            Count(job, "spatialgen_list_assets", true, string.Empty);
            return OpResult.Ok("Assets in the scene.", AssetList(job));
        }

        public static OpResult ListParts(string assetId)
        {
            if (!TryJob(out AgentJob job, out OpResult error))
                return error;
            if (!TryFindAsset(job, assetId, out AgentAsset asset, out error))
                return Reject(job, "spatialgen_list_parts", error);

            var parts = new List<object>();
            foreach (AgentPart part in PartsOf(asset))
                parts.Add(PartData(part, IsPartEditable(job, asset, part), PartialPartEdit.Prepare(job, part)));

            Count(job, "spatialgen_list_parts", true, $"asset={assetId}");
            return OpResult.Ok($"{parts.Count} part(s) in {asset.label}.", new Dictionary<string, object>
            {
                ["asset"] = AssetData(asset, job),
                ["parts"] = parts
            });
        }

        // ------------------------------------------------------------------ assets

        public static OpResult CreateAsset(string label, string prompt, float[] origin, float yawDegrees, float[] volumeSize)
        {
            const string tool = "spatialgen_create_asset";
            if (!TryJob(out AgentJob job, out OpResult error))
                return error;

            bool allowed = job.condition == StudyCondition.TextOnly || job.mode == AgentJobMode.Edit;
            if (!allowed)
                return Reject(job, tool, OpResult.Fail(
                    "In this condition every asset is prepared from a spatial proxy; fill those instead of creating new ones."));

            if (!TryVector(origin, out Vector3 originWorld) || !TryVector(volumeSize, out Vector3 size))
                return Reject(job, tool, OpResult.Fail("origin and volume_size must be arrays of three numbers."));

            size = ClampSize(size);
            Quaternion rotation = Quaternion.Euler(0f, yawDegrees, 0f);

            if (job.mode == AgentJobMode.Edit)
            {
                if (job.HasPartScope && !string.IsNullOrEmpty(job.selection.assetId))
                    return Reject(job, tool, OpResult.Fail("Add parts to the selected object."));
                if (job.condition == StudyCondition.TextOnly && !string.IsNullOrEmpty(job.lockedAssetId))
                    return Reject(job, tool, OpResult.Fail(
                        "This edit already changes another asset; one edit may change only one asset."));

                if (job.hasRegion)
                {
                    Matrix4x4 frame = Matrix4x4.TRS(originWorld, rotation, Vector3.one);
                    Vector3 localCenter = new(0f, size.y * 0.5f, 0f);
                    if (!CornersInsideRegion(job, frame, localCenter, Quaternion.identity, size))
                        return Reject(job, tool, OpResult.Fail(
                            "A new asset must fit inside the selected edit region.", RegionData(job)));
                }
            }

            GameObject root = string.IsNullOrEmpty(job.previewToken) ? EnsureGeneratedRoot() : RefinementPreviewStage.ForJob(job);
            string assetId = ShortId();
            string cleanLabel = string.IsNullOrWhiteSpace(label) ? "asset" : label.Trim();

            var go = new GameObject($"Generated_Mesh_{Sanitize(cleanLabel)}_{assetId}");
            if (go.scene != root.scene) SceneManager.MoveGameObjectToScene(go, root.scene);
            go.transform.SetParent(root.transform, false);
            go.transform.SetPositionAndRotation(originWorld, rotation);

            AgentAsset asset = go.AddComponent<AgentAsset>();
            asset.assetId = assetId;
            asset.label = cleanLabel;
            asset.prompt = prompt ?? string.Empty;
            asset.volumeSize = size;
            asset.createdByJob = job.jobId;
            AgentObjectUndo.Created(go, "Agent: create asset");

            if (job.mode == AgentJobMode.Edit && job.condition == StudyCondition.TextOnly)
                job.lockedAssetId = assetId;
            job.assetsCreated++;

            MarkDirty(go);
            Count(job, tool, true, $"asset={assetId}; label={cleanLabel}");
            return OpResult.Ok($"Created asset '{cleanLabel}'. Add its parts with spatialgen_add_parts.",
                AssetData(asset, job));
        }

        /// <summary>Removes every part of an asset so it can be rebuilt (condition A edits, or a fresh start in generation).</summary>
        public static OpResult ClearAsset(string assetId)
        {
            const string tool = "spatialgen_clear_asset";
            if (!TryJob(out AgentJob job, out OpResult error))
                return error;
            if (!TryFindAsset(job, assetId, out AgentAsset asset, out error))
                return Reject(job, tool, error);
            if (job.mode == AgentJobMode.Edit && job.hasRegion)
                return Reject(job, tool, OpResult.Fail(
                    "Region edits cannot clear a whole asset. Remove only the editable parts inside the region."));
            if (!TryClaimAsset(job, asset, out error))
                return Reject(job, tool, error);

            int removed = 0;
            foreach (AgentPart part in PartsOf(asset))
            {
                AgentObjectUndo.Destroy(part.gameObject);
                removed++;
            }

            job.partsRemoved += removed;
            MarkDirty(asset.gameObject);
            Count(job, tool, true, $"asset={assetId}; removed={removed}");
            return OpResult.Ok($"Removed {removed} part(s) from '{asset.label}'.");
        }

        // ------------------------------------------------------------------ parts

        public static OpResult AddParts(string assetId, List<PartSpec> specs)
        {
            const string tool = "spatialgen_add_parts";
            if (!TryJob(out AgentJob job, out OpResult error))
                return error;
            if (!TryFindAsset(job, assetId, out AgentAsset asset, out error))
                return Reject(job, tool, error);
            if (specs == null || specs.Count == 0)
                return Reject(job, tool, OpResult.Fail("parts must be a non-empty array."));
            if (!TryClaimAsset(job, asset, out error))
                return Reject(job, tool, error);

            int existing = PartsOf(asset).Count;
            var created = new List<object>();
            var rejected = new List<object>();
            var warnings = new List<string>();

            for (int i = 0; i < specs.Count; i++)
            {
                PartSpec spec = specs[i];
                if (existing + created.Count >= Settings.maxPartsPerAsset)
                {
                    rejected.Add(Rejection(i, spec, $"asset already has the maximum of {Settings.maxPartsPerAsset} parts"));
                    continue;
                }

                if (!TryParseShape(spec.shape, out AgentPartShape shape))
                {
                    rejected.Add(Rejection(i, spec, $"unknown shape '{spec.shape}'"));
                    continue;
                }

                if (!TryVector(spec.center, out Vector3 center) || !TryVector(spec.size, out Vector3 size))
                {
                    rejected.Add(Rejection(i, spec, "center and size must be arrays of three numbers"));
                    continue;
                }

                size = ClampSize(size);
                Quaternion rotation = TryVector(spec.rotation, out Vector3 euler) ? Quaternion.Euler(euler) : Quaternion.identity;

                if (!CheckPlacement(job, asset, center, rotation, size, out string problem, out string warning))
                {
                    rejected.Add(Rejection(i, spec, problem));
                    continue;
                }

                if (!string.IsNullOrEmpty(warning))
                    warnings.Add($"part {i}: {warning}");

                AgentPart part = CreatePart(asset, job, spec, shape, center, rotation, size);
                if (job.HasPartScope) job.addedPartIds.Add(part.partId);
                created.Add(PartData(part, true));
            }

            job.partsAdded += created.Count;
            if (created.Count > 0)
                MarkDirty(asset.gameObject);

            bool ok = created.Count > 0;
            Count(job, tool, ok, $"asset={assetId}; added={created.Count}; rejected={rejected.Count}");
            string message = $"Added {created.Count} part(s) to '{asset.label}'" +
                             (rejected.Count > 0 ? $"; rejected {rejected.Count}." : ".");
            var data = new Dictionary<string, object>
            {
                ["added"] = created,
                ["rejected"] = rejected,
                ["warnings"] = warnings,
                ["part_count"] = existing + created.Count
            };
            return ok ? OpResult.Ok(message, data) : OpResult.Fail(message, data);
        }

        public static OpResult UpdateParts(string assetId, List<PartSpec> specs)
        {
            const string tool = "spatialgen_update_parts";
            if (!TryJob(out AgentJob job, out OpResult error))
                return error;
            if (!TryFindAsset(job, assetId, out AgentAsset asset, out error))
                return Reject(job, tool, error);
            if (specs == null || specs.Count == 0)
                return Reject(job, tool, OpResult.Fail("parts must be a non-empty array of objects with an id."));
            if (!TryClaimAsset(job, asset, out error))
                return Reject(job, tool, error);

            Dictionary<string, AgentPart> byId = PartIndex(asset);
            var updated = new List<object>();
            var rejected = new List<object>();
            var warnings = new List<string>();

            for (int i = 0; i < specs.Count; i++)
            {
                PartSpec spec = specs[i];
                if (spec.id == null || !byId.TryGetValue(spec.id, out AgentPart part))
                {
                    rejected.Add(Rejection(i, spec, $"no part with id '{spec.id}' in this asset"));
                    continue;
                }

                if (!IsPartEditable(job, asset, part))
                {
                    rejected.Add(Rejection(i, spec, "this part is outside the edit region and must stay unchanged"));
                    continue;
                }

                AgentPartShape shape = part.shape;
                if (spec.shape != null && !TryParseShape(spec.shape, out shape))
                {
                    rejected.Add(Rejection(i, spec, $"unknown shape '{spec.shape}'"));
                    continue;
                }

                Transform t = part.transform;
                PartialPartEdit slice = PartialPartEdit.Prepare(job, part);
                if (slice != null && !slice.HasInside)
                {
                    rejected.Add(Rejection(i, spec, "the part has no geometry inside the box"));
                    continue;
                }
                Vector3 originalCenter = slice?.Center ?? t.localPosition;
                Vector3 originalSize = slice?.Size ?? t.localScale;
                Vector3 center = TryVector(spec.center, out Vector3 c) ? c : originalCenter;
                Vector3 size = TryVector(spec.size, out Vector3 s) ? ClampSize(s) : originalSize;
                Quaternion rotation = TryVector(spec.rotation, out Vector3 euler) ? Quaternion.Euler(euler) : t.localRotation;

                bool rebuildShape = shape != part.shape ||
                    (spec.segments.HasValue && LowPolyMeshes.ClampSegments(spec.segments.Value) != part.segments);
                bool geometryChanged = center != originalCenter || size != originalSize ||
                    Quaternion.Angle(rotation, t.localRotation) > 0.001f || rebuildShape;
                string problem = null;
                string warning = null;
                if (geometryChanged && !CheckPlacement(job, asset, center, rotation, size, out problem, out warning, part.partId))
                {
                    rejected.Add(Rejection(i, spec, problem));
                    continue;
                }

                if (!string.IsNullOrEmpty(warning))
                    warnings.Add($"part {spec.id}: {warning}");

                // Creating the remainder flushes pending RecordObject diffs. Capture the original
                // immediately so one Undo restores its geometry, transform and colour together.
                AgentObjectUndo.Record(part, "Agent: update part");
                if (slice != null)
                {
                    slice.IsolateInside(part);
                    job.materializedSlices.Add(part.partId);
                }
                if (rebuildShape) part.fragments.Clear();
                t.localPosition = center;
                t.localRotation = rotation;
                t.localScale = size;
                part.shape = shape;
                if (spec.segments.HasValue)
                    part.segments = LowPolyMeshes.ClampSegments(spec.segments.Value);
                if (spec.color != null)
                    part.color = ParseColor(spec.color, part.color);
                if (!string.IsNullOrWhiteSpace(spec.name))
                    part.gameObject.name = PartName(spec.name, part.partId);
                part.lastJobId = job.jobId;
                part.Rebuild();
                updated.Add(PartData(part, true));
            }

            job.partsUpdated += updated.Count;
            if (updated.Count > 0)
                MarkDirty(asset.gameObject);

            bool ok = updated.Count > 0;
            Count(job, tool, ok, $"asset={assetId}; updated={updated.Count}; rejected={rejected.Count}");
            string message = $"Updated {updated.Count} part(s)" + (rejected.Count > 0 ? $"; rejected {rejected.Count}." : ".");
            var data = new Dictionary<string, object> { ["updated"] = updated, ["rejected"] = rejected, ["warnings"] = warnings };
            return ok ? OpResult.Ok(message, data) : OpResult.Fail(message, data);
        }

        public static OpResult RemoveParts(string assetId, List<string> partIds)
        {
            const string tool = "spatialgen_remove_parts";
            if (!TryJob(out AgentJob job, out OpResult error))
                return error;
            if (!TryFindAsset(job, assetId, out AgentAsset asset, out error))
                return Reject(job, tool, error);
            if (partIds == null || partIds.Count == 0)
                return Reject(job, tool, OpResult.Fail("part_ids must be a non-empty array of part ids."));
            if (!TryClaimAsset(job, asset, out error))
                return Reject(job, tool, error);

            Dictionary<string, AgentPart> byId = PartIndex(asset);
            var removed = new List<string>();
            var rejected = new List<object>();

            foreach (string id in partIds)
            {
                if (id == null || !byId.TryGetValue(id, out AgentPart part))
                {
                    rejected.Add(new Dictionary<string, object> { ["id"] = id, ["reason"] = "no such part in this asset" });
                    continue;
                }

                if (!IsPartEditable(job, asset, part))
                {
                    rejected.Add(new Dictionary<string, object> { ["id"] = id, ["reason"] = "outside the edit region" });
                    continue;
                }

                PartialPartEdit slice = PartialPartEdit.Prepare(job, part);
                if (slice != null)
                {
                    if (!slice.HasInside)
                    {
                        rejected.Add(new Dictionary<string, object> { ["id"] = id, ["reason"] = "empty intersection" });
                        continue;
                    }
                    slice.RetainOutside(part);
                }
                else AgentObjectUndo.Destroy(part.gameObject);
                byId.Remove(id);
                removed.Add(id);
            }

            job.partsRemoved += removed.Count;
            if (removed.Count > 0)
                MarkDirty(asset.gameObject);

            bool ok = removed.Count > 0;
            Count(job, tool, ok, $"asset={assetId}; removed={removed.Count}; rejected={rejected.Count}");
            var data = new Dictionary<string, object> { ["removed"] = removed, ["rejected"] = rejected };
            string message = $"Removed {removed.Count} part(s)" + (rejected.Count > 0 ? $"; rejected {rejected.Count}." : ".");
            return ok ? OpResult.Ok(message, data) : OpResult.Fail(message, data);
        }

        public static OpResult Finish(string summary)
        {
            if (!TryJob(out AgentJob job, out OpResult error))
                return error;

            job.finishedByAgent = true;
            job.summary = summary ?? string.Empty;
            Count(job, "spatialgen_finish", true, $"summary={job.summary}");
            return OpResult.Ok("Recorded. The task is complete; stop now.");
        }

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

        /// <summary>Coarse, asset-level scope check; part-level region checks come on top.</summary>
        private static bool TryClaimAsset(AgentJob job, AgentAsset asset, out OpResult error)
        {
            error = null;
            if (job.mode == AgentJobMode.Generate)
            {
                bool inScope = job.condition == StudyCondition.SpatialControls
                    ? job.assetIds.Contains(asset.assetId)
                    : asset.createdByJob == job.jobId;
                if (!inScope)
                    error = OpResult.Fail("This asset is not part of the current generation run.");
                return error == null;
            }

            if (job.HasPartScope)
            {
                if (!AssetInSelection(job, asset)) error = OpResult.Fail("This object is outside the selection.");
                return error == null;
            }
            if (job.hasRegion)
                return true;

            if (string.IsNullOrEmpty(job.lockedAssetId))
            {
                job.lockedAssetId = asset.assetId;
                return true;
            }

            if (job.lockedAssetId != asset.assetId)
                error = OpResult.Fail(
                    $"This edit already changes asset '{job.lockedAssetId}'. One edit may change only one asset.");
            return error == null;
        }

        private static bool IsPartEditable(AgentJob job, AgentAsset asset, AgentPart part)
        {
            if (job.mode == AgentJobMode.Generate)
            {
                return job.condition == StudyCondition.SpatialControls
                    ? job.assetIds.Contains(asset.assetId)
                    : asset.createdByJob == job.jobId;
            }

            if (job.HasPartScope)
                return AssetInSelection(job, asset) && (job.addedPartIds.Contains(part.partId) ||
                    job.selection.wholeParts.Contains(part.partId) || job.selection.partialParts.Contains(part.partId));
            if (job.hasRegion)
                return OverlapWithRegion(job, part.transform) >= EditableOverlap;

            return string.IsNullOrEmpty(job.lockedAssetId) || job.lockedAssetId == asset.assetId;
        }

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

        // ------------------------------------------------------------------ scene helpers

        public static GameObject EnsureGeneratedRoot() => GenerationRunner.EnsureGeneratedRoot();

        public static List<AgentAsset> AllAssets(AgentJob job = null)
        {
            var assets = new List<AgentAsset>();
            GameObject root = string.IsNullOrEmpty(job?.previewToken)
                ? GameObject.Find(GenerationRunner.GeneratedRootName) : RefinementPreviewStage.ForJob(job);
            if (root != null)
                assets.AddRange(root.GetComponentsInChildren<AgentAsset>(false));
            return assets;
        }

        public static List<AgentPart> PartsOf(AgentAsset asset)
        {
            var parts = new List<AgentPart>();
            if (asset == null)
                return parts;

            foreach (Transform child in asset.transform)
            {
                AgentPart part = child.GetComponent<AgentPart>();
                if (part != null)
                    parts.Add(part);
            }

            return parts;
        }

        private static Dictionary<string, AgentPart> PartIndex(AgentAsset asset)
        {
            var index = new Dictionary<string, AgentPart>(StringComparer.Ordinal);
            foreach (AgentPart part in PartsOf(asset))
            {
                if (!string.IsNullOrEmpty(part.partId))
                    index[part.partId] = part;
            }

            return index;
        }

        private static bool TryFindAsset(AgentJob job, string assetId, out AgentAsset asset, out OpResult error)
        {
            asset = null;
            error = null;
            if (string.IsNullOrWhiteSpace(assetId))
            {
                error = OpResult.Fail("asset_id is required. Use spatialgen_list_assets to see the ids.");
                return false;
            }

            foreach (AgentAsset candidate in AllAssets(job))
            {
                if (candidate.assetId == assetId.Trim())
                {
                    asset = candidate;
                    return true;
                }
            }

            error = OpResult.Fail($"No asset with id '{assetId}'. Use spatialgen_list_assets to see the ids.");
            return false;
        }

        private static AgentPart CreatePart(
            AgentAsset asset, AgentJob job, PartSpec spec, AgentPartShape shape,
            Vector3 center, Quaternion rotation, Vector3 size)
        {
            string partId = ShortId();
            var go = new GameObject(PartName(spec.name, partId), typeof(MeshFilter), typeof(MeshRenderer));
            if (go.scene != asset.gameObject.scene) SceneManager.MoveGameObjectToScene(go, asset.gameObject.scene);
            go.transform.SetParent(asset.transform, false);
            go.transform.localPosition = center;
            go.transform.localRotation = rotation;
            go.transform.localScale = size;

            AgentPart part = go.AddComponent<AgentPart>();
            part.partId = partId;
            part.shape = shape;
            part.segments = LowPolyMeshes.ClampSegments(spec.segments ?? 8);
            part.color = ParseColor(spec.color, DefaultColor);
            part.lastJobId = job.jobId;
            part.Rebuild();

            AgentObjectUndo.Created(go, "Agent: add part");
            return part;
        }

        private static string PartName(string requested, string partId)
        {
            string name = string.IsNullOrWhiteSpace(requested) ? "part" : Sanitize(requested.Trim());
            return $"{name}_{partId}";
        }

        private static void MarkDirty(GameObject go)
        {
            if (go != null && EditorSceneManager.IsPreviewSceneObject(go)) return;
            if (go != null && go.scene.IsValid())
                EditorSceneManager.MarkSceneDirty(go.scene);
            SceneView.RepaintAll();
        }

        // ------------------------------------------------------------------ data shaping

        private static List<object> AssetList(AgentJob job)
        {
            var list = new List<object>();
            foreach (AgentAsset asset in AllAssets(job))
                list.Add(AssetData(asset, job));
            return list;
        }

        private static bool AssetInSelection(AgentJob job, AgentAsset asset) =>
            asset.assetId == job.selection.assetId ||
            (string.IsNullOrEmpty(job.selection.assetId) && asset.createdByJob == job.jobId);

        public static Dictionary<string, object> AssetData(AgentAsset asset, AgentJob job)
        {
            Transform t = asset.transform;
            bool inScope = job.mode == AgentJobMode.Generate
                ? (job.condition == StudyCondition.SpatialControls ? job.assetIds.Contains(asset.assetId) : asset.createdByJob == job.jobId)
                : job.HasPartScope ? AssetInSelection(job, asset)
                : job.hasRegion || string.IsNullOrEmpty(job.lockedAssetId) || job.lockedAssetId == asset.assetId;

            return new Dictionary<string, object>
            {
                ["asset_id"] = asset.assetId,
                ["label"] = asset.label,
                ["prompt"] = asset.prompt,
                ["proxy_id"] = asset.proxyId,
                ["origin_world"] = Round(t.position),
                ["yaw_degrees"] = Round(t.eulerAngles.y),
                ["front_direction_world"] = Round(t.right),
                ["volume_size"] = Round(asset.volumeSize),
                ["part_count"] = PartsOf(asset).Count,
                ["in_scope"] = inScope
            };
        }

        private static Dictionary<string, object> PartData(AgentPart part, bool editable, PartialPartEdit slice = null)
        {
            Transform t = part.transform;
            return new Dictionary<string, object>
            {
                ["id"] = part.partId,
                ["name"] = t.name,
                ["shape"] = part.shape.ToString().ToLowerInvariant(),
                ["center"] = Round(slice?.Center ?? t.localPosition),
                ["size"] = Round(slice?.Size ?? t.localScale),
                ["rotation"] = Round(SignedEuler(t.localEulerAngles)),
                ["color"] = "#" + ColorUtility.ToHtmlStringRGB(part.color),
                ["segments"] = part.segments,
                ["editable"] = editable && (slice == null || slice.HasInside),
                ["partial"] = slice != null
            };
        }

        public static Dictionary<string, object> RegionData(AgentJob job) => new()
        {
            ["center_world"] = Round(job.regionCenter),
            ["size"] = Round(job.regionSize),
            ["rotation_euler"] = Round(SignedEuler(job.regionRotation.eulerAngles)),
            ["selected_parts"] = job.HasPartScope ? job.selection : null
        };

        private static Dictionary<string, object> Rejection(int index, PartSpec spec, string reason) => new()
        {
            ["index"] = index,
            ["id"] = spec?.id,
            ["name"] = spec?.name,
            ["reason"] = reason
        };

        private static OpResult Reject(AgentJob job, string tool, OpResult error)
        {
            Count(job, tool, false, error.message);
            return error;
        }

        private static void Count(AgentJob job, string tool, bool ok, string detail)
        {
            job.toolCalls++;
            if (!ok)
                job.rejectedCalls++;
            AgentJobState.instance.Persist();

            InteractionLogger.Log(new InteractionEvent
            {
                type = "agent_tool",
                extra = $"job={job.jobId}; tool={tool}; ok={ok}; {detail}"
            });
        }

        // ------------------------------------------------------------------ parsing

        public static bool TryParseShape(string value, out AgentPartShape shape)
        {
            shape = AgentPartShape.Box;
            if (string.IsNullOrWhiteSpace(value))
                return true;

            switch (value.Trim().ToLowerInvariant())
            {
                case "cube":
                case "box":
                    shape = AgentPartShape.Box;
                    return true;
                case "prism":
                case "roof":
                case "gable":
                case "wedge":
                    shape = AgentPartShape.Wedge;
                    return true;
                case "ball":
                case "sphere":
                    shape = AgentPartShape.Sphere;
                    return true;
                default:
                    return Enum.TryParse(value.Trim(), true, out shape);
            }
        }

        private static bool TryVector(float[] values, out Vector3 vector)
        {
            vector = Vector3.zero;
            if (values == null || values.Length != 3)
                return false;
            foreach (float v in values)
            {
                if (float.IsNaN(v) || float.IsInfinity(v))
                    return false;
            }

            vector = new Vector3(values[0], values[1], values[2]);
            return true;
        }

        private static Vector3 ClampSize(Vector3 size) => new(
            Mathf.Clamp(Mathf.Abs(size.x), MinPartSize, MaxPartSize),
            Mathf.Clamp(Mathf.Abs(size.y), MinPartSize, MaxPartSize),
            Mathf.Clamp(Mathf.Abs(size.z), MinPartSize, MaxPartSize));

        private static Color ParseColor(string value, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;
            string trimmed = value.Trim();
            if (ColorUtility.TryParseHtmlString(trimmed, out Color color))
                return color;
            if (!trimmed.StartsWith("#") && ColorUtility.TryParseHtmlString("#" + trimmed, out color))
                return color;
            return fallback;
        }

        private static Vector3 SignedEuler(Vector3 euler) => new(Signed(euler.x), Signed(euler.y), Signed(euler.z));

        private static float Signed(float angle)
        {
            angle %= 360f;
            if (angle > 180f) angle -= 360f;
            if (angle <= -180f) angle += 360f;
            return angle;
        }

        private static float[] Round(Vector3 v) => new[] { Round(v.x), Round(v.y), Round(v.z) };

        private static float Round(float v) => (float)Math.Round(v, 3);

        public static string ShortId() => Guid.NewGuid().ToString("N").Substring(0, 8);

        public static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "item";
            char[] chars = value.Trim().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_' && chars[i] != '-')
                    chars[i] = '_';
            }

            string sanitized = new string(chars).Trim('_');
            if (sanitized.Length > 32)
                sanitized = sanitized.Substring(0, 32);
            return sanitized.Length == 0 ? "item" : sanitized;
        }
    }
}
