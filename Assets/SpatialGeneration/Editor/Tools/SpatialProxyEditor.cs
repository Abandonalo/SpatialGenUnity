using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(SpatialProxy))]
public class SpatialProxyEditor : Editor
{
    private double _lastResizeLogTime = -999;
    private const double ResizeLogInterval = 0.10; // seconds (10 logs/sec max)

    private void OnSceneGUI()
    {
        SpatialProxy proxy = (SpatialProxy)target;

        switch (proxy.role)
        {
            case SpatialProxyRole.Occupy:
                DrawBox(proxy);
                HandleBoxResize(proxy);
                break;

            case SpatialProxyRole.Avoid:
                DrawSphere(proxy);
                HandleSphereResize(proxy);
                break;

            case SpatialProxyRole.Attract:
                DrawCylinder(proxy);
                HandleCylinderResize(proxy);
                break;
        }
    }

    public override void OnInspectorGUI()
    {
        SpatialProxy proxy = (SpatialProxy)target;

        EditorGUI.BeginChangeCheck();

        var newRole = (SpatialProxyRole)EditorGUILayout.EnumPopup("Role", proxy.role);
        var newSize = EditorGUILayout.Vector3Field("Size", proxy.size);

        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(proxy, "Edit Spatial Proxy");

            bool roleChanged = newRole != proxy.role;
            bool sizeChanged = newSize != proxy.size;

            proxy.role = newRole;
            proxy.size = newSize;
            EditorUtility.SetDirty(proxy);

            if (roleChanged)
            {
                // role changes are rare; no need to throttle, but fine if you want
                InteractionLogger.Log(new InteractionEvent
                {
                    type = "proxy_role_change",
                    proxy_id = proxy.ProxyId,
                    position = proxy.transform.position,
                    size = proxy.size,
                    role = proxy.role.ToString()
                });
            }

            if (sizeChanged)
            {
                // inspector size changes are discrete; no need to throttle, but fine if you want
                InteractionLogger.Log(new InteractionEvent
                {
                    type = "proxy_resize",
                    proxy_id = proxy.ProxyId,
                    position = proxy.transform.position,
                    size = proxy.size,
                    role = proxy.role.ToString(),
                    extra = "via_inspector"
                });
            }
        }
    }

    // --------- DRAWING ---------

    private void DrawBox(SpatialProxy proxy)
    {
        using (new Handles.DrawingScope(new Color(0f, 1f, 1f, 0.9f), proxy.transform.localToWorldMatrix))
        {
            Handles.DrawWireCube(Vector3.zero, proxy.size);
        }
    }

    private void DrawSphere(SpatialProxy proxy)
    {
        float radius = 0.5f * Mathf.Max(proxy.size.x, proxy.size.y, proxy.size.z);

        using (new Handles.DrawingScope(new Color(1f, 0.3f, 0.3f, 0.9f), proxy.transform.localToWorldMatrix))
        {
            Handles.DrawWireDisc(Vector3.zero, Vector3.up, radius);
            Handles.DrawWireDisc(Vector3.zero, Vector3.right, radius);
            Handles.DrawWireDisc(Vector3.zero, Vector3.forward, radius);
        }
    }

    private void DrawCylinder(SpatialProxy proxy)
    {
        float radiusX = proxy.size.x * 0.5f;
        float radiusZ = proxy.size.z * 0.5f;
        float radius = Mathf.Max(radiusX, radiusZ);
        float halfH = proxy.size.y * 0.5f;

        using (new Handles.DrawingScope(new Color(0.2f, 0.5f, 1f, 0.9f), proxy.transform.localToWorldMatrix))
        {
            Handles.DrawWireDisc(new Vector3(0, +halfH, 0), Vector3.up, radius);
            Handles.DrawWireDisc(new Vector3(0, -halfH, 0), Vector3.up, radius);

            Handles.DrawLine(new Vector3(+radius, -halfH, 0), new Vector3(+radius, +halfH, 0));
            Handles.DrawLine(new Vector3(-radius, -halfH, 0), new Vector3(-radius, +halfH, 0));
            Handles.DrawLine(new Vector3(0, -halfH, +radius), new Vector3(0, +halfH, +radius));
            Handles.DrawLine(new Vector3(0, -halfH, -radius), new Vector3(0, +halfH, -radius));
        }
    }

    // --------- HANDLES ---------

    private void HandleBoxResize(SpatialProxy proxy)
    {
        EditorGUI.BeginChangeCheck();

        Handles.color = Color.cyan;
        Vector3 newSize = Handles.ScaleHandle(
            proxy.size,
            proxy.transform.position,
            proxy.transform.rotation,
            HandleUtility.GetHandleSize(proxy.transform.position)
        );

        newSize = ClampMinSize(newSize);

        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(proxy, "Resize Box Proxy");
            proxy.size = newSize;
            EditorUtility.SetDirty(proxy);

            ThrottledResizeLog(proxy, "box");
        }
    }

    private void HandleSphereResize(SpatialProxy proxy)
    {
        float radius = 0.5f * Mathf.Max(proxy.size.x, proxy.size.y, proxy.size.z);

        EditorGUI.BeginChangeCheck();

        Handles.color = new Color(1f, 0.3f, 0.3f, 0.9f);
        float newRadius = Handles.RadiusHandle(proxy.transform.rotation, proxy.transform.position, radius);
        newRadius = Mathf.Max(0.01f, newRadius);

        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(proxy, "Resize Sphere Proxy");

            float d = newRadius * 2f;
            proxy.size = new Vector3(d, d, d);
            EditorUtility.SetDirty(proxy);

            ThrottledResizeLog(proxy, "sphere");
        }
    }

    private void HandleCylinderResize(SpatialProxy proxy)
    {
        float radius = 0.5f * Mathf.Max(proxy.size.x, proxy.size.z);
        float halfH = proxy.size.y * 0.5f;

        EditorGUI.BeginChangeCheck();

        // Radius handle (XZ)
        Handles.color = new Color(0.2f, 0.5f, 1f, 0.9f);
        float newRadius = Handles.RadiusHandle(proxy.transform.rotation, proxy.transform.position, radius);
        newRadius = Mathf.Max(0.01f, newRadius);

        // Height sliders (top/bottom)
        Vector3 axis = proxy.transform.up;

        Vector3 topWorld = proxy.transform.position + axis * halfH;
        Vector3 bottomWorld = proxy.transform.position - axis * halfH;

        float handleSizeTop = HandleUtility.GetHandleSize(topWorld) * 0.2f;
        float handleSizeBottom = HandleUtility.GetHandleSize(bottomWorld) * 0.2f;

        Vector3 newTopWorld = Handles.Slider(topWorld, axis, handleSizeTop, Handles.ConeHandleCap, 0f);
        Vector3 newBottomWorld = Handles.Slider(bottomWorld, -axis, handleSizeBottom, Handles.ConeHandleCap, 0f);

        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(proxy, "Resize Cylinder Proxy");

            float newHeight = Vector3.Distance(newTopWorld, newBottomWorld);
            newHeight = Mathf.Max(0.02f, newHeight);

            proxy.size = new Vector3(newRadius * 2f, newHeight, newRadius * 2f);
            EditorUtility.SetDirty(proxy);

            ThrottledResizeLog(proxy, "cylinder");
        }
    }

    private void ThrottledResizeLog(SpatialProxy proxy, string shapeTag)
    {
        double now = EditorApplication.timeSinceStartup;
        if (now - _lastResizeLogTime > ResizeLogInterval)
        {
            _lastResizeLogTime = now;

            InteractionLogger.Log(new InteractionEvent
            {
                type = "proxy_resize",
                proxy_id = proxy.ProxyId,
                position = proxy.transform.position,
                size = proxy.size,
                role = proxy.role.ToString(),
                extra = shapeTag
            });
        }
    }

    private Vector3 ClampMinSize(Vector3 v)
    {
        v.x = Mathf.Max(0.01f, v.x);
        v.y = Mathf.Max(0.01f, v.y);
        v.z = Mathf.Max(0.01f, v.z);
        return v;
    }
}
