using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One canonical view's capture. <see cref="viewType"/> travels as the enum name so the
/// Python router is not coupled to C# enum ordering.
/// </summary>
[Serializable]
public class ViewData
{
    public string viewType = string.Empty;
    public int width;
    public int height;
    public string rgbBase64 = string.Empty;
    public string depthBase64 = string.Empty;

    /// <summary>Sobel edges of the depth pass; used as Canny conditioning.</summary>
    public string edgesBase64 = string.Empty;

    /// <summary>Binary inpaint mask: the selection box intersected with visible surfaces.</summary>
    public string maskBase64 = string.Empty;

    /// <summary>Projected selection bounds for this view, in bottom-left viewport UV.</summary>
    public float cropMinX;
    public float cropMinY;
    public float cropMaxX = 1f;
    public float cropMaxY = 1f;

    /// <summary>Capture-time projection state; do not re-read a camera after inference.</summary>
    public Matrix4x4 cameraWorldToCamera;
    public Matrix4x4 cameraProjection;
    public Vector3 cameraPosition;
    public Vector3 cameraForward;
}

[Serializable]
public class MultiViewData
{
    public List<ViewData> views = new();
}

/// <summary>
/// Refinement payload. Every view shares one seed, one prompt and one resolution: that is
/// what keeps the four inpaints describing the same object rather than four variations of it.
/// Mirrors <c>MultiViewRefinementRequestModel</c> in the router.
/// </summary>
[Serializable]
public class MultiViewRefinementRequest
{
    public string requestId = string.Empty;
    public string sessionId = string.Empty;
    public string mode = "refine";

    public string positivePrompt = string.Empty;
    public string negativePrompt = string.Empty;

    public int seed;
    public int steps = RefinementDefaults.Steps;
    public float cfg = RefinementDefaults.Cfg;
    public float denoise = RefinementDefaults.Denoise;

    /// <summary>"auto", "hunyuan3d_2mv" or "tripo_sr".</summary>
    public string lifter = "auto";
    public bool allowFallback = true;

    /// <summary>View whose refined image is lifted back to 3D.</summary>
    public string reconstructionView = "Front";

    /// <summary>
    /// The selection's footprint in the reconstruction view, in viewport UV. The router crops
    /// the refined image to this rect before lifting, so the resulting mesh spans the region
    /// the user selected and not the surrounding context the cameras included for blending.
    /// Sent as four floats rather than a Vector4 to keep the JSON flat for pydantic.
    /// </summary>
    public float cropMinX;
    public float cropMinY;
    public float cropMaxX = 1f;
    public float cropMaxY = 1f;

    public List<ViewData> views = new();

    /// <summary>Copy without image payloads, for writing a readable debug artifact.</summary>
    public MultiViewRefinementRequest WithoutViewPayloads()
    {
        var copy = new MultiViewRefinementRequest
        {
            requestId = requestId,
            sessionId = sessionId,
            mode = mode,
            positivePrompt = positivePrompt,
            negativePrompt = negativePrompt,
            seed = seed,
            steps = steps,
            cfg = cfg,
            denoise = denoise,
            lifter = lifter,
            allowFallback = allowFallback,
            reconstructionView = reconstructionView,
            cropMinX = cropMinX,
            cropMinY = cropMinY,
            cropMaxX = cropMaxX,
            cropMaxY = cropMaxY
        };
        foreach (ViewData view in views)
        {
            copy.views.Add(new ViewData
            {
                viewType = view.viewType,
                width = view.width,
                height = view.height,
                cropMinX = view.cropMinX,
                cropMinY = view.cropMinY,
                cropMaxX = view.cropMaxX,
                cropMaxY = view.cropMaxY,
                cameraWorldToCamera = view.cameraWorldToCamera,
                cameraProjection = view.cameraProjection,
                cameraPosition = view.cameraPosition,
                cameraForward = view.cameraForward
            });
        }
        return copy;
    }
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
    public List<RefinedViewResult> refinedViews = new();

    /// <summary>The lifted region mesh, base64 .glb.</summary>
    public string meshBase64 = string.Empty;

    public bool success;
    public string status = string.Empty;
    public string errorMessage = string.Empty;
    public string lifterUsed = string.Empty;
    public bool fallbackUsed;
    public List<string> warnings = new();

    /// <summary>True while the router still has the job queued or running.</summary>
    public bool IsPending
    {
        get
        {
            if (!success)
                return false;

            string normalized = (status ?? string.Empty).Trim().ToLowerInvariant();
            return normalized is "queued" or "running" or "pending";
        }
    }
}
