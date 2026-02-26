using System.Collections.Generic;
using UnityEngine;

namespace SpatialGeneration.Generation.Intent
{
    public static class SceneIntentBuilder
    {
        public static SceneIntent Build(SceneStage stage = SceneStage.Creation)
        {
            SpatialProxy[] proxies = Object.FindObjectsByType<SpatialProxy>(FindObjectsSortMode.None);
            return Build(proxies, stage);
        }

        public static SceneIntent Build(IEnumerable<SpatialProxy> proxies, SceneStage stage = SceneStage.Creation)
        {
            SceneIntent sceneIntent = new()
            {
                Stage = stage
            };

            foreach (SpatialProxy proxy in proxies)
            {
                if (proxy == null)
                    continue;

                sceneIntent.Proxies.Add(new ProxyIntent
                {
                    Id = proxy.ProxyId,
                    Role = ToIntentRole(proxy.role),
                    Shape = ToIntentShape(proxy.Shape),
                    Label = proxy.label,
                    Strength = Mathf.Clamp01(proxy.strength),
                    Priority = proxy.priority,
                    Pose = new PoseData
                    {
                        Position = ToVector3Data(proxy.transform.position),
                        Rotation = ToQuaternionData(proxy.transform.rotation),
                        Scale = ToVector3Data(proxy.transform.lossyScale)
                    }
                });
            }

            return sceneIntent;
        }

        private static ProxyRole ToIntentRole(SpatialProxyRole role)
        {
            return role switch
            {
                SpatialProxyRole.Occupy => ProxyRole.Occupy,
                SpatialProxyRole.Avoid => ProxyRole.Avoid,
                SpatialProxyRole.Attract => ProxyRole.Attract,
                _ => ProxyRole.Occupy
            };
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

        private static Vector3Data ToVector3Data(Vector3 value)
        {
            return new Vector3Data
            {
                X = value.x,
                Y = value.y,
                Z = value.z
            };
        }

        private static QuaternionData ToQuaternionData(Quaternion value)
        {
            return new QuaternionData
            {
                X = value.x,
                Y = value.y,
                Z = value.z,
                W = value.w
            };
        }
    }
}
