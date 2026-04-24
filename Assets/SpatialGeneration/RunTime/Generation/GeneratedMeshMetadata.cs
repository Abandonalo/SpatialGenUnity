using UnityEngine;

/// <summary>
/// Per-mesh metadata attached to each <c>Generated_Mesh_*</c> when a scene is
/// initially generated. Refinement uses this to locate the original 2D source
/// image (<c>spatialgen_mesh_source_*.png</c>) the mesh was lifted from, so it
/// can inpaint on that image directly and re-run the same lifting pipeline,
/// instead of re-rendering the current scene (which drifts the non-masked
/// regions).
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("Spatial Generation/Generated Mesh Metadata")]
public class GeneratedMeshMetadata : MonoBehaviour
{
    /// <summary>Absolute path to the <c>spatialgen_mesh_source_*.png</c>
    /// image that was fed into the 3D lifting (TripoSR) stage when this mesh
    /// was generated. Empty if the path could not be recovered.</summary>
    public string sourceImagePath;

    /// <summary>Absolute path to the original mesh file (e.g. meshsave_*.glb).
    /// Informational; kept alongside the source image path for debugging.</summary>
    public string meshPath;

    /// <summary>Occupy proxy id this mesh was placed against.</summary>
    public string proxyId;

    /// <summary>World-space position the proxy placement used (bounds
    /// center). Cached so refinement can rebuild the exact same pose when
    /// replacing this mesh with a refined one.</summary>
    public Vector3 proxyPosition;

    /// <summary>World-space rotation applied during proxy placement.</summary>
    public Quaternion proxyRotation = Quaternion.identity;

    /// <summary>Target bounds size the proxy placement was fit to.</summary>
    public Vector3 proxySize;
}
