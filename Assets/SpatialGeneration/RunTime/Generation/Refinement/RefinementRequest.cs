using System;

[Serializable]
public class RefinementRequest
{
    public string requestId = string.Empty;

    public string globalPrompt = string.Empty;
    public string localPrompt = string.Empty;

    public string rgbImageBase64 = string.Empty;
    public string depthImageBase64 = string.Empty;
    public string maskImageBase64 = string.Empty;

    public RegionSelection selection;

    public float denoiseStrength = 0.6f;
    public int steps = 20;
    public float cfgScale = 8f;

    public string sessionId = string.Empty;
}
