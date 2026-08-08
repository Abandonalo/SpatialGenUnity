using UnityEngine;

/// <summary>
/// Records which proxy a generated mesh came from and the pose it was fitted to.
/// Refinement reads this to restore the same orientation when it splices a region back in.
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("Spatial Generation/Generated Mesh Metadata")]
public class GeneratedMeshMetadata : MonoBehaviour
{
    /// <summary>Project-relative path of the imported mesh asset.</summary>
    public string meshPath;

    /// <summary>Occupy proxy this mesh was generated and placed for.</summary>
    public string proxyId;

    /// <summary>World pose the proxy had at generation time.</summary>
    public Vector3 proxyPosition;

    public Quaternion proxyRotation = Quaternion.identity;

    /// <summary>Bounds size the mesh was fitted to.</summary>
    public Vector3 proxySize;
}
