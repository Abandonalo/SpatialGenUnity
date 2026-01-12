using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(SpatialProxy))]
public class SpatialProxyGizmo : Editor
{
    private void OnSceneGUI()
    {
        SpatialProxy proxy = (SpatialProxy)target;

        Handles.color = Color.cyan;
        Vector3 newSize = Handles.ScaleHandle(
            proxy.size,
            proxy.transform.position,
            proxy.transform.rotation,
            HandleUtility.GetHandleSize(proxy.transform.position)
        );

        if (newSize != proxy.size)
        {
            Undo.RecordObject(proxy, "Resize Spatial Proxy");
            proxy.size = newSize;
        }
    }

    private void OnDrawGizmos()
    {
        SpatialProxy proxy = (SpatialProxy)target;
        Gizmos.color = new Color(0, 1, 1, 0.2f);
        Gizmos.DrawCube(proxy.transform.position, proxy.size);
    }
}
