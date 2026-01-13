using UnityEditor;
using UnityEngine;

public static class SpatialProxyFactory
{
    public static void CreateProxy()
{
    GameObject go = new GameObject("Spatial Proxy");
    go.transform.localScale = Vector3.one; 

    go.AddComponent<SpatialProxy>();
    Undo.RegisterCreatedObjectUndo(go, "Create Spatial Proxy");
    Selection.activeGameObject = go;
}

}
