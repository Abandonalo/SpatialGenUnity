using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SpatialGeneration.Generation.Intent
{
    public static class ConstraintCompiler
    {
        private const string ConstraintProxyLayerName = "ConstraintProxyLayer";
        private const int FallbackConstraintLayer = 31;

        public static CompiledConstraints Compile(
            SceneIntent sceneIntent,
            ConstraintSet constraintSet,
            Camera captureCamera,
            int width,
            int height)
        {
            if (captureCamera == null)
                throw new ArgumentNullException(nameof(captureCamera));
            if (constraintSet == null)
                throw new ArgumentNullException(nameof(constraintSet));

            HashSet<string> occupyIds = new();
            HashSet<string> avoidIds = new();
            HashSet<string> focusIds = new();

            foreach (Constraint c in constraintSet.Constraints)
            {
                if (c == null || string.IsNullOrEmpty(c.ProxyId))
                    continue;

                switch (c.Type)
                {
                    case ConstraintType.OccupyVolume: occupyIds.Add(c.ProxyId); break;
                    case ConstraintType.KeepEmpty: avoidIds.Add(c.ProxyId); break;
                    case ConstraintType.FocusRegion: focusIds.Add(c.ProxyId); break;
                }
            }

            Texture2D maskOccupy = RenderMask(sceneIntent, captureCamera, width, height, occupyIds);
            Texture2D maskAvoid = RenderMask(sceneIntent, captureCamera, width, height, avoidIds);
            Texture2D maskFocusRaw = RenderMask(sceneIntent, captureCamera, width, height, focusIds, 1.08f);
            Texture2D maskFocus = BoxBlur(maskFocusRaw, width, height, 2);
            if (maskFocusRaw != null) UnityEngine.Object.DestroyImmediate(maskFocusRaw);

            if (string.Equals(constraintSet.ConflictPolicy, "avoid_wins", StringComparison.OrdinalIgnoreCase))
                ApplyAvoidWins(maskOccupy, maskAvoid);

            return new CompiledConstraints
            {
                MaskOccupy = maskOccupy,
                MaskAvoid = maskAvoid,
                MaskFocus = maskFocus,
                ConstraintJson = IntentJson.SerializeConstraintSet(constraintSet)
            };
        }

        public static Texture2D RenderProxyPass(
            SceneIntent sceneIntent,
            Camera sourceCamera,
            int width,
            int height,
            HashSet<string> proxyIds,
            Material material,
            float scaleMultiplier = 1f)
        {
            if (sourceCamera == null) throw new ArgumentNullException(nameof(sourceCamera));
            if (material == null) throw new ArgumentNullException(nameof(material));

            Dictionary<string, ProxyIntent> intentById = BuildIntentLookup(sceneIntent);
            Dictionary<string, SpatialProxy> liveById = BuildLiveProxyLookup();
            HashSet<string> ids = proxyIds ?? new HashSet<string>();

            RenderTexture rt = new(width, height, 24, RenderTextureFormat.ARGB32);
            rt.Create();

            int layer = ResolveConstraintLayer();
            List<GameObject> tempVolumes = CreateTempVolumes(ids, intentById, liveById, layer, material, scaleMultiplier);

            int previousMask = sourceCamera.cullingMask;
            CameraClearFlags previousFlags = sourceCamera.clearFlags;
            Color previousBg = sourceCamera.backgroundColor;
            RenderTexture previousTarget = sourceCamera.targetTexture;
            bool previousAllowMsaa = sourceCamera.allowMSAA;
            bool previousHdr = sourceCamera.allowHDR;

            try
            {
                sourceCamera.cullingMask = 1 << layer;
                sourceCamera.clearFlags = CameraClearFlags.SolidColor;
                sourceCamera.backgroundColor = Color.black;
                sourceCamera.allowMSAA = false;
                sourceCamera.allowHDR = false;
                sourceCamera.targetTexture = rt;
                sourceCamera.Render();
                return ReadTexture(rt, width, height);
            }
            finally
            {
                sourceCamera.cullingMask = previousMask;
                sourceCamera.clearFlags = previousFlags;
                sourceCamera.backgroundColor = previousBg;
                sourceCamera.targetTexture = previousTarget;
                sourceCamera.allowMSAA = previousAllowMsaa;
                sourceCamera.allowHDR = previousHdr;

                for (int i = 0; i < tempVolumes.Count; i++)
                    if (tempVolumes[i] != null) UnityEngine.Object.DestroyImmediate(tempVolumes[i]);

                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        public static void ApplyAvoidWins(Texture2D occupyMask, Texture2D avoidMask)
        {
            if (occupyMask == null || avoidMask == null) return;
            if (occupyMask.width != avoidMask.width || occupyMask.height != avoidMask.height)
                throw new ArgumentException("occupyMask and avoidMask must have matching dimensions.");

            Color[] occupy = occupyMask.GetPixels();
            Color[] avoid = avoidMask.GetPixels();
            for (int i = 0; i < occupy.Length; i++)
            {
                float keep = 1f - Mathf.Clamp01(avoid[i].r);
                float v = occupy[i].r * keep;
                occupy[i] = new Color(v, v, v, 1f);
            }
            occupyMask.SetPixels(occupy);
            occupyMask.Apply(false);
        }

        private static Dictionary<string, ProxyIntent> BuildIntentLookup(SceneIntent sceneIntent)
        {
            Dictionary<string, ProxyIntent> lookup = new();
            if (sceneIntent?.Proxies == null) return lookup;
            foreach (ProxyIntent proxy in sceneIntent.Proxies)
            {
                if (proxy == null || string.IsNullOrEmpty(proxy.Id)) continue;
                lookup[proxy.Id] = proxy;
            }
            return lookup;
        }

        private static Dictionary<string, SpatialProxy> BuildLiveProxyLookup()
        {
            Dictionary<string, SpatialProxy> lookup = new();
            SpatialProxy[] liveProxies = UnityEngine.Object.FindObjectsByType<SpatialProxy>(FindObjectsSortMode.None);
            foreach (SpatialProxy p in liveProxies)
            {
                if (p == null || string.IsNullOrEmpty(p.ProxyId)) continue;
                lookup[p.ProxyId] = p;
            }
            return lookup;
        }

        private static Texture2D RenderMask(SceneIntent sceneIntent, Camera sourceCamera, int width, int height, HashSet<string> proxyIds, float focusScaleMultiplier = 1f)
        {
            Material material = CreateSolidWhiteMaterial();
            try
            {
                return RenderProxyPass(sceneIntent, sourceCamera, width, height, proxyIds, material, focusScaleMultiplier);
            }
            finally
            {
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
            }
        }

        private static List<GameObject> CreateTempVolumes(
            HashSet<string> ids,
            Dictionary<string, ProxyIntent> intentById,
            Dictionary<string, SpatialProxy> liveById,
            int layer,
            Material material,
            float scaleMultiplier)
        {
            List<GameObject> volumes = new();
            foreach (string id in ids)
            {
                if (string.IsNullOrEmpty(id)) continue;
                GameObject volume = TryCreateVolume(id, intentById, liveById, layer, material, scaleMultiplier);
                if (volume != null) volumes.Add(volume);
            }
            return volumes;
        }

        private static GameObject TryCreateVolume(
            string proxyId,
            Dictionary<string, ProxyIntent> intentById,
            Dictionary<string, SpatialProxy> liveById,
            int layer,
            Material material,
            float scaleMultiplier)
        {
            ProxyShape shape = ProxyShape.Box;
            Vector3 position = Vector3.zero;
            Quaternion rotation = Quaternion.identity;
            Vector3 scale = Vector3.one;

            if (liveById.TryGetValue(proxyId, out SpatialProxy liveProxy) && liveProxy != null)
            {
                shape = ToIntentShape(liveProxy.Shape);
                position = liveProxy.transform.position;
                rotation = liveProxy.transform.rotation;
                scale = Vector3.Scale(liveProxy.size, liveProxy.transform.lossyScale);
            }
            else if (intentById.TryGetValue(proxyId, out ProxyIntent proxy) && proxy != null)
            {
                shape = proxy.Shape;
                position = ToVector3(proxy.Pose?.Position, Vector3.zero);
                rotation = ToQuaternion(proxy.Pose?.Rotation, Quaternion.identity);
                scale = ToVector3(proxy.Pose?.Scale, Vector3.one);
            }
            else return null;

            scale *= Mathf.Max(0.001f, scaleMultiplier);
            scale.x = Mathf.Max(0.01f, scale.x);
            scale.y = Mathf.Max(0.01f, scale.y);
            scale.z = Mathf.Max(0.01f, scale.z);

            PrimitiveType primitive = shape switch
            {
                ProxyShape.Box => PrimitiveType.Cube,
                ProxyShape.Sphere => PrimitiveType.Sphere,
                ProxyShape.Cylinder => PrimitiveType.Cylinder,
                _ => PrimitiveType.Cube
            };

            GameObject go = GameObject.CreatePrimitive(primitive);
            go.name = $"ConstraintMask_{proxyId}";
            go.layer = layer;
            go.transform.position = position;
            go.transform.rotation = rotation;
            go.transform.localScale = scale;

            Collider collider = go.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.DestroyImmediate(collider);

            MeshRenderer renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            }

            return go;
        }

        private static Texture2D ReadTexture(RenderTexture rt, int width, int height)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            try
            {
                Texture2D texture = new(width, height, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply(false);
                return texture;
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        private static Texture2D BoxBlur(Texture2D source, int width, int height, int radius)
        {
            if (source == null || radius <= 0) return source;
            Color[] src = source.GetPixels();
            Color[] dst = new Color[src.Length];
            int diameter = radius * 2 + 1;
            float invKernel = 1f / (diameter * diameter);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float sum = 0f;
                    for (int ky = -radius; ky <= radius; ky++)
                    {
                        int sy = Mathf.Clamp(y + ky, 0, height - 1);
                        int row = sy * width;
                        for (int kx = -radius; kx <= radius; kx++)
                        {
                            int sx = Mathf.Clamp(x + kx, 0, width - 1);
                            sum += src[row + sx].r;
                        }
                    }
                    float v = Mathf.Clamp01(sum * invKernel);
                    dst[y * width + x] = new Color(v, v, v, 1f);
                }
            }

            Texture2D blurred = new(width, height, TextureFormat.RGBA32, false);
            blurred.SetPixels(dst);
            blurred.Apply(false);
            return blurred;
        }

        private static Material CreateSolidWhiteMaterial()
        {
            Shader shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) throw new InvalidOperationException("Failed to find a usable unlit shader for constraint mask rendering.");
            Material material = new(shader) { color = Color.white };
            return material;
        }

        private static int ResolveConstraintLayer()
        {
            int namedLayer = LayerMask.NameToLayer(ConstraintProxyLayerName);
            return namedLayer >= 0 ? namedLayer : FallbackConstraintLayer;
        }

        private static ProxyShape ToIntentShape(SpatialProxyShape shape)
        {
            return shape switch
            {
                SpatialProxyShape.Box => ProxyShape.Box,
                SpatialProxyShape.Sphere => ProxyShape.Sphere,
                SpatialProxyShape.Cylinder => ProxyShape.Cylinder,
                _ => ProxyShape.Box
            };
        }

        private static Vector3 ToVector3(Vector3Data v, Vector3 fallback)
        {
            if (v == null) return fallback;
            return new Vector3(v.X, v.Y, v.Z);
        }

        private static Quaternion ToQuaternion(QuaternionData q, Quaternion fallback)
        {
            if (q == null) return fallback;
            return new Quaternion(q.X, q.Y, q.Z, q.W);
        }
    }
}
