using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class RefinedViewProjection
{
    public string viewType;
    public string imageAbsolutePath;
    public Camera camera;
    public Matrix4x4 worldToCameraMatrix;
    public Matrix4x4 projectionMatrix;
    public Vector3 cameraPosition;
    public Vector3 cameraForward;
    public bool hasStoredProjection;
    public float cropMinX;
    public float cropMinY;
    public float cropMaxX = 1f;
    public float cropMaxY = 1f;
    public bool flipVertical;
}

/// <summary>
/// Published by <see cref="RefinementController"/> once a refined region mesh is on disk.
/// The editor-side <c>RefinementMeshLoader</c> subscribes to
/// <see cref="RefinementController.RefinedMeshReady"/> and splices the mesh into the scene:
/// the region is replaced, everything outside <see cref="Region"/> is preserved verbatim.
/// </summary>
[Serializable]
public class RefinedMeshContext
{
    public string requestId;

    /// <summary>Absolute path of the .glb the backend returned.</summary>
    public string meshAbsolutePath;

    /// <summary>The box the user selected. Doubles as the split plane set and the placement target.</summary>
    public RegionSelection Region;

    /// <summary>Refined full-frame images and immutable capture-time projections.</summary>
    public List<RefinedViewProjection> Views = new();

    public string LifterUsed = string.Empty;
}
