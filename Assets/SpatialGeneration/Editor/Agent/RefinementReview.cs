using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SpatialGeneration.Agent
{
    // Serializable authoring data, including the clipped cells on partially edited parts.
    // Unity's transient Mesh and Material objects are rebuilt from this data.
    [Serializable]
    public class PreviewNode
    {
        public string name, data;
        public bool active;
        public Vector3 position, scale;
        public Quaternion rotation;

        protected void Read(Component component)
        {
            Transform t = component.transform;
            name = t.name;
            active = t.gameObject.activeSelf;
            position = t.localPosition;
            rotation = t.localRotation;
            scale = t.localScale;
            data = JsonUtility.ToJson(component);
        }

        public void Restore(Component component)
        {
            Transform t = component.transform;
            t.name = name;
            t.SetLocalPositionAndRotation(position, rotation);
            t.localScale = scale;
            JsonUtility.FromJsonOverwrite(data, component);
            t.gameObject.SetActive(active);
        }

        protected static GameObject Child(Transform parent, string name)
        {
            var go = new GameObject(name);
            if (go.scene != parent.gameObject.scene) SceneManager.MoveGameObjectToScene(go, parent.gameObject.scene);
            go.transform.SetParent(parent, false);
            return go;
        }
    }

    [Serializable]
    public sealed class PreviewPart : PreviewNode
    {
        public string id;
        public bool rendererEnabled;
        public static PreviewPart Capture(AgentPart part)
        {
            var item = new PreviewPart { id = part.partId, rendererEnabled = part.GetComponent<MeshRenderer>().enabled };
            item.Read(part);
            return item;
        }

        public AgentPart Create(Transform parent)
        {
            var part = Child(parent, name).AddComponent<AgentPart>();
            RestorePart(part);
            return part;
        }

        public void RestorePart(AgentPart part)
        {
            Restore(part);
            part.Rebuild();
            part.GetComponent<MeshRenderer>().enabled = rendererEnabled;
        }
    }

    [Serializable]
    public sealed class PreviewAsset : PreviewNode
    {
        public string id;
        public List<PreviewPart> parts = new();

        public static PreviewAsset Capture(AgentAsset asset)
        {
            var item = new PreviewAsset { id = asset.assetId };
            item.Read(asset);
            foreach (AgentPart part in AgentSceneOps.PartsOf(asset)) item.parts.Add(PreviewPart.Capture(part));
            return item;
        }

        public AgentAsset Create(Transform parent)
        {
            var asset = Child(parent, name).AddComponent<AgentAsset>();
            Restore(asset);
            foreach (PreviewPart part in parts) part.Create(asset.transform);
            return asset;
        }

        public void ApplyParts(AgentAsset asset)
        {
            var existing = AgentSceneOps.PartsOf(asset).ToDictionary(p => p.partId);
            foreach (PreviewPart item in parts)
            {
                if (!existing.TryGetValue(item.id, out AgentPart part))
                {
                    Undo.RegisterCreatedObjectUndo(item.Create(asset.transform).gameObject, "Add refined part");
                    continue;
                }
                existing.Remove(item.id);
                if (Same(PreviewPart.Capture(part), item)) continue;
                Undo.RegisterCompleteObjectUndo(new UnityEngine.Object[] { part, part.transform, part.gameObject }, "Refine part");
                item.RestorePart(part);
                EditorUtility.SetDirty(part);
            }
            foreach (AgentPart removed in existing.Values) Undo.DestroyObjectImmediate(removed.gameObject);
        }

        public static bool Same(PreviewNode first, PreviewNode second) =>
            first != null && second != null && JsonUtility.ToJson(first) == JsonUtility.ToJson(second);
    }

    [Serializable]
    public sealed class RefinementIdea
    {
        public string jobId, summary;
        // Every changed object is stored in full, including its untouched parts for rendering.
        public List<PreviewAsset> objects = new();
    }

    [Serializable]
    public sealed class RefinementReview
    {
        public string id, request, style, sourceScene, sourceGlobalId;
        public int sourceInstanceId;
        public StudyCondition condition;
        public RegionSelection selection;
        public Matrix4x4 sourceMatrix;
        public List<PreviewAsset> baseline = new();
        public List<RefinementIdea> ideas = new();
        public int selected;

        public static RefinementReview Begin(GameObject source, string request, string style, StudyCondition condition, RegionSelection region)
        {
            if (source == null) throw new InvalidOperationException("There is no generated model to preview.");
            var review = new RefinementReview {
                id = Guid.NewGuid().ToString("N"), request = request, style = style, condition = condition,
                selection = region?.Clone(), sourceScene = source.scene.path, sourceInstanceId = source.GetInstanceID(),
                sourceGlobalId = GlobalObjectId.GetGlobalObjectIdSlow(source).ToString(),
                sourceMatrix = source.transform.localToWorldMatrix
            };
            foreach (AgentAsset asset in source.GetComponentsInChildren<AgentAsset>())
                review.baseline.Add(PreviewAsset.Capture(asset));
            if (review.baseline.Count == 0) throw new InvalidOperationException("There are no generated objects to preview.");
            return review;
        }

        public bool IsSource(GameObject source) =>
            source != null && source.scene.path == sourceScene && source.transform.localToWorldMatrix == sourceMatrix &&
            (source.GetInstanceID() == sourceInstanceId || (!string.IsNullOrEmpty(sourceScene) &&
              GlobalObjectId.GetGlobalObjectIdSlow(source).ToString() == sourceGlobalId));

        public RefinementIdea CaptureIdea(GameObject staged, string jobId, string summary)
        {
            var result = new RefinementIdea { jobId = jobId, summary = summary };
            foreach (AgentAsset asset in staged.GetComponentsInChildren<AgentAsset>())
            {
                PreviewAsset updated = PreviewAsset.Capture(asset);
                if (!PreviewAsset.Same(baseline.Find(a => a.id == updated.id), updated)) result.objects.Add(updated);
            }
            return result;
        }

        public void Apply(GameObject source, RegionSelectionManager manager = null)
        {
            if (!IsSource(source)) throw new InvalidOperationException("The source model has changed. Start a new preview.");
            var idea = ideas[selected];
            var live = source.GetComponentsInChildren<AgentAsset>().ToDictionary(a => a.assetId);
            // Validate every touched object before the first write. Other objects can change independently.
            foreach (PreviewAsset candidate in idea.objects)
            {
                PreviewAsset original = baseline.Find(a => a.id == candidate.id);
                live.TryGetValue(candidate.id, out AgentAsset current);
                if (original == null ? current != null : current == null || !PreviewAsset.Same(original, PreviewAsset.Capture(current)))
                    throw new InvalidOperationException("This object changed after the preview was made. Discard the ideas and preview it again.");
            }

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            try
            {
                Undo.SetCurrentGroupName("Apply refinement preview");
                foreach (PreviewAsset candidate in idea.objects)
                {
                    if (live.TryGetValue(candidate.id, out AgentAsset current)) candidate.ApplyParts(current);
                    else Undo.RegisterCreatedObjectUndo(candidate.Create(source.transform).gameObject, "Add refined object");
                }
                if (manager != null && selection?.partScoped == true)
                {
                    Undo.RegisterCompleteObjectUndo(manager, "Update refinement selection");
                    var owner = source.GetComponentsInChildren<AgentAsset>().FirstOrDefault(a => a.assetId == selection.assetId);
                    manager.SetSelection(RefinementPartSelection.AfterEdit(selection, owner));
                    EditorUtility.SetDirty(manager);
                }
                Undo.CollapseUndoOperations(group);
                EditorSceneManager.MarkSceneDirty(source.scene);
                SceneView.RepaintAll();
            }
            catch
            {
                Undo.RevertAllDownToGroup(group);
                throw;
            }
        }
    }
}
