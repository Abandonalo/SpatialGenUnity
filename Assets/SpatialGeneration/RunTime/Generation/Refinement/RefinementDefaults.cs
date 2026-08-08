/// <summary>
/// Refinement constants shared with the router. Mirrors the matching values in
/// <c>tools/comfy_router_backend/models.py</c>; change both together.
/// </summary>
public static class RefinementDefaults
{
    /// <summary>
    /// KSampler denoise inside the mask. High because the masked pixels are being replaced,
    /// not nudged: at low denoise the inpaint returns a blurred copy of the original.
    /// </summary>
    public const float Denoise = 0.95f;

    public const int Steps = 30;
    public const float Cfg = 8f;

    /// <summary>Canonical capture resolution per view.</summary>
    public const int ViewResolution = 512;
}
