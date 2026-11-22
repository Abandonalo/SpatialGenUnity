using System;
using System.Collections.Generic;
using UnityEngine;
using SpatialGeneration.Authoring;

namespace SpatialGeneration.Agent
{
    // Shapes tool results for the agent and parses the arguments it sends.
    public static partial class AgentSceneOps
    {
        // ------------------------------------------------------------------ data shaping

        private static List<object> AssetList(AgentJob job)
        {
            var list = new List<object>();
            foreach (AgentAsset asset in AllAssets(job))
                list.Add(AssetData(asset, job));
            return list;
        }

        public static Dictionary<string, object> AssetData(AgentAsset asset, AgentJob job)
        {
            Transform t = asset.transform;
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
                ["in_scope"] = InScope(job, asset)
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
                    // Names only: Enum.TryParse would also accept "7" and yield an undefined shape.
                    return Enum.TryParse(value.Trim(), true, out shape) && !char.IsDigit(value.Trim()[0]) &&
                           Enum.IsDefined(typeof(AgentPartShape), shape);
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
