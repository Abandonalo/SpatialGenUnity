using System;
using System.Collections.Generic;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SpatialGeneration.Agent.Mcp
{
    // MCP for Unity discovers these by attribute and exposes them to Claude Code / Codex.
    // Each wrapper only parses arguments; all rules live in AgentSceneOps.

    internal static class McpGlue
    {
        public static object Respond(OpResult result) =>
            result.ok ? new SuccessResponse(result.message, result.data) : new ErrorResponse(result.message, result.data);

        public static string Str(JObject p, string key) => p?[key]?.Type == JTokenType.Null ? null : p?[key]?.ToString();

        public static float Float(JObject p, string key, float fallback)
        {
            JToken token = p?[key];
            if (token == null || token.Type == JTokenType.Null)
                return fallback;
            try { return token.Value<float>(); }
            catch (Exception) { return fallback; }
        }

        public static float[] Vec(JToken token)
        {
            token = Unwrap(token);
            if (token is not JArray array || array.Count != 3)
                return null;
            try { return new[] { array[0].Value<float>(), array[1].Value<float>(), array[2].Value<float>() }; }
            catch (Exception) { return null; }
        }

        /// <summary>Accepts a JSON array, or a string holding one (some clients stringify nested arguments).</summary>
        public static JToken Unwrap(JToken token)
        {
            if (token != null && token.Type == JTokenType.String)
            {
                string text = token.ToString().Trim();
                if (text.StartsWith("[") || text.StartsWith("{"))
                {
                    try { return JToken.Parse(text); }
                    catch (JsonException) { return token; }
                }
            }

            return token;
        }

        public static List<PartSpec> Parts(JObject p)
        {
            JToken token = Unwrap(p?["parts"]);
            if (token is JObject single)
                token = new JArray(single);
            if (token is not JArray array)
                return null;

            var specs = new List<PartSpec>();
            foreach (JToken item in array)
            {
                if (Unwrap(item) is not JObject o)
                    continue;
                int? segments = null;
                if (o["segments"] != null && o["segments"].Type != JTokenType.Null)
                {
                    try { segments = o["segments"].Value<int>(); }
                    catch (Exception) { segments = null; }
                }

                specs.Add(new PartSpec
                {
                    id = Str(o, "id"),
                    name = Str(o, "name"),
                    shape = Str(o, "shape"),
                    center = Vec(o["center"]),
                    size = Vec(o["size"]),
                    rotation = Vec(o["rotation"]),
                    color = Str(o, "color"),
                    segments = segments
                });
            }

            return specs;
        }

        public static List<string> Ids(JObject p, string key)
        {
            JToken token = Unwrap(p?[key]);
            var ids = new List<string>();
            if (token is JArray array)
            {
                foreach (JToken item in array)
                    ids.Add(item.ToString());
            }
            else if (token != null && token.Type == JTokenType.String)
            {
                ids.Add(token.ToString());
            }

            return ids;
        }
    }

    [McpForUnityTool("spatialgen_get_task",
        Description = "Spatial Generation: the current task (mode, condition, request, edit region, assets) and the coordinate conventions. Call this first.")]
    public static class SpatialGenGetTask
    {
        public class Parameters
        {
        }

        public static object HandleCommand(JObject @params) => McpGlue.Respond(AgentSceneOps.GetTask());
    }

    [McpForUnityTool("spatialgen_list_assets",
        Description = "Spatial Generation: list the generated assets with id, label, prompt, origin, yaw, volume and part count.")]
    public static class SpatialGenListAssets
    {
        public class Parameters
        {
        }

        public static object HandleCommand(JObject @params) => McpGlue.Respond(AgentSceneOps.ListAssets());
    }

    [McpForUnityTool("spatialgen_list_parts",
        Description = "Spatial Generation: list one asset's parts in its own frame, each with editable=true/false for the current task.")]
    public static class SpatialGenListParts
    {
        public class Parameters
        {
            [ToolParameter("Asset id from spatialgen_list_assets.")]
            public string asset_id { get; set; }
        }

        public static object HandleCommand(JObject @params) =>
            McpGlue.Respond(AgentSceneOps.ListParts(McpGlue.Str(@params, "asset_id")));
    }

    [McpForUnityTool("spatialgen_create_asset",
        Description = "Spatial Generation: create an empty asset group (text-only condition, or a new object inside an edit region). " +
                      "origin = footprint centre on the ground in world coordinates [x,y,z]; yaw_degrees turns its front (+X) about +Y; " +
                      "volume_size = [depth along X, height, width along Z] in metres.")]
    public static class SpatialGenCreateAsset
    {
        public class Parameters
        {
            [ToolParameter("Short name, e.g. \"left stall\".")]
            public string label { get; set; }

            [ToolParameter("What the asset is, e.g. \"wooden market stall with a striped awning\".", Required = false)]
            public string prompt { get; set; }

            [ToolParameter("Footprint centre on the ground, world coordinates [x, y, z].")]
            public List<float> origin { get; set; }

            [ToolParameter("Rotation about +Y in degrees; 0 means the front faces world +X.", Required = false)]
            public float? yaw_degrees { get; set; }

            [ToolParameter("[depth, height, width] in metres.")]
            public List<float> volume_size { get; set; }
        }

        public static object HandleCommand(JObject @params) =>
            McpGlue.Respond(AgentSceneOps.CreateAsset(
                McpGlue.Str(@params, "label"),
                McpGlue.Str(@params, "prompt"),
                McpGlue.Vec(@params?["origin"]),
                McpGlue.Float(@params, "yaw_degrees", 0f),
                McpGlue.Vec(@params?["volume_size"])));
    }

    [McpForUnityTool("spatialgen_add_parts",
        Description = "Spatial Generation: add primitives to an asset. parts = array of {name, shape (box|wedge|pyramid|cylinder|cone|sphere), " +
                      "center [x,y,z], size [x,y,z], rotation [x,y,z] Euler degrees, color \"#RRGGBB\", segments} in the asset's frame.")]
    public static class SpatialGenAddParts
    {
        public class Parameters
        {
            [ToolParameter("Asset id.")]
            public string asset_id { get; set; }

            [ToolParameter("Array of part objects; send many at once.")]
            public List<object> parts { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            List<PartSpec> parts = McpGlue.Parts(@params);
            if (parts == null)
                return new ErrorResponse("parts must be an array of part objects.");
            return McpGlue.Respond(AgentSceneOps.AddParts(McpGlue.Str(@params, "asset_id"), parts));
        }
    }

    [McpForUnityTool("spatialgen_update_parts",
        Description = "Spatial Generation: change existing parts. parts = array of {id, plus any of name, shape, center, size, rotation, color, segments}; " +
                      "omitted fields stay as they are. Only editable parts can change.")]
    public static class SpatialGenUpdateParts
    {
        public class Parameters
        {
            [ToolParameter("Asset id.")]
            public string asset_id { get; set; }

            [ToolParameter("Array of {id, ...changed fields}.")]
            public List<object> parts { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            List<PartSpec> parts = McpGlue.Parts(@params);
            if (parts == null)
                return new ErrorResponse("parts must be an array of objects with an id.");
            return McpGlue.Respond(AgentSceneOps.UpdateParts(McpGlue.Str(@params, "asset_id"), parts));
        }
    }

    [McpForUnityTool("spatialgen_remove_parts",
        Description = "Spatial Generation: remove parts by id. Only editable parts can be removed.")]
    public static class SpatialGenRemoveParts
    {
        public class Parameters
        {
            [ToolParameter("Asset id.")]
            public string asset_id { get; set; }

            [ToolParameter("Array of part ids.")]
            public List<string> part_ids { get; set; }
        }

        public static object HandleCommand(JObject @params) =>
            McpGlue.Respond(AgentSceneOps.RemoveParts(McpGlue.Str(@params, "asset_id"), McpGlue.Ids(@params, "part_ids")));
    }

    [McpForUnityTool("spatialgen_clear_asset",
        Description = "Spatial Generation: remove every part of one asset so it can be rebuilt (not allowed in region edits).")]
    public static class SpatialGenClearAsset
    {
        public class Parameters
        {
            [ToolParameter("Asset id.")]
            public string asset_id { get; set; }
        }

        public static object HandleCommand(JObject @params) =>
            McpGlue.Respond(AgentSceneOps.ClearAsset(McpGlue.Str(@params, "asset_id")));
    }

    [McpForUnityTool("spatialgen_finish",
        Description = "Spatial Generation: report that the task is complete, with a one-sentence summary. Call once at the end.")]
    public static class SpatialGenFinish
    {
        public class Parameters
        {
            [ToolParameter("One sentence describing what you built or changed.")]
            public string summary { get; set; }
        }

        public static object HandleCommand(JObject @params) =>
            McpGlue.Respond(AgentSceneOps.Finish(McpGlue.Str(@params, "summary")));
    }
}
