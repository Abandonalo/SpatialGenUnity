using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("SpatialGeneration.Tests.EditMode")]

namespace SpatialGeneration.Agent
{
    // The persisted job carries the token; the working hierarchy only exists in this private scene.
    // A stale tool call after a reload or cancellation cannot resolve to the live GeneratedContent.
    internal sealed class RefinementPreviewStage : IDisposable
    {
        private static RefinementPreviewStage _current;
        private readonly Scene _scene;
        private readonly string _token;
        public GameObject Root { get; private set; }

        public static GameObject ForJob(AgentJob job) =>
            job != null && _current != null && job.previewToken == _current._token ? _current.Root : null;

        public RefinementPreviewStage(RefinementReview review, GameObject source, string token)
        {
            if (_current != null) throw new InvalidOperationException("A refinement preview is already running.");
            if (!review.IsSource(source)) throw new InvalidOperationException("Open the original model or start a new preview.");
            _token = token;
            _scene = EditorSceneManager.NewPreviewScene();
            try
            {
                Root = CopyFrame(source.transform).gameObject;
                Root.name = "Refinement working copy";
                foreach (PreviewAsset asset in review.baseline) asset.Create(Root.transform);
                _current = this;
                AssemblyReloadEvents.beforeAssemblyReload += Interrupt;
                EditorApplication.quitting += Interrupt;
            }
            catch { Dispose(); throw; }
        }

        private Transform CopyFrame(Transform source)
        {
            Transform parent = source.parent != null ? CopyFrame(source.parent) : null;
            var frame = new GameObject("Preview frame");
            SceneManager.MoveGameObjectToScene(frame, _scene);
            frame.transform.SetParent(parent, false);
            frame.transform.SetLocalPositionAndRotation(source.localPosition, source.localRotation);
            frame.transform.localScale = source.localScale;
            return frame.transform;
        }

        private void Interrupt()
        {
            AgentProcessRunner.Cancel();
            var job = AgentJobState.instance.current;
            if (job != null && job.previewToken == _token)
            {
                job.running = false;
                AgentJobState.instance.Persist();
            }
            Dispose();
        }

        [InitializeOnLoadMethod]
        private static void RecoverInterruptedPreview()
        {
            EditorApplication.delayCall += () =>
            {
                var job = AgentJobState.instance.current;
                if (job?.running != true || string.IsNullOrEmpty(job.previewToken) || ForJob(job) != null) return;
                job.running = false;
                AgentJobState.instance.Persist();
            };
        }

        public void Dispose()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= Interrupt;
            EditorApplication.quitting -= Interrupt;
            if (_current == this) _current = null;
            Root = null;
            if (_scene.IsValid() && _scene.isLoaded) EditorSceneManager.ClosePreviewScene(_scene);
        }
    }

    // Preview operations must never add to the participant's Undo history.
    internal static class AgentObjectUndo
    {
        public static void Created(GameObject go, string label)
        {
            if (!EditorSceneManager.IsPreviewSceneObject(go)) Undo.RegisterCreatedObjectUndo(go, label);
        }

        public static void Record(AgentPart part, string label)
        {
            if (!EditorSceneManager.IsPreviewSceneObject(part))
                Undo.RegisterCompleteObjectUndo(new UnityEngine.Object[] { part, part.transform, part.gameObject }, label);
        }

        public static void Destroy(GameObject go)
        {
            if (EditorSceneManager.IsPreviewSceneObject(go)) UnityEngine.Object.DestroyImmediate(go);
            else Undo.DestroyObjectImmediate(go);
        }
    }
}
