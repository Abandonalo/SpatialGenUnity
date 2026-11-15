using UnityEngine;

namespace SpatialGeneration.Agent
{
    /// <summary>
    /// Root of one agent-built asset. Its transform defines the asset frame the agent models in:
    /// origin at the centre of the footprint on the ground, +Y up, +X the asset's front.
    /// Parts are its direct children.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Spatial Generation/Agent Asset")]
    public class AgentAsset : MonoBehaviour
    {
        public string assetId;
        public string label;
        [TextArea(2, 5)] public string prompt;

        /// <summary>Spatial proxy this asset was generated for; empty in the text-only condition.</summary>
        public string proxyId;

        /// <summary>
        /// Volume the asset should occupy, in the asset frame: x is depth (front-back),
        /// y height, z width. From the proxy in condition B, from the agent in condition A.
        /// </summary>
        public Vector3 volumeSize = Vector3.one;

        public string createdByJob;
    }
}
