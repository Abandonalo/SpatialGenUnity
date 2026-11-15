using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SpatialGeneration.Agent
{
    public static class RefinementPartSelection
    {
        public const float WholeCoverage = 0.85f;
        public static bool Has(RegionSelection box, string id) => box != null &&
            (box.wholeParts.Contains(id) || box.partialParts.Contains(id) || box.outsideParts.Contains(id));

        public static RegionSelection Pick(RegionSelection previous, AgentAsset asset, AgentPart part, bool toggle)
        {
            bool same = previous != null && previous.assetId == asset.assetId;
            if (toggle && previous?.partScoped == true && !same && !string.IsNullOrEmpty(previous.assetId))
                return previous;
            RegionSelection box = toggle && same ? previous.Clone() : new RegionSelection();
            box.assetId = asset.assetId;
            box.partScoped = true;
            if (Has(box, part.partId))
            {
                box.wholeParts.Remove(part.partId);
                box.partialParts.Remove(part.partId);
                box.outsideParts.Remove(part.partId);
            }
            else box.wholeParts.Add(part.partId);
            if (box.partialParts.Count + box.outsideParts.Count == 0)
            {
                if (box.wholeParts.Count == 0) return null;
                box.hasCrop = false;
                box.rotation = box.wholeParts.Count == 1
                    ? AgentSceneOps.PartsOf(asset).Find(p => p.partId == box.wholeParts[0])?.transform.rotation ?? asset.transform.rotation
                    : asset.transform.rotation;
            }
            else if (!box.hasCrop) StoreCrop(box);
            box.pickedWholeParts = !box.hasCrop;
            Fit(box, asset);
            return box;
        }

        // Resolve once on release. Manual handle edits classify the same IDs against the exact box.
        public static RegionSelection Draw(RegionSelection bounds, AgentAsset owner, bool promote)
        {
            RegionSelection box = bounds.Clone();
            box.partScoped = true;
            box.assetId = owner != null ? owner.assetId : string.Empty;
            if (owner == null) return box;
            foreach (AgentPart part in AgentSceneOps.PartsOf(owner))
                Classify(box, part, promote, false);
            if (promote && box.wholeParts.Count > 0)
            {
                StoreCrop(box);
                Fit(box, owner);
            }
            return box;
        }

        public static RegionSelection Adjust(RegionSelection original, Vector3 center, Quaternion rotation, Vector3 size, AgentAsset owner)
        {
            var box = new RegionSelection { selectionId = original.selectionId, center = center,
                rotation = rotation, size = RegionSelection.ClampSize(size), assetId = original.assetId,
                partScoped = original.partScoped };
            foreach (AgentPart part in AgentSceneOps.PartsOf(owner))
                if (Has(original, part.partId)) Classify(box, part, false, true);
            return box;
        }

        private static void Classify(RegionSelection box, AgentPart part, bool promote, bool keepOutside)
        {
            if (!box.GetWorldBounds().Intersects(part.GetComponent<Renderer>().bounds))
            {
                if (keepOutside) box.outsideParts.Add(part.partId);
                return;
            }
            PartCut cut = PartGeometry.Cut(part, box);
            if (cut.inside.Count == 0 || cut.coverage <= 0)
            {
                if (keepOutside) box.outsideParts.Add(part.partId);
            }
            else if (cut.outside.Count == 0 || (promote && cut.coverage >= WholeCoverage))
                box.wholeParts.Add(part.partId);
            else box.partialParts.Add(part.partId);
        }

        private static void StoreCrop(RegionSelection box)
        {
            box.hasCrop = true;
            box.cropCenter = box.center;
            box.cropSize = box.size;
        }

        // A kept edit may have resized a whole part or removed just the material under
        // the box. Refresh the same targets before accepting another prompt.
        public static RegionSelection AfterEdit(RegionSelection before, AgentAsset asset)
        {
            if (before == null || !before.partScoped || string.IsNullOrEmpty(before.assetId)) return before;
            if (asset == null) return null;
            var box = before.Clone();
            List<AgentPart> parts = AgentSceneOps.PartsOf(asset);
            box.wholeParts.RemoveAll(id => !parts.Exists(p => p.partId == id));
            var crop = before.Crop();
            box.partialParts.Clear();
            box.outsideParts.Clear();
            foreach (AgentPart part in parts)
            {
                if (!before.partialParts.Contains(part.partId) && !before.outsideParts.Contains(part.partId)) continue;
                PartCut cut = PartGeometry.Cut(part, crop);
                if (cut.inside.Count == 0 || cut.coverage <= 0) box.outsideParts.Add(part.partId);
                else if (cut.outside.Count == 0) box.wholeParts.Add(part.partId);
                else box.partialParts.Add(part.partId);
            }
            if (before.wholeParts.Count + before.partialParts.Count + before.outsideParts.Count > 0 &&
                box.wholeParts.Count + box.partialParts.Count + box.outsideParts.Count == 0) return null;
            if (box.pickedWholeParts) Fit(box, asset);
            return box;
        }

        private static void Fit(RegionSelection box, AgentAsset asset)
        {
            Quaternion inverse = Quaternion.Inverse(box.rotation);
            Bounds bounds = default;
            bool started = box.hasCrop;
            if (started) bounds = new Bounds(inverse * box.cropCenter, box.cropSize);
            foreach (AgentPart p in AgentSceneOps.PartsOf(asset))
            {
                if (!box.wholeParts.Contains(p.partId)) continue;
                Matrix4x4 matrix = Matrix4x4.Rotate(inverse) * p.transform.localToWorldMatrix;
                foreach (var v in p.GetComponent<MeshFilter>().sharedMesh.vertices)
                {
                    Vector3 point = matrix.MultiplyPoint3x4(v);
                    if (!started) { bounds = new Bounds(point, Vector3.zero); started = true; }
                    else bounds.Encapsulate(point);
                }
            }
            if (!started) return;
            box.center = box.rotation * bounds.center;
            box.size = RegionSelection.ClampSize(bounds.size);
        }

        public static void Commit(RegionSelectionManager manager, RegionSelection value, string label)
        {
            Undo.RecordObject(manager, label);
            manager.SetSelection(value);
            EditorUtility.SetDirty(manager);
            SceneView.RepaintAll();
        }
    }
}
