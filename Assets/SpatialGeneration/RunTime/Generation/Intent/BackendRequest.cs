using System;

namespace SpatialGeneration.Generation.Intent
{
    [Serializable]
    public class BackendRequest
    {
        public string RequestId = string.Empty;

        public string Prompt = string.Empty;

        public string NegativePrompt = string.Empty;

        public string ConstraintSetJson = string.Empty;

        public ComfyUIRequestPayload Payload = new();
    }

    [Serializable]
    public class ComfyUIRequestPayload
    {
        public string DepthBase64 = string.Empty;

        public string EdgesBase64 = string.Empty;

        public string MaskOccupyBase64 = string.Empty;

        public string MaskAvoidBase64 = string.Empty;

        public string MaskFocusBase64 = string.Empty;

        public GenerationParams Generation = new();
    }

    [Serializable]
    public class GenerationParams
    {
        public int Seed = -1;

        public int Steps = 30;

        public float Cfg = 7.0f;

        public string Sampler = "euler";

        public int Width = 1024;

        public int Height = 1024;
    }
}
