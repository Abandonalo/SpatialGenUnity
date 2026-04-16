using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(RegionSelectionManager))]
public class RegionSelectionManagerEditor : Editor
{
    private const string OverlayShaderName = "Hidden/SpatialGen/RegionSelectionOverlay";
    private static Material _overlayMaterial;

    private void OnEnable()
    {
        Tools.hidden = true;
    }

    private void OnDisable()
    {
        Tools.hidden = false;
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
    }

    private void OnSceneGUI()
    {
        RegionSelectionManager manager = (RegionSelectionManager)target;
        RegionSelection selection = manager.CurrentSelection;
        if (selection == null)
            return;

        Tools.hidden = Selection.activeGameObject == manager.gameObject;

        Color color = manager.IsSelecting
            ? new Color(1f, 0.8f, 0.2f, 1f)
            : new Color(0.2f, 1f, 0.7f, 1f);

        DrawSelectionOverlay(manager, selection);

        Matrix4x4 handleMatrix = Matrix4x4.TRS(selection.center, selection.rotation, Vector3.one);
        using (new Handles.DrawingScope(color, handleMatrix))
        {
            Handles.DrawWireCube(Vector3.zero, selection.size);
        }

        EditorGUI.BeginChangeCheck();
        Handles.color = color;
        Vector3 updatedSize = Handles.ScaleHandle(
            selection.size,
            selection.center,
            selection.rotation,
            HandleUtility.GetHandleSize(selection.center));
        updatedSize = ClampSize(updatedSize);

        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(manager, "Resize Region Selection");
            manager.SetSelection(new RegionSelection
            {
                selectionId = selection.selectionId,
                center = selection.center,
                size = updatedSize,
                rotation = selection.rotation
            });
            EditorUtility.SetDirty(manager);
            SceneView.RepaintAll();
        }

        EditorGUI.BeginChangeCheck();
        Vector3 updatedCenter = Handles.PositionHandle(selection.center, selection.rotation);
        if (!EditorGUI.EndChangeCheck())
            return;

        Undo.RecordObject(manager, "Move Region Selection");
        manager.SetSelection(new RegionSelection
        {
            selectionId = selection.selectionId,
            center = updatedCenter,
            size = selection.size,
            rotation = selection.rotation
        });
        EditorUtility.SetDirty(manager);
        SceneView.RepaintAll();
    }

    private static Vector3 ClampSize(Vector3 size)
    {
        return new Vector3(
            Mathf.Max(0.01f, Mathf.Abs(size.x)),
            Mathf.Max(0.01f, Mathf.Abs(size.y)),
            Mathf.Max(0.01f, Mathf.Abs(size.z)));
    }

    private static void DrawSelectionOverlay(RegionSelectionManager manager, RegionSelection selection)
    {
        if (Event.current.type != EventType.Repaint)
            return;

        Material overlayMaterial = GetOverlayMaterial();
        if (overlayMaterial == null)
            return;

        Bounds selectionBounds = RegionSelectionManager.BuildWorldBounds(selection);
        Matrix4x4 worldToSelection = Matrix4x4.TRS(selection.center, selection.rotation, Vector3.one).inverse;

        overlayMaterial.SetMatrix("_SelectionWorldToLocal", worldToSelection);
        overlayMaterial.SetVector("_SelectionHalfExtents", selection.size * 0.5f);
        overlayMaterial.SetColor("_OverlayColor", new Color(0.5f, 0.5f, 0.5f, 0.85f));

        Renderer[] renderers = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (!IsCandidateGeometry(manager, renderer))
                continue;
            if (!renderer.bounds.Intersects(selectionBounds))
                continue;

            MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
            if (meshFilter == null || meshFilter.sharedMesh == null)
                continue;

            Mesh mesh = meshFilter.sharedMesh;
            Matrix4x4 matrix = renderer.localToWorldMatrix;
            int subMeshCount = Mathf.Max(1, mesh.subMeshCount);
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                if (!overlayMaterial.SetPass(0))
                    continue;

                Graphics.DrawMeshNow(mesh, matrix, subMeshIndex);
            }
        }
    }

    private static Material GetOverlayMaterial()
    {
        if (_overlayMaterial != null)
            return _overlayMaterial;

        Shader shader = Shader.Find(OverlayShaderName);
        if (shader == null)
            return null;

        _overlayMaterial = new Material(shader)
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        return _overlayMaterial;
    }

    private static bool IsCandidateGeometry(RegionSelectionManager manager, Renderer renderer)
    {
        if (renderer == null || !renderer.enabled)
            return false;

        GameObject go = renderer.gameObject;
        if (go == null)
            return false;
        if (manager != null && (go == manager.gameObject || go.transform.IsChildOf(manager.transform)))
            return false;
        if ((go.hideFlags & HideFlags.HideInHierarchy) != 0)
            return false;
        if (go.GetComponentInParent<RegionSelectionManager>() != null)
            return false;
        if (go.GetComponentInParent<SpatialProxy>() != null)
            return false;

        Bounds candidateBounds = renderer.bounds;
        return candidateBounds.size.sqrMagnitude > 1e-6f;
    }
}
