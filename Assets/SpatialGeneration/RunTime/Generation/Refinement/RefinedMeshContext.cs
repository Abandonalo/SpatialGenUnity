using System;
using UnityEngine;

/// <summary>
/// Payload published by <see cref="RefinementController"/> when a refined mesh
/// has been written to disk. Editor-side code (<c>RefinementMeshLoader</c>)
/// subscribes to <c>RefinementController.RefinedMeshReady</c> to instantiate
/// the mesh into the scene, using the selection bounds to place and scale it
/// so it replaces exactly the region the user chose to refine.
/// </summary>
[Serializable]
public class RefinedMeshContext
{
    public string requestId;
    public string meshAbsolutePath;
    public Vector3 selectionCenter;
    public Vector3 selectionSize;
    public Quaternion selectionRotation;
}
