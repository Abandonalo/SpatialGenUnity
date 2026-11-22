using UnityEditor;
using UnityEngine;
using SpatialGeneration.Agent;

namespace SpatialGeneration.Authoring
{
    /// <summary>
    /// Scene-view handles for the refinement region, plus a tinted overlay showing which surfaces
    /// fall inside it so the user can judge the edit's reach before running it.
    /// </summary>
    [CustomEditor(typeof(RegionSelectionManager))]
    public class RegionSelectionManagerEditor : Editor
    {
        private const string OverlayShaderName = "Hidden/SpatialGen/RegionSelectionOverlay";
        private static readonly Color HandleColor = new(0.2f, 1f, 0.7f, 1f);

        private static Material _overlayMaterial;
        private bool _toolsWereHidden;

        private void OnEnable()
        {
            _toolsWereHidden = Tools.hidden;
            Tools.hidden = true;
        }

        private void OnDisable()
        {
            Tools.hidden = _toolsWereHidden;
        }

        public override void OnInspectorGUI()
        {
            if (BackendRegistry.Settings.backendKind != BackendKind.Agent) { DrawDefaultInspector(); return; }
            EditorGUILayout.HelpBox("Select parts in the Scene view and enter your refinement prompt in Tools > Spatial Generation.", MessageType.None);
        }

        private void OnSceneGUI()
        {
            var manager = (RegionSelectionManager)target;
            if (BackendRegistry.Settings.backendKind == BackendKind.Agent) return;
            RegionSelection selection = manager.CurrentSelection;
            if (selection == null)
                return;

            DrawOverlay(manager, selection);

            EditorGUI.BeginChangeCheck();
            Handles.color = HandleColor;
            Vector3 size = Handles.ScaleHandle(
                selection.size, selection.center, selection.rotation, HandleUtility.GetHandleSize(selection.center));
            Vector3 center = Handles.PositionHandle(selection.center, selection.rotation);

            if (!EditorGUI.EndChangeCheck())
                return;

            Undo.RecordObject(manager, "Edit Region Selection");
            manager.SetSelection(new RegionSelection
            {
                selectionId = selection.selectionId,
                center = center,
                size = RegionSelection.ClampSize(size),
                rotation = selection.rotation
            });
            EditorUtility.SetDirty(manager);
            SceneView.RepaintAll();
        }

        internal static void DrawOverlay(RegionSelectionManager manager, RegionSelection selection)
        {
            if (Event.current.type != EventType.Repaint)
                return;

            Material overlay = GetOverlayMaterial();
            if (overlay == null)
                return;

            overlay.SetMatrix("_SelectionWorldToLocal", selection.WorldToLocal());
            overlay.SetVector("_SelectionHalfExtents", RegionSelection.ClampSize(selection.size) * 0.5f);
            overlay.SetColor("_OverlayColor", new Color(0.18f, 0.85f, 0.62f, 0.6f));

            Bounds selectionBounds = selection.GetWorldBounds();
            RegionSelection crop = selection.Crop();

            foreach (Renderer renderer in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (!IsCandidate(manager, renderer) || !renderer.bounds.Intersects(selectionBounds))
                    continue;

                if (selection.partScoped)
                {
                    var part = renderer.GetComponent<AgentPart>();
                    if (part == null || part.GetComponentInParent<AgentAsset>()?.assetId != selection.assetId) continue;
                    bool whole = selection.wholeParts.Contains(part.partId);
                    if (!whole && !selection.partialParts.Contains(part.partId)) continue;
                    RegionSelection clippingBox = whole ? selection : crop;
                    overlay.SetMatrix("_SelectionWorldToLocal", clippingBox.WorldToLocal());
                    overlay.SetVector("_SelectionHalfExtents", clippingBox.size * 0.5f);
                }

                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null)
                    continue;

                Mesh mesh = filter.sharedMesh;
                for (int subMesh = 0; subMesh < Mathf.Max(1, mesh.subMeshCount); subMesh++)
                {
                    if (overlay.SetPass(0))
                        Graphics.DrawMeshNow(mesh, renderer.localToWorldMatrix, subMesh);
                }
            }
        }

        private static bool IsCandidate(RegionSelectionManager manager, Renderer renderer)
        {
            if (renderer == null || !renderer.enabled)
                return false;

            GameObject go = renderer.gameObject;
            if (manager != null && (go == manager.gameObject || go.transform.IsChildOf(manager.transform)))
                return false;
            if ((go.hideFlags & HideFlags.HideInHierarchy) != 0)
                return false;
            if (go.GetComponentInParent<SpatialProxy>() != null)
                return false;

            return renderer.bounds.size.sqrMagnitude > 1e-6f;
        }

        private static Material GetOverlayMaterial()
        {
            if (_overlayMaterial != null)
                return _overlayMaterial;

            Shader shader = Shader.Find(OverlayShaderName);
            if (shader == null)
                return null;

            _overlayMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return _overlayMaterial;
        }
    }
}
