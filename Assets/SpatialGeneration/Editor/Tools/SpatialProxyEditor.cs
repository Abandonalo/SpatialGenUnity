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
        var newLabel = EditorGUILayout.TextField("Label", proxy.label);
        EditorGUILayout.LabelField("Asset Prompt");
        var newAssetPrompt = EditorGUILayout.TextArea(proxy.assetPrompt, GUILayout.MinHeight(48f));
        var newAssetImage = (Texture2D)EditorGUILayout.ObjectField("Asset Image", proxy.assetImage, typeof(Texture2D), false);
        if (newAssetImage != null)
        {
            EditorGUILayout.HelpBox(
                "When an asset image is assigned, this proxy uses the image-to-3D workflow. The text prompt remains available as fallback metadata.",
                MessageType.Info);
        }
        var newStrength = EditorGUILayout.Slider("Strength", proxy.strength, 0f, 1f);
        var newPriority = EditorGUILayout.IntField("Priority", proxy.priority);
        var newSize = EditorGUILayout.Vector3Field("Size", proxy.size);

        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.TextField("Proxy ID", proxy.ProxyId);
            EditorGUILayout.TextField("Shape", proxy.Shape.ToString());
        }

        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(proxy, "Edit Spatial Proxy");

            bool roleChanged = newRole != proxy.role;
            bool labelChanged = newLabel != proxy.label;
            bool assetPromptChanged = newAssetPrompt != proxy.assetPrompt;
            bool assetImageChanged = newAssetImage != proxy.assetImage;
            bool strengthChanged = !Mathf.Approximately(newStrength, proxy.strength);
            bool priorityChanged = newPriority != proxy.priority;
            bool sizeChanged = newSize != proxy.size;

            proxy.role = newRole;
            proxy.label = newLabel;
            proxy.assetPrompt = newAssetPrompt;
            proxy.assetImage = newAssetImage;
            proxy.strength = Mathf.Clamp01(newStrength);
            proxy.priority = newPriority;
            proxy.size = newSize;
            EditorUtility.SetDirty(proxy);

            if (roleChanged)
            {
                InteractionLogger.Log(CreateProxyEvent(proxy, "proxy_role_change"));
            }

            if (labelChanged)
            {
                InteractionLogger.Log(CreateProxyEvent(proxy, "proxy_label_change"));
            }

            if (assetPromptChanged)
            {
                InteractionLogger.Log(CreateProxyEvent(proxy, "proxy_asset_prompt_change"));
            }

            if (assetImageChanged)
            {
                InteractionLogger.Log(CreateProxyEvent(proxy, "proxy_asset_image_change", newAssetImage != null ? "assigned" : "cleared"));
            }

            if (strengthChanged)
            {
                InteractionLogger.Log(CreateProxyEvent(proxy, "proxy_strength_change"));
            }

            if (priorityChanged)
            {
                InteractionLogger.Log(CreateProxyEvent(proxy, "proxy_priority_change"));
            }

            if (sizeChanged)
            {
                InteractionLogger.Log(CreateProxyEvent(proxy, "proxy_resize", "via_inspector"));
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
            InteractionLogger.Log(CreateProxyEvent(proxy, "proxy_resize", shapeTag));
        }
    }

    private static InteractionEvent CreateProxyEvent(SpatialProxy proxy, string type, string extra = null)
    {
        return new InteractionEvent
        {
            type = type,
            proxy_id = proxy.ProxyId,
            role = proxy.role.ToString(),
            shape = proxy.Shape.ToString(),
            label = proxy.label,
            strength = proxy.strength,
            priority = proxy.priority,
            position = proxy.transform.position,
            size = proxy.size,
            extra = extra
        };
    }

    private Vector3 ClampMinSize(Vector3 v)
    {
        v.x = Mathf.Max(0.01f, v.x);
        v.y = Mathf.Max(0.01f, v.y);
        v.z = Mathf.Max(0.01f, v.z);
        return v;
    }
}
