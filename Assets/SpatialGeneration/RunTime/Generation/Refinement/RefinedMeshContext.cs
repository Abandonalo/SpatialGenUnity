using System;
using UnityEngine;

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
}
