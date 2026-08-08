using UnityEngine;

namespace SpatialGeneration.Generation
{
    /// <summary>
    /// Seats a generated mesh inside the volume the user authored.
    ///
    /// Reconstruction models normalise their output — TripoSR returns a mesh whose longest
    /// axis is about 1 unit regardless of the subject's real proportions — so the raw mesh
    /// almost never matches the proxy's aspect ratio. Fitting therefore has to decide
    /// between filling the authored volume and preserving the reconstruction's shape.
    /// </summary>
    public static class MeshFitting
    {
        private const float MinExtent = 1e-4f;

        /// <summary>
        /// Scales <paramref name="target"/> to occupy <paramref name="targetSize"/> and centres
        /// it on <paramref name="targetCenter"/>.
        /// </summary>
        /// <param name="preserveProportions">
        /// When true, scales uniformly so the mesh keeps its own shape and fits inside the
        /// volume — which leaves it visibly smaller than the proxy whenever the proportions
        /// disagree. When false (the default) each axis is scaled to fill the volume: the
        /// proxy is the user's stated intent for how much space the asset should take, and
        /// matching it is what the greybox workflow is for.
        /// </param>
        public static void FitToVolume(
            GameObject target,
            Vector3 targetSize,
            Vector3 targetCenter,
            bool preserveProportions)
        {
            if (target == null)
                return;

            if (!TryMeasureAlongOwnAxes(target, out Vector3 currentSize, out _))
                return;

            Vector3 ratios = new(
                Mathf.Max(MinExtent, targetSize.x) / Mathf.Max(MinExtent, currentSize.x),
                Mathf.Max(MinExtent, targetSize.y) / Mathf.Max(MinExtent, currentSize.y),
                Mathf.Max(MinExtent, targetSize.z) / Mathf.Max(MinExtent, currentSize.z));

            if (preserveProportions)
            {
                float uniform = Mathf.Min(ratios.x, Mathf.Min(ratios.y, ratios.z));
                ratios = new Vector3(uniform, uniform, uniform);
            }

            target.transform.localScale = Vector3.Scale(target.transform.localScale, ratios);
            CenterOn(target, targetCenter);
        }

        /// <summary>Moves <paramref name="target"/> so its geometry is centred on <paramref name="center"/>.</summary>
        public static void CenterOn(GameObject target, Vector3 center)
        {
            if (target == null)
                return;

            if (!TryMeasureAlongOwnAxes(target, out _, out Vector3 worldCenter))
            {
                target.transform.position = center;
                return;
            }

            target.transform.position += center - worldCenter;
        }

        /// <summary>
        /// Measures the mesh along the object's own axes, rather than using
        /// <see cref="Renderer.bounds"/>.
        ///
        /// Renderer bounds are a world axis-aligned box, so a rotated asset measures larger
        /// than it is — by up to 73% for a 45-degree turn. Fitting against that number
        /// shrinks rotated assets for no reason.
        /// </summary>
        private static bool TryMeasureAlongOwnAxes(GameObject target, out Vector3 size, out Vector3 worldCenter)
        {
            size = Vector3.zero;
            worldCenter = target.transform.position;

            Quaternion toLocal = Quaternion.Inverse(target.transform.rotation);
            Vector3 min = Vector3.positiveInfinity;
            Vector3 max = Vector3.negativeInfinity;
            bool any = false;

            foreach (MeshFilter filter in target.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null)
                    continue;

                Matrix4x4 toWorld = filter.transform.localToWorldMatrix;
                foreach (Vector3 vertex in mesh.vertices)
                {
                    Vector3 local = toLocal * toWorld.MultiplyPoint3x4(vertex);
                    min = Vector3.Min(min, local);
                    max = Vector3.Max(max, local);
                    any = true;
                }
            }

            if (!any)
                return false;

            size = max - min;
            worldCenter = target.transform.rotation * ((min + max) * 0.5f);
            return true;
        }
    }
}
