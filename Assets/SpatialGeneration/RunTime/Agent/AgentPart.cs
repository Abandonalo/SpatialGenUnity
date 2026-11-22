using UnityEngine;
using System.Collections.Generic;

namespace SpatialGeneration.Agent
{
    /// <summary>
    /// One primitive of an agent-built asset. The transform carries placement (local position,
    /// rotation, and size as local scale); this component carries what the transform cannot:
    /// the shape, its segment count and the flat colour. The mesh is rebuilt from these on
    /// enable, so nothing generated is stored in the scene file.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    [AddComponentMenu("Spatial Generation/Agent Part")]
    public class AgentPart : MonoBehaviour
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        public string partId;
        public AgentPartShape shape = AgentPartShape.Box;
        public int segments = 8;
        public Color color = Color.white;
        [HideInInspector] public List<PartCell> fragments = new();
        private Mesh _fragmentMesh;

        /// <summary>Job that last created or changed this part, for the interaction log.</summary>
        public string lastJobId;

        /// <summary>
        /// For the unselected remainder split off a partially edited part: the part it came from.
        /// Scoring treats such a remainder as preserved material rather than new geometry.
        /// </summary>
        public string sourcePartId;

        private void OnEnable() => Rebuild();

#if UNITY_EDITOR
        private void OnValidate()
        {
            segments = LowPolyMeshes.ClampSegments(segments);
            // Assigning meshes inside OnValidate triggers SendMessage warnings; defer it.
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this != null && isActiveAndEnabled)
                    Rebuild();
            };
        }
#endif

        public void Rebuild()
        {
            MeshFilter filter = GetComponent<MeshFilter>();
            MeshRenderer meshRenderer = GetComponent<MeshRenderer>();
            if (filter == null || meshRenderer == null)
                return;

            ReleaseFragmentMesh();
            if (fragments != null && fragments.Count > 0)
                _fragmentMesh = PartGeometry.BuildMesh(fragments);
            filter.sharedMesh = _fragmentMesh != null ? _fragmentMesh : LowPolyMeshes.Get(shape, segments);
            meshRenderer.sharedMaterial = LowPolyMeshes.SharedMaterial;

            var block = new MaterialPropertyBlock();
            meshRenderer.GetPropertyBlock(block);
            block.SetColor(BaseColorId, color);
            block.SetColor(ColorId, color);
            meshRenderer.SetPropertyBlock(block);
        }

        private void OnDestroy() => ReleaseFragmentMesh();

        private void ReleaseFragmentMesh()
        {
            if (_fragmentMesh == null) return;
            if (Application.isPlaying) Destroy(_fragmentMesh);
            else DestroyImmediate(_fragmentMesh);
            _fragmentMesh = null;
        }
    }
}
