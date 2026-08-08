using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialGeneration.Generation.Backends
{
    /// <summary>
    /// Wire format shared with the FastAPI router in <c>tools/comfy_router_backend/models.py</c>.
    /// Field names are snake_case because <see cref="JsonUtility"/> serializes them verbatim;
    /// changing a name here means changing the matching pydantic model.
    /// </summary>
    [Serializable]
    public class GenerateRequestBody
    {
        public string request_id = string.Empty;
        public string mode = "generate";

        public string prompt = string.Empty;
        public string negative_prompt = string.Empty;

        /// <summary>Reference photo for image-to-3D. Empty means text-to-3D.</summary>
        public string rgb_image = string.Empty;

        /// <summary>Linear depth of the proxy volumes, drives ControlNet depth.</summary>
        public string depth_image = string.Empty;

        /// <summary>Edge map derived from depth, drives ControlNet Canny.</summary>
        public string edges_image = string.Empty;

        /// <summary>Occupy mask for the proxy this run targets.</summary>
        public string mask_image = string.Empty;

        /// <summary>"hunyuan_2_1" or "tripo_sr".</summary>
        public string generation_model = string.Empty;

        public int geometry_resolution = 512;
        public float tripo_threshold = 25f;

        public ProxyBody proxy;
        public GenerationParamsBody generation = new();
    }

    [Serializable]
    public class GenerationParamsBody
    {
        public int seed = -1;
        public int steps = 30;
        public float cfg = 7f;
        public string sampler = "euler";
        public int width = 512;
        public int height = 512;
    }

    /// <summary>The occupy proxy a generation run is producing an asset for.</summary>
    [Serializable]
    public class ProxyBody
    {
        public string id = string.Empty;
        public string role = string.Empty;
        public string shape = string.Empty;
        public string label = string.Empty;
        public Vector3Body position = new();
        public QuaternionBody rotation = new();
        public Vector3Body size = new() { x = 1f, y = 1f, z = 1f };
    }

    [Serializable]
    public class Vector3Body
    {
        public float x;
        public float y;
        public float z;

        public static Vector3Body From(Vector3 v) => new() { x = v.x, y = v.y, z = v.z };
    }

    [Serializable]
    public class QuaternionBody
    {
        public float x;
        public float y;
        public float z;
        public float w = 1f;

        public static QuaternionBody From(Quaternion q) => new() { x = q.x, y = q.y, z = q.z, w = q.w };
    }

    /// <summary>Response of <c>GET /health</c>.</summary>
    [Serializable]
    public class HealthBody
    {
        public bool ok;
        public string comfy_url = string.Empty;

        /// <summary>Whether the router can reach the ComfyUI it submits graphs to.</summary>
        public bool comfy_reachable;

        /// <summary>Why ComfyUI could not be reached, when it could not.</summary>
        public string detail = string.Empty;
    }

    /// <summary>Response of <c>POST /generate</c>.</summary>
    [Serializable]
    public class SubmitResponseBody
    {
        public string prompt_id = string.Empty;
    }

    /// <summary>Response of <c>GET /result/{prompt_id}</c>.</summary>
    [Serializable]
    public class RunResultBody
    {
        public string prompt_id = string.Empty;

        /// <summary>"running", "success" or "error".</summary>
        public string status = string.Empty;

        public bool completed;
        public List<OutputFileBody> files = new();
        public string message = string.Empty;

        public bool IsError => string.Equals(status, "error", StringComparison.OrdinalIgnoreCase);
        public bool IsFinished => completed || IsError || (files != null && files.Count > 0);
    }

    [Serializable]
    public class OutputFileBody
    {
        public string filename = string.Empty;
        public string subfolder = string.Empty;
        public string type = "output";
    }
}
