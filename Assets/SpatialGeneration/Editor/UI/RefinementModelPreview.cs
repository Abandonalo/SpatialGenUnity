using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SpatialGeneration.Agent
{
    // The preview camera sees only the completed model in its own preview scene.
    internal sealed class RefinementModelPreview : IDisposable
    {
        private PreviewRenderUtility _renderer;
        private RefinementIdea _shown;
        private string _reviewId;
        private Bounds _bounds;
        private bool _empty;
        private Vector2 _orbit;
        private float _zoom;

        internal void Show(RefinementReview review, RefinementIdea idea)
        {
            if (ReferenceEquals(idea, _shown)) return;
            Dispose();
            if (_reviewId != review.id)
            {
                _reviewId = review.id;
                _zoom = 1f;
                Vector3 angles = SceneView.lastActiveSceneView != null
                    ? SceneView.lastActiveSceneView.rotation.eulerAngles : new Vector3(18, -35, 0);
                _orbit = new Vector2(angles.y, Mathf.DeltaAngle(0f, angles.x));
            }
            _renderer = new PreviewRenderUtility();
            _renderer.camera.clearFlags = CameraClearFlags.SolidColor;
            _renderer.camera.backgroundColor = EditorGUIUtility.isProSkin ? new Color(.18f,.19f,.2f) : new Color(.78f,.79f,.80f);
            _renderer.ambientColor = new Color(.45f,.45f,.45f);
            _renderer.lights[0].intensity = 1.15f;
            _renderer.lights[0].transform.rotation = Quaternion.Euler(40,-32,0);
            _renderer.lights[1].intensity = .65f;
            _renderer.lights[1].transform.rotation = Quaternion.Euler(20,145,0);

            var root = new GameObject("Refinement model preview");
            _renderer.AddSingleGO(root);
            root.transform.rotation = review.sourceMatrix.rotation;
            root.transform.localScale = review.sourceMatrix.lossyScale;
            foreach (PreviewAsset asset in idea.objects) asset.Create(root.transform);
            Renderer[] surfaces = root.GetComponentsInChildren<Renderer>().Where(r => r.enabled).ToArray();
            _empty = surfaces.Length == 0;
            if (!_empty)
            {
                _bounds = surfaces[0].bounds;
                foreach (Renderer surface in surfaces) _bounds.Encapsulate(surface.bounds);
            }
            _shown = idea;
        }

        public void Draw(RefinementReview review)
        {
            var idea = review.ideas[review.selected];
            Rect rect = GUILayoutUtility.GetRect(100f, 230f, GUILayout.ExpandWidth(true));
            int control = GUIUtility.GetControlID("OrbitRefinementPreview".GetHashCode(), FocusType.Passive, rect);
            Event evt = Event.current;
            if (evt.type == EventType.Repaint || _shown != null) Show(review, idea);
            if (evt.type == EventType.MouseDown && evt.button == 0 && rect.Contains(evt.mousePosition))
            {
                GUIUtility.hotControl = control;
                evt.Use();
            }
            else if (GUIUtility.hotControl == control && evt.type == EventType.MouseDrag)
            {
                _orbit.x += evt.delta.x * .65f;
                _orbit.y = Mathf.Clamp(_orbit.y - evt.delta.y * .65f, -88f, 88f);
                GUI.changed = true;
                evt.Use();
            }
            else if (GUIUtility.hotControl == control && evt.type == EventType.MouseUp)
            {
                GUIUtility.hotControl = 0;
                evt.Use();
            }
            else if (evt.type == EventType.ScrollWheel && rect.Contains(evt.mousePosition))
            {
                _zoom = Mathf.Clamp(_zoom * Mathf.Exp(evt.delta.y * .07f), .25f, 3f);
                GUI.changed = true;
                evt.Use();
            }

            if (evt.type != EventType.Repaint || rect.width < 1) return;
            if (_empty)
            {
                GUI.Box(rect, "This idea removes all geometry from the object.");
                return;
            }
            GUI.DrawTexture(rect, Render(rect), ScaleMode.StretchToFill, false);
        }

        internal Texture Render(Rect rect)
        {
            Camera camera = _renderer.camera;
            float radius = Mathf.Max(.01f, _bounds.extents.magnitude);
            Quaternion rotation = Quaternion.Euler(_orbit.y,_orbit.x,0);
            camera.transform.SetPositionAndRotation(_bounds.center - rotation * Vector3.forward * radius * 3f, rotation);
            camera.orthographic = true;
            camera.orthographicSize = radius * 1.08f * _zoom / Mathf.Min(1f, rect.width / rect.height);
            camera.nearClipPlane = radius * .01f;
            camera.farClipPlane = radius * 6f;
            _renderer.BeginPreview(rect, GUIStyle.none);
            try { _renderer.Render(true); }
            catch { _renderer.EndPreview(); throw; }
            return _renderer.EndPreview();
        }

        public void Dispose()
        {
            _renderer?.Cleanup();
            _renderer = null;
            _shown = null;
        }
    }
}
