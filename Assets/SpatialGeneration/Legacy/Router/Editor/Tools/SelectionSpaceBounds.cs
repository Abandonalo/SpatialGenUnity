using UnityEngine;

/// <summary>
/// Accumulates mesh geometry in an oriented refinement box's local coordinate frame.
/// This describes the source surface that was actually removed, independently of how much
/// empty space the user left around it when drawing the selection box.
/// </summary>
public sealed class SelectionSpaceBounds
{
    private readonly RegionSelection selection;
    private readonly Matrix4x4 worldToSelection;
    private Vector3 min = Vector3.positiveInfinity;
    private Vector3 max = Vector3.negativeInfinity;

    public SelectionSpaceBounds(RegionSelection selection)
    {
        this.selection = selection;
        worldToSelection = selection != null ? selection.WorldToLocal() : Matrix4x4.identity;
    }

    public bool IsValid { get; private set; }
    public Vector3 LocalMin => IsValid ? min : Vector3.zero;
    public Vector3 LocalMax => IsValid ? max : Vector3.zero;
    public Vector3 LocalCenter => IsValid ? (min + max) * 0.5f : Vector3.zero;
    public Vector3 Size => IsValid ? max - min : Vector3.zero;
    public Vector3 WorldCenter => selection != null
        ? selection.center + selection.rotation * LocalCenter
        : LocalCenter;

    public void Encapsulate(Mesh mesh, Matrix4x4 localToWorld)
    {
        if (mesh == null)
            return;

        foreach (Vector3 vertex in mesh.vertices)
        {
            Vector3 point = worldToSelection.MultiplyPoint3x4(
                localToWorld.MultiplyPoint3x4(vertex));
            if (!Finite(point))
                continue;
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
            IsValid = true;
        }
    }

    private static bool Finite(Vector3 value) =>
        !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
        !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
        !float.IsNaN(value.z) && !float.IsInfinity(value.z);
}
