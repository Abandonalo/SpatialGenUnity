using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SpatialGeneration.Authoring;

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
    /// Agent operations never enter the participant's Undo history: edits run on a private preview
    /// copy (applied later as one Undo step), and a generation run is a version the window manages,
    /// which Ctrl+Z must not take apart part by part.
    /// </summary>
    public static partial class AgentSceneOps
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
                UnityEngine.Object.DestroyImmediate(part.gameObject);
                removed++;
            }

            job.partsRemoved += removed;
            if (!job.clearedAssetIds.Contains(asset.assetId))
                job.clearedAssetIds.Add(asset.assetId);
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
            if (MustClearFirst(job, asset))
                return Reject(job, tool, OpResult.Fail(ClearFirstMessage));
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
            if (MustClearFirst(job, asset))
                return Reject(job, tool, OpResult.Fail(ClearFirstMessage));
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
            if (MustClearFirst(job, asset))
                return Reject(job, tool, OpResult.Fail(ClearFirstMessage));
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
                else UnityEngine.Object.DestroyImmediate(part.gameObject);
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

        // ------------------------------------------------------------------ scene helpers

        public static GameObject EnsureGeneratedRoot() => GeneratedContentRoot.Ensure(undoable: false);

        public static List<AgentAsset> AllAssets(AgentJob job = null)
        {
            var assets = new List<AgentAsset>();
            GameObject root = string.IsNullOrEmpty(job?.previewToken)
                ? GameObject.Find(GeneratedContentRoot.Name) : RefinementPreviewStage.ForJob(job);
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

    }
}
