using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using SpatialGeneration.Authoring;

namespace SpatialGeneration.Agent
{
    // Owned by the Spatial Generation window so selecting mesh geometry never transfers
    // Unity's active selection away from the refinement interaction.
    public sealed class RefinementSceneInteraction
    {
        private static readonly Color Tint = new(0.2f, 0.95f, 0.7f, 1f);
        private SceneView _view;
        private int _control;
        private Vector2 _start;
        private AgentPart _pressed;
        private bool _dragging;
        private RegionSelection _preview;

        public void Cancel()
        {
            if (_view != null && GUIUtility.hotControl == _control) GUIUtility.hotControl = 0;
            _view = null;
            _preview = null;
            _pressed = null;
            _dragging = false;
        }

        public void Draw(SceneView view)
        {
            var session = StudySession.instance;
            if (BackendRegistry.Settings.backendKind != BackendKind.Agent || session.phase != StudyPhase.Refinement ||
                session.condition != StudyCondition.SpatialControls || !StudyController.CanEdit ||
                EditorApplication.isPlayingOrWillChangePlaymode)
            {
                if (_view == view) Cancel();
                return;
            }
            var manager = Object.FindFirstObjectByType<RegionSelectionManager>();
            if (manager != null) DrawSelection(view, manager, AgentSceneOps.AllAssets());
        }

        public void DrawSelection(SceneView view, RegionSelectionManager manager, List<AgentAsset> assets)
        {
            var current = manager.CurrentSelection;
            var owner = assets.Find(a => a.assetId == current?.assetId);
            var evt = Event.current;
            int id = GUIUtility.GetControlID("RefinementSelection".GetHashCode(), FocusType.Passive);
            if (_view == view && (evt.type == EventType.Ignore ||
                evt.type == EventType.KeyDown && evt.keyCode == KeyCode.Escape))
            {
                Cancel();
                if (evt.type == EventType.KeyDown) evt.Use();
                view.Repaint();
                return;
            }

            RegionSelection visible = _view == view && _preview != null ? _preview : current;
            if (visible != null && visible.partScoped && evt.type == EventType.Repaint)
            {
                RegionSelectionManagerEditor.DrawOverlay(manager, visible);
                using (new Handles.DrawingScope(Tint, Matrix4x4.TRS(visible.center, visible.rotation, Vector3.one)))
                    Handles.DrawWireCube(Vector3.zero, visible.size);
            }
            if (evt.alt || Tools.current == Tool.View)
            {
                if (_view == view) Cancel();
                return;
            }
            if (current?.partScoped == true && !_dragging)
                DrawHandles(manager, current, owner, view);

            if (evt.type == EventType.Layout) HandleUtility.AddDefaultControl(id);
            if (evt.type == EventType.MouseDown && evt.button == 0 && HandleUtility.nearestControl == id &&
                GUIUtility.hotControl == 0)
            {
                _pressed = Pick(evt.mousePosition, assets);
                _start = evt.mousePosition;
                _control = id;
                _view = view;
                GUIUtility.hotControl = id;
                evt.Use();
                return;
            }
            if (_view != view || GUIUtility.hotControl != _control) return;
            if (evt.type == EventType.MouseDrag || (evt.type == EventType.MouseUp && evt.button == 0))
            {
                _dragging |= Vector2.Distance(_start, evt.mousePosition) >= 5f;
                if (_dragging)
                {
                    AgentAsset target = _pressed != null ? _pressed.GetComponentInParent<AgentAsset>() : owner;
                    target ??= FindDrawOwner(assets, _start, evt.mousePosition, view);
                    var bounds = DrawnBox(view, _start, evt.mousePosition, target);
                    _preview = bounds != null ? RefinementPartSelection.Draw(bounds, target, evt.type == EventType.MouseUp) : null;
                }
                if (evt.type == EventType.MouseUp)
                {
                    if (_dragging && _preview != null)
                        RefinementPartSelection.Commit(manager, _preview, "Draw refinement region");
                    else if (!_dragging && _pressed != null)
                    {
                        var chosen = RefinementPartSelection.Pick(current, _pressed.GetComponentInParent<AgentAsset>(), _pressed, evt.shift);
                        if (!ReferenceEquals(chosen, current)) RefinementPartSelection.Commit(manager, chosen, "Select refinement part");
                    }
                    Cancel();
                }
                evt.Use();
                view.Repaint();
            }
        }

        private static AgentPart Pick(Vector2 mouse, List<AgentAsset> assets)
        {
            var candidates = new List<GameObject>();
            foreach (AgentAsset asset in assets)
            foreach (AgentPart part in AgentSceneOps.PartsOf(asset))
                if (part.isActiveAndEnabled) candidates.Add(part.gameObject);
            if (candidates.Count == 0) return null;
            var picked = HandleUtility.PickGameObject(mouse, false, null, candidates.ToArray());
            return picked != null ? picked.GetComponent<AgentPart>() : null;
        }

        // If the drag starts in empty space, use the closest object it encloses on screen.
        private static AgentAsset FindDrawOwner(List<AgentAsset> assets, Vector2 start, Vector2 end, SceneView view)
        {
            var rectangle = Rect.MinMaxRect(Mathf.Min(start.x,end.x), Mathf.Min(start.y,end.y),
                Mathf.Max(start.x,end.x), Mathf.Max(start.y,end.y));
            AgentAsset best = null;
            float closest = float.PositiveInfinity;
            foreach (AgentAsset asset in assets)
            foreach (AgentPart part in AgentSceneOps.PartsOf(asset))
            {
                var bounds = part.GetComponent<Renderer>().bounds;
                float depth = view.camera.WorldToViewportPoint(bounds.center).z;
                if (depth <= 0 || depth >= closest) continue;
                var region = new RegionSelection { center = bounds.center, size = bounds.size };
                Vector2 min = Vector2.positiveInfinity, max = Vector2.negativeInfinity;
                foreach (Vector3 corner in region.GetWorldCorners())
                {
                    Vector2 p = HandleUtility.WorldToGUIPoint(corner);
                    min = Vector2.Min(min, p);
                    max = Vector2.Max(max, p);
                }
                if (!rectangle.Overlaps(Rect.MinMaxRect(min.x,min.y,max.x,max.y))) continue;
                best = asset;
                closest = depth;
            }
            return best;
        }

        private static RegionSelection DrawnBox(SceneView view, Vector2 start, Vector2 end, AgentAsset owner)
        {
            Quaternion rotation = view.camera.transform.rotation;
            Quaternion inverse = Quaternion.Inverse(rotation);
            Vector3 pivot = view.pivot;
            float depth = Mathf.Max(0.1f, view.size * 0.15f);
            if (owner != null)
            {
                Bounds bounds = default;
                bool initialized = false;
                foreach (AgentPart part in AgentSceneOps.PartsOf(owner))
                {
                    var world = part.GetComponent<Renderer>().bounds;
                    if (initialized) bounds.Encapsulate(world);
                    else { bounds = world; initialized = true; }
                }
                if (initialized)
                {
                    pivot = bounds.center;
                    Vector3 f = view.camera.transform.forward;
                    depth = Vector3.Dot(bounds.size, new Vector3(Mathf.Abs(f.x),Mathf.Abs(f.y),Mathf.Abs(f.z)));
                }
            }
            var plane = new Plane(view.camera.transform.forward, pivot);
            Ray first = HandleUtility.GUIPointToWorldRay(start), last = HandleUtility.GUIPointToWorldRay(end);
            if (!plane.Raycast(first, out float t0) || !plane.Raycast(last, out float t1)) return null;
            Vector3 a = inverse * first.GetPoint(t0), b = inverse * last.GetPoint(t1);
            return new RegionSelection { center = rotation * ((a+b)*0.5f), rotation = rotation,
                size = RegionSelection.ClampSize(new Vector3(b.x-a.x,b.y-a.y,depth)) };
        }

        private static void DrawHandles(RegionSelectionManager manager, RegionSelection box, AgentAsset owner, SceneView view)
        {
            Vector3 center = box.center, size = box.size;
            Quaternion rotation = box.rotation;
            EditorGUI.BeginChangeCheck();
            using (new Handles.DrawingScope(Tint))
            {
                for (int axis = 0; axis < 3; axis++)
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    Vector3 local = Vector3.zero;
                    local[axis] = sign;
                    Vector3 normal = rotation * local;
                    Vector3 face = box.center + normal * (box.size[axis] * 0.5f);
                    int faceId = GUIUtility.GetControlID(FocusType.Passive);
                    if (Mathf.Abs(Vector3.Dot(normal, view.camera.transform.forward)) > 0.99f) continue;
                    Vector3 point = Handles.Slider(faceId, face, normal, 0.055f * HandleUtility.GetHandleSize(face),
                        Handles.CubeHandleCap, 0f);
                    float delta = Mathf.Max(0.01f - box.size[axis], Vector3.Dot(point - face, normal));
                    size[axis] += delta;
                    center += normal * (delta * 0.5f);
                }
                if (Tools.current == Tool.Rotate)
                    rotation = Handles.RotationHandle(rotation, center);
                else
                {
                    Vector3 corner = center + rotation * (size * 0.5f);
                    center += Handles.PositionHandle(corner, rotation) - corner;
                }
            }
            if (EditorGUI.EndChangeCheck())
                RefinementPartSelection.Commit(manager, RefinementPartSelection.Adjust(box, center, rotation, size, owner),
                    "Adjust refinement region");
        }
    }
}
