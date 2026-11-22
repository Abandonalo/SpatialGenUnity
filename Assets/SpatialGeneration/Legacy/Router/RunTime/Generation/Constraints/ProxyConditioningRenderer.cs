using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using SpatialGeneration.Utils;

namespace SpatialGeneration.Generation.Intent
{
    /// <summary>Conditioning images for one occupy proxy. Caller owns the textures.</summary>
    public sealed class ProxyConditioning : IDisposable
    {
        /// <summary>Linear depth of the proxy volume, black background.</summary>
        public Texture2D Depth;

        /// <summary>Sobel edges of <see cref="Depth"/>.</summary>
        public Texture2D Edges;

        /// <summary>Binary silhouette of the proxy volume.</summary>
        public Texture2D Mask;

        public void Dispose() => TextureUtils.Destroy(Depth, Edges, Mask);
    }

    /// <summary>
    /// Renders a proxy's volume into the conditioning images that steer generation.
    ///
    /// This is where the thesis' spatial control actually enters the model: the primitive the
    /// user placed becomes a depth map and an edge map, which the backend feeds to ControlNet.
    /// The camera is framed on the proxy so the model sees one isolated object, which is also
    /// what the image-to-3D lifter downstream needs.
    /// </summary>
    public static class ProxyConditioningRenderer
    {
        private const string DepthShaderName = "Hidden/SpatialGen/EncodeLinearDepth";
        private const string ConditioningLayerName = "ConstraintProxyLayer";
        private const int FallbackLayer = 31;

        /// <summary>
        /// Margin around the proxy in frame. Small on purpose: the prompt asks for the whole
        /// object in shot, and every pixel of padding is resolution the lifter does not get.
        /// </summary>
        private const float FramingPadding = 1.1f;

        /// <summary>
        /// Renders <paramref name="proxy"/> from a canonical front view of its own volume.
        ///
        /// Deliberately independent of the scene camera. The depth map drives ControlNet, so
        /// its viewing angle becomes the angle of the generated image — and the lifter that
        /// consumes that image reconstructs a front elevation, which is also the pose
        /// placement assumes when it seats the mesh on the proxy. Rendering from wherever
        /// the user's viewport happened to be would fight all three. Keeping it canonical
        /// also makes a run reproducible from the proxies alone.
        /// </summary>
        public static ProxyConditioning Render(ProxyIntent proxy, int width, int height)
        {
            if (proxy == null)
                throw new ArgumentNullException(nameof(proxy));

            Shader depthShader = Shader.Find(DepthShaderName);
            if (depthShader == null)
                throw new InvalidOperationException($"Depth shader '{DepthShaderName}' not found.");

            int safeWidth = Mathf.Max(64, width);
            int safeHeight = Mathf.Max(64, height);

            Vector3 position = ToVector3(proxy.Pose?.Position, Vector3.zero);
            Quaternion rotation = ToQuaternion(proxy.Pose?.Rotation, Quaternion.identity);
            Vector3 size = ClampSize(ToVector3(proxy.Pose?.Scale, Vector3.one));

            GameObject volume = null;
            GameObject cameraObject = null;
            Material depthMaterial = null;
            Material whiteMaterial = null;
            Texture2D depth = null;
            Texture2D mask = null;

            try
            {
                int layer = ResolveLayer();
                volume = CreateVolume(proxy, position, rotation, size, layer);

                cameraObject = CreateFramingCamera(rotation, position, size, layer);
                Camera camera = cameraObject.GetComponent<Camera>();

                depthMaterial = new Material(depthShader) { hideFlags = HideFlags.HideAndDontSave };
                depthMaterial.SetFloat("_MaxDepth", Mathf.Max(0.01f, camera.farClipPlane));
                Shader.SetGlobalFloat("_MaxDepth", Mathf.Max(0.01f, camera.farClipPlane));
                depth = RenderWithMaterial(camera, volume, depthMaterial, safeWidth, safeHeight);

                whiteMaterial = CreateUnlitWhiteMaterial();
                mask = RenderWithMaterial(camera, volume, whiteMaterial, safeWidth, safeHeight);
                TextureUtils.Binarize(mask);

                return new ProxyConditioning
                {
                    Depth = depth,
                    Edges = TextureUtils.BuildEdgeMap(depth),
                    Mask = mask
                };
            }
            catch
            {
                TextureUtils.Destroy(depth, mask);
                throw;
            }
            finally
            {
                DestroyIfPresent(volume);
                DestroyIfPresent(cameraObject);
                if (depthMaterial != null) UnityEngine.Object.DestroyImmediate(depthMaterial);
                if (whiteMaterial != null) UnityEngine.Object.DestroyImmediate(whiteMaterial);
            }
        }

        /// <summary>
        /// Off-screen camera on the proxy's front axis, looking back at it.
        ///
        /// "Front" is the proxy's local +X, the same convention
        /// <see cref="MultiViewCameraManager"/> uses for its Front view and that placement
        /// corrects for when it seats the imported mesh.
        /// </summary>
        private static GameObject CreateFramingCamera(Quaternion proxyRotation, Vector3 center, Vector3 size, int layer)
        {
            var go = new GameObject("SpatialGen_ConditioningCamera") { hideFlags = HideFlags.HideAndDontSave };
            Camera camera = go.AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.cullingMask = 1 << layer;
            camera.allowHDR = false;
            camera.allowMSAA = false;

            // Frame on the face we are looking at, not the volume's diagonal: the diagonal
            // is up to 1.7x the visible height, which would shrink the object in frame and
            // waste most of the pixels the lifter gets to work with.
            Vector3 front = proxyRotation * Vector3.right;
            Vector3 up = proxyRotation * Vector3.up;
            float visibleHalfHeight = 0.5f * Mathf.Max(size.y, Mathf.Max(size.x, size.z));
            float distance = Mathf.Max(1f, size.magnitude * 2f);

            camera.orthographicSize = Mathf.Max(0.05f, visibleHalfHeight * FramingPadding);
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = distance + size.magnitude * 2f;
            go.transform.SetPositionAndRotation(
                center + front * distance,
                Quaternion.LookRotation(-front, up));

            return go;
        }

        private static GameObject CreateVolume(
            ProxyIntent proxy, Vector3 position, Quaternion rotation, Vector3 size, int layer)
        {
            PrimitiveType primitive = proxy.Shape switch
            {
                ProxyShape.Sphere => PrimitiveType.Sphere,
                ProxyShape.Cylinder => PrimitiveType.Cylinder,
                _ => PrimitiveType.Cube
            };

            GameObject go = GameObject.CreatePrimitive(primitive);
            go.name = $"SpatialGen_Conditioning_{proxy.Id}";
            go.hideFlags = HideFlags.HideAndDontSave;
            go.layer = layer;
            go.transform.SetPositionAndRotation(position, rotation);

            // Unity's cylinder mesh is two units tall, so height must be halved to match the
            // authored size the user sees on the proxy gizmo.
            go.transform.localScale = primitive == PrimitiveType.Cylinder
                ? new Vector3(size.x, size.y * 0.5f, size.z)
                : size;

            if (go.TryGetComponent(out Collider collider))
                UnityEngine.Object.DestroyImmediate(collider);

            if (go.TryGetComponent(out MeshRenderer renderer))
            {
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }

            return go;
        }

        private static Texture2D RenderWithMaterial(
            Camera camera, GameObject volume, Material material, int width, int height)
        {
            var renderer = volume.GetComponent<MeshRenderer>();
            Material previous = renderer != null ? renderer.sharedMaterial : null;
            RenderTexture target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            target.filterMode = FilterMode.Point;

            try
            {
                if (renderer != null)
                    renderer.sharedMaterial = material;

                camera.targetTexture = target;
                camera.Render();
                return TextureUtils.ReadPixels(target);
            }
            finally
            {
                camera.targetTexture = null;
                if (renderer != null)
                    renderer.sharedMaterial = previous;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        private static Material CreateUnlitWhiteMaterial()
        {
            Shader shader = Shader.Find("Unlit/Color")
                            ?? Shader.Find("Universal Render Pipeline/Unlit")
                            ?? Shader.Find("Sprites/Default");
            if (shader == null)
                throw new InvalidOperationException("No unlit shader available for the proxy mask pass.");

            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, color = Color.white };
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", Color.white);
            return material;
        }

        private static int ResolveLayer()
        {
            int named = LayerMask.NameToLayer(ConditioningLayerName);
            return named >= 0 ? named : FallbackLayer;
        }

        private static void DestroyIfPresent(GameObject go)
        {
            if (go != null)
                UnityEngine.Object.DestroyImmediate(go);
        }

        private static Vector3 ClampSize(Vector3 size) => new(
            Mathf.Max(0.01f, Mathf.Abs(size.x)),
            Mathf.Max(0.01f, Mathf.Abs(size.y)),
            Mathf.Max(0.01f, Mathf.Abs(size.z)));

        private static Vector3 ToVector3(Vector3Data value, Vector3 fallback) =>
            value == null ? fallback : new Vector3(value.X, value.Y, value.Z);

        private static Quaternion ToQuaternion(QuaternionData value, Quaternion fallback) =>
            value == null ? fallback : new Quaternion(value.X, value.Y, value.Z, value.W);
    }
}
