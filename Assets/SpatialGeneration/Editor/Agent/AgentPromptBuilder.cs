using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using SpatialGeneration.Authoring;

namespace SpatialGeneration.Agent
{
    /// <summary>
    /// Writes the instruction the headless agent receives. Rules that matter for the study
    /// (what may change) are also enforced by <see cref="AgentSceneOps"/>; the prompt states
    /// them so the agent does not waste turns on rejected calls.
    /// </summary>
    public static class AgentPromptBuilder
    {
        public const string FrameConventions =
            "Every asset has its own frame. Units are metres. The origin is the centre of the asset's footprint " +
            "on the ground; +Y is up; +X is the asset's front (the side that faces the viewer, e.g. a door or a " +
            "stall counter); +Z is the asset's left side and -Z its right side. The volume is volume_size = " +
            "[depth along X, height along Y, width along Z], so the asset must fit x in [-depth/2, depth/2], " +
            "y in [0, height], z in [-width/2, width/2]. A part is a unit primitive centred at center, scaled to " +
            "size [x, y, z] and rotated by rotation (Euler degrees, Unity order Z then X then Y), all in the asset frame.";

        private const string Shapes =
            "box; wedge (triangular prism with its ridge along local X at the top and faces sloping down towards " +
            "±Z, e.g. a gable roof; rotate it 90° about Y for a ridge along Z); pyramid; cylinder (axis along Y); " +
            "cone (apex up); sphere. cylinder, cone and sphere accept segments (3–32, default 8).";

        [Serializable]
        private class TaskAsset
        {
            public string asset_id;
            public string label;
            public string prompt;
            public float[] origin_world;
            public float yaw_degrees;
            public float[] front_direction_world;
            public float[] volume_size;
            public int part_count;
        }

        [Serializable]
        private class TaskObject
        {
            public string name;
            public float[] bounds_center;
            public float[] bounds_size;
        }

        [Serializable]
        private class TaskDocument
        {
            public string job_id;
            public string mode;
            public string condition;
            public string request;
            public string style_prompt;
            public bool has_region;
            public float[] region_center_world;
            public float[] region_size;
            public float[] region_rotation_euler;
            public RegionSelection selected_parts;
            public int max_parts_per_asset;
            public TaskAsset[] assets;
            public TaskObject[] scene_objects;
        }

        public static string BuildTaskJson(AgentJob job, int maxPartsPerAsset)
        {
            var assets = new List<TaskAsset>();
            foreach (AgentAsset asset in AgentSceneOps.AllAssets(job))
            {
                Transform t = asset.transform;
                assets.Add(new TaskAsset
                {
                    asset_id = asset.assetId,
                    label = asset.label,
                    prompt = asset.prompt,
                    origin_world = Arr(t.position),
                    yaw_degrees = (float)Math.Round(t.eulerAngles.y, 2),
                    front_direction_world = Arr(t.right),
                    volume_size = Arr(asset.volumeSize),
                    part_count = AgentSceneOps.PartsOf(asset).Count
                });
            }

            var document = new TaskDocument
            {
                job_id = job.jobId,
                mode = job.mode == AgentJobMode.Generate ? "generate" : "edit",
                condition = job.condition == StudyCondition.TextOnly ? "A_text_only" : "B_spatial_controls",
                request = job.request ?? string.Empty,
                style_prompt = job.stylePrompt ?? string.Empty,
                has_region = job.hasRegion,
                selected_parts = job.HasPartScope ? job.selection : null,
                region_center_world = job.hasRegion ? Arr(job.regionCenter) : null,
                region_size = job.hasRegion ? Arr(job.regionSize) : null,
                region_rotation_euler = job.hasRegion ? Arr(job.regionRotation.eulerAngles) : null,
                max_parts_per_asset = maxPartsPerAsset,
                assets = assets.ToArray(),
                scene_objects = CollectSceneObjects().ToArray()
            };

            return JsonUtility.ToJson(document, true);
        }

        public static string BuildPrompt(AgentJob job, string taskJson)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are the 3D modelling backend of a Unity authoring tool used in a user study.");
            sb.AppendLine("You build and edit low-poly assets inside the open Unity scene, only through the MCP tools of the");
            sb.AppendLine("Unity MCP server whose names start with \"spatialgen_\". If they are not listed as tools, call them");
            sb.AppendLine("through execute_custom_tool with tool_name set to the tool's name and parameters as an object.");
            sb.AppendLine("If several Unity instances are connected, pick this project's instance with set_active_instance first.");
            sb.AppendLine("Do not use any other tool. Do not create, read or edit files or scripts. Nobody can answer questions,");
            sb.AppendLine("so never ask any; decide yourself. When the task is done, call spatialgen_finish with one sentence.");
            sb.AppendLine();
            sb.AppendLine("Tools: spatialgen_get_task, spatialgen_list_assets, spatialgen_list_parts(asset_id),");
            sb.AppendLine("spatialgen_create_asset(label, prompt, origin, yaw_degrees, volume_size), spatialgen_add_parts(asset_id, parts),");
            sb.AppendLine("spatialgen_update_parts(asset_id, parts with id), spatialgen_remove_parts(asset_id, part_ids),");
            sb.AppendLine("spatialgen_clear_asset(asset_id), spatialgen_finish(summary).");
            sb.AppendLine();
            sb.AppendLine("Coordinate frame: " + FrameConventions);
            sb.AppendLine("Shapes: " + Shapes);
            sb.AppendLine();
            sb.AppendLine("Modelling rules:");
            sb.AppendLine("- Model each object as a recognisable low-poly asset from 6 to 40 primitives with flat colours (hex, e.g. \"#8B5A2B\").");
            sb.AppendLine("- Use real-world proportions, keep parts touching where they connect, and rest the object on y = 0.");
            sb.AppendLine("- Fill the asset's volume the way the object naturally would; do not leave parts floating or poking out.");
            sb.AppendLine("- Follow the asset prompt and the shared style prompt. Send many parts per spatialgen_add_parts call.");
            sb.AppendLine("- Read every tool result: fix rejected parts and warnings before you finish.");
            sb.AppendLine();
            sb.AppendLine("Your task:");
            sb.AppendLine(TaskInstruction(job));
            if (job.ideaNumber > 1)
            {
                sb.AppendLine($"This is preview idea {job.ideaNumber}, starting from the original model. Explore another way to satisfy the same request and spatial constraints.");
                if (!string.IsNullOrEmpty(job.previousIdeas)) sb.AppendLine("Previously previewed ideas: " + job.previousIdeas);
            }
            sb.AppendLine();
            sb.AppendLine("Task data (also available from spatialgen_get_task):");
            sb.AppendLine(taskJson);
            return sb.ToString();
        }

        private static string TaskInstruction(AgentJob job)
        {
            if (job.mode == AgentJobMode.Generate && job.condition == StudyCondition.SpatialControls)
            {
                return "Build one asset for every entry in assets. Each already exists as an empty group whose position, " +
                       "orientation and volume the designer set with a spatial proxy; model the object its prompt describes " +
                       "inside that volume, facing +X. Do not create other assets.";
            }

            if (job.mode == AgentJobMode.Generate)
            {
                return "The designer described the whole scene in words only (request). Decide which objects it needs and " +
                       "where they go. Create each with spatialgen_create_asset (origin = footprint centre in world " +
                       "coordinates on the ground, yaw_degrees = rotation about +Y so that the asset's front +X faces the " +
                       "right way, volume_size = [depth, height, width] in metres), then build its parts. Respect every " +
                       "layout statement: relative positions, distances, orientations, contacts and paths that must stay clear. " +
                       "scene_objects lists what is already in the scene; the ground is y = 0 unless a ground object says otherwise.";
            }

            if (job.hasRegion)
            {
                if (job.HasPartScope)
                    return "Apply the user's request to the selected_parts of its asset. List the parts first. " +
                           "wholeParts are whole targets. For partialParts, listed centers and sizes describe only the " +
                           "clipped portion; updates and removals preserve the rest. Add requested geometry inside the box. " +
                           "With pickedWholeParts, whole targets can also be resized or receive a touching attachment. " +
                           "An empty selection of parts allows additions in that space. Use the request to decide the change.";
                return "Apply the designer's requested change (request) inside the selected edit region only. " +
                       "spatialgen_list_parts marks the parts you may change (editable = true); everything else must stay as it is, " +
                       "and new or moved parts must lie inside the region. Make exactly the requested change and nothing more. " +
                       "If the request adds a new object, you may create it with spatialgen_create_asset inside the region.";
            }

            return "The designer described a change in words only (request). Identify the one asset it refers to, list its " +
                   "parts, then rebuild that whole asset with the change applied: spatialgen_clear_asset, then " +
                   "spatialgen_add_parts. Adding to, updating or removing single parts of an existing object is rejected " +
                   "until you have cleared it. The rebuilt object may be larger or smaller than before: its volume is refitted " +
                   "to the parts you build. Keep whatever the request does not mention as close to before as you can. " +
                   "If the request asks for a new object, create it with spatialgen_create_asset instead. Change only one asset.";
        }

        private static List<TaskObject> CollectSceneObjects()
        {
            var objects = new List<TaskObject>();
            GameObject generatedRoot = GameObject.Find(GeneratedContentRoot.Name);

            foreach (Renderer renderer in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (objects.Count >= 40)
                    break;
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;
                if ((renderer.gameObject.hideFlags & HideFlags.HideInHierarchy) != 0)
                    continue;
                if (generatedRoot != null && renderer.transform.IsChildOf(generatedRoot.transform))
                    continue;
                if (renderer.GetComponentInParent<SpatialProxy>() != null)
                    continue;
                if (renderer.GetComponentInParent<RegionSelectionManager>() != null)
                    continue;

                Bounds bounds = renderer.bounds;
                objects.Add(new TaskObject
                {
                    name = renderer.gameObject.name,
                    bounds_center = Arr(bounds.center),
                    bounds_size = Arr(bounds.size)
                });
            }

            return objects;
        }

        private static float[] Arr(Vector3 v) =>
            new[] { (float)Math.Round(v.x, 3), (float)Math.Round(v.y, 3), (float)Math.Round(v.z, 3) };
    }
}
