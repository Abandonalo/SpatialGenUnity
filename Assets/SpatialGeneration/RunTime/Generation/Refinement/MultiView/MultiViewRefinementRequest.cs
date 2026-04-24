using System;
using System.Collections.Generic;

// Per-view RGB / depth / mask triplet. viewType is serialized as a string
// (enum name) so the Python backend can key per-view logic without being
// coupled to the C# enum ordering.
[Serializable]
public class ViewData
{
    public string viewType = string.Empty;
    public int width;
    public int height;
    public string rgbBase64 = string.Empty;
    public string depthBase64 = string.Empty;
    public string maskBase64 = string.Empty;
}

[Serializable]
public class MultiViewData
{
    public List<ViewData> views = new List<ViewData>();
}

// Multi-view refinement payload. Every view MUST share:
//   - the same seed (deterministic, cross-view consistent latents)
//   - the same width/height
//   - the same prompt pair
// so the server-side inpainter yields mutually consistent refined images.
[Serializable]
public class MultiViewRefinementRequest
{
    public string requestId = string.Empty;
    public string sessionId = string.Empty;

    public string mode = "refine";

    public string positivePrompt = string.Empty;
    public string negativePrompt = string.Empty;

    // Fixed seed shared across every view. Must be non-negative; the
    // controller is responsible for populating this deterministically so
    // repeated runs with the same scene + prompt produce identical output.
    public int seed;

    public int steps = 20;
    public float cfg = 8f;
    public float denoise = 1.0f;

    // Preferred view used for the final TripoSR reconstruction.
    public string reconstructionView = "Front";

    public List<ViewData> views = new List<ViewData>();

    public RegionSelection selection;
}

[Serializable]
public class RefinedViewResult
{
    public string viewType = string.Empty;
    public string refinedImageBase64 = string.Empty;
}

[Serializable]
public class MultiViewRefinementResponse
{
    public string requestId = string.Empty;
    public List<RefinedViewResult> refinedViews = new List<RefinedViewResult>();

    // Mesh produced from the reconstructionView (or Front by default).
    public string meshBase64 = string.Empty;

    public bool success;
    public string errorMessage = string.Empty;
}
