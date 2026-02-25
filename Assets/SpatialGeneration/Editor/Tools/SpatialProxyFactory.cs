using UnityEditor;
using UnityEngine;

public static class SpatialProxyFactory
{
    public static void CreateProxy()
    {
        GameObject go = new GameObject("Spatial Proxy");
        go.transform.localScale = Vector3.one;

        SpatialProxy proxy = go.AddComponent<SpatialProxy>();

        Undo.RegisterCreatedObjectUndo(go, "Create Spatial Proxy");
        Selection.activeGameObject = go;

        InteractionLogger.Log(new InteractionEvent
        {
            type = "proxy_create",
            proxy_id = proxy.ProxyId,
            position = go.transform.position,
            size = proxy.size,
            role = proxy.role.ToString(),
            shape = proxy.Shape.ToString(),
            label = proxy.label,
            strength = proxy.strength,
            priority = proxy.priority
        });
    }
}
