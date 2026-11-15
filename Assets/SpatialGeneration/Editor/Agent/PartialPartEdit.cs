using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SpatialGeneration.Agent
{
    // Prepare a slice without changing the scene. A failed operation or read-only agent call
    // must leave the original primitive intact.
    internal sealed class PartialPartEdit
    {
        private readonly PartCut _cut;
        private readonly Bounds _bounds;
        public Vector3 Center { get; }
        public Vector3 Size { get; }

        public static PartialPartEdit Prepare(AgentJob job, AgentPart part)
        {
            if (!job.HasPartScope || !job.selection.partialParts.Contains(part.partId) ||
                job.materializedSlices.Contains(part.partId)) return null;
            return new PartialPartEdit(part, PartGeometry.Cut(part, job.selection.Crop()));
        }

        public bool HasInside => _cut.inside.Count > 0 && _cut.coverage > 0;

        private PartialPartEdit(AgentPart part, PartCut cut)
        {
            _cut = cut;
            if (!HasInside) return;
            _bounds = new Bounds(cut.inside[0].triangles[0], Vector3.zero);
            foreach (PartCell cell in cut.inside)
            foreach (Vector3 v in cell.triangles) _bounds.Encapsulate(v);
            Transform t = part.transform;
            Center = t.localPosition + t.localRotation * Vector3.Scale(t.localScale, _bounds.center);
            Size = Vector3.Scale(t.localScale, _bounds.size);
        }

        public void RetainOutside(AgentPart part)
        {
            if (_cut.outside.Count == 0) { AgentObjectUndo.Destroy(part.gameObject); return; }
            AgentObjectUndo.Record(part, "Remove selected portion");
            part.fragments = _cut.outside;
            part.Rebuild();
            EditorUtility.SetDirty(part);
        }

        public void IsolateInside(AgentPart part)
        {
            if (_cut.outside.Count > 0)
            {
                GameObject remainder = Object.Instantiate(part.gameObject, part.transform.parent, false);
                AgentPart preserved = remainder.GetComponent<AgentPart>();
                preserved.partId = AgentSceneOps.ShortId();
                string name = part.name;
                if (name.EndsWith("_" + part.partId)) name = name.Substring(0, name.Length - part.partId.Length - 1);
                remainder.name = name + "_remainder_" + preserved.partId;
                preserved.fragments = _cut.outside;
                preserved.Rebuild();
                AgentObjectUndo.Created(remainder, "Preserve unselected portion");
            }
            part.fragments = new List<PartCell>(_cut.inside.Count);
            Vector3 size = _bounds.size;
            foreach (PartCell cell in _cut.inside)
            {
                var points = new Vector3[cell.triangles.Length];
                for (int i = 0; i < points.Length; i++)
                {
                    Vector3 p = cell.triangles[i] - _bounds.center;
                    points[i] = new Vector3(p.x / size.x, p.y / size.y, p.z / size.z);
                }
                part.fragments.Add(new PartCell { triangles = points });
            }
        }
    }
}
