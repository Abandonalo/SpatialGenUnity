using System.Collections.Generic;
using UnityEngine;
using SpatialGeneration.Generation.Intent;

/// <summary>Everything the backend produced for a single occupy proxy.</summary>
public class AssetGenerationResult
{
    /// <summary>Proxy this asset belongs to. Carried through so placement never
    /// has to match meshes to proxies by list index.</summary>
    public string ProxyId = string.Empty;

    /// <summary>Absolute path of the generated mesh, or empty when the backend
    /// produced no mesh and <see cref="FallbackPrimitive"/> should be used.</summary>
    public string MeshPath = string.Empty;

    /// <summary>Every file downloaded for this run (meshes, preview images).</summary>
    public List<string> OutputFiles = new();

    /// <summary>Primitive to place when the backend returned no mesh.</summary>
    public PrimitiveType? FallbackPrimitive;

    public bool HasMesh => !string.IsNullOrWhiteSpace(MeshPath);
}

/// <summary>Aggregated results of one Generate action across all occupy proxies.</summary>
public class GenerationResult
{
    public List<AssetGenerationResult> Assets = new();

    /// <summary>Directory holding this run's request and conditioning artifacts.</summary>
    public string ArtifactDirectory = string.Empty;
}

/// <summary>
/// A single asset generation: one occupy proxy, its conditioning images and its prompt.
/// </summary>
public class AssetGenerationRequest
{
    public string RequestId = string.Empty;
    public string ProxyId = string.Empty;

    public string Prompt = string.Empty;
    public string NegativePrompt = string.Empty;

    /// <summary>Linear depth of the proxy volume. Drives ControlNet depth.</summary>
    public string DepthBase64 = string.Empty;

    /// <summary>Sobel edges of <see cref="DepthBase64"/>. Drives ControlNet Canny.</summary>
    public string EdgesBase64 = string.Empty;

    /// <summary>Proxy silhouette, white on black.</summary>
    public string MaskBase64 = string.Empty;

    /// <summary>Optional reference photo assigned on the proxy; switches the
    /// backend from text-to-3D to image-to-3D.</summary>
    public string ReferenceImageBase64 = string.Empty;

    /// <summary>Pose and shape of the proxy, so the backend can log or condition on it.</summary>
    public ProxyVolume Volume = new();

    public int Seed = -1;
    public int Steps = 30;
    public float Cfg = 7f;
    public string Sampler = "euler";
    public int Width = 512;
    public int Height = 512;
}

/// <summary>Pose and shape of an authored proxy, in Unity world space.</summary>
public class ProxyVolume
{
    public ProxyRole Role = ProxyRole.Occupy;
    public ProxyShape Shape = ProxyShape.Box;
    public string Label = string.Empty;
    public Vector3 Position = Vector3.zero;
    public Quaternion Rotation = Quaternion.identity;
    public Vector3 Size = Vector3.one;

    public PrimitiveType ToPrimitive() => Shape switch
    {
        ProxyShape.Sphere => PrimitiveType.Sphere,
        ProxyShape.Cylinder => PrimitiveType.Cylinder,
        _ => PrimitiveType.Cube
    };
}
