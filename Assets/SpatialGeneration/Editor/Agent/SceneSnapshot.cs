using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SpatialGeneration.Agent
{
    /// <summary>
    /// Writes the generated scene (and, in condition B, the proxies) as JSON in world space,
    /// so spatial fit and preservation can be scored offline from the logged snapshots: after
    /// every generation run, at acceptance, before and after every edit, and at the end.
    /// </summary>
    public static class SceneSnapshot
    {
        [Serializable]
        private class PartRecord
        {
            public string part_id;
            public string name;
            public string shape;
            public int segments;
            public string color;
            public float[] center_world;
            public float[] rotation_world;
            public float[] size_world;
            public PartCell[] clipped_geometry_local;
        }

        [Serializable]
        private class AssetRecord
        {
            public string asset_id;
            public string label;
            public string prompt;
            public string proxy_id;
            public float[] origin_world;
            public float[] rotation_world;
            public float[] volume_size;
            public float[] bounds_center_world;
            public float[] bounds_size_world;
            public PartRecord[] parts;
        }

        [Serializable]
        private class ProxyRecord
        {
            public string proxy_id;
            public string label;
            public string prompt;
            public string role;
            public float[] center_world;
            public float[] rotation_world;
            public float[] size_world;
        }

        [Serializable]
        private class SnapshotDocument
        {
            public string label;
            public string utc;
            public string participant;
            public string condition;
            public string target;
            public string phase;
            public int run;
            public AssetRecord[] assets;
            public ProxyRecord[] proxies;
        }

        public static string Write(string label)
        {
            StudySession session = StudySession.instance;
            var document = new SnapshotDocument
            {
                label = label,
                utc = StudySession.NowUtc(),
                participant = session.participantId,
                condition = session.condition == StudyCondition.TextOnly ? "A" : "B",
                target = session.targetScene,
                phase = session.phase.ToString(),
                run = session.activeRun,
                assets = CollectAssets(),
                proxies = CollectProxies()
            };

            string participant = string.IsNullOrWhiteSpace(session.participantId) ? "unassigned" : AgentSceneOps.Sanitize(session.participantId);
            string dir = Path.Combine(AgentProcessRunner.ProjectRoot, "Logs", "SpatialGenerationStudy", participant,
                $"condition_{document.condition}");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{AgentSceneOps.Sanitize(label)}.json");
            File.WriteAllText(path, JsonUtility.ToJson(document, true));
            return path;
        }

        private static AssetRecord[] CollectAssets()
        {
            var records = new List<AssetRecord>();
            foreach (AgentAsset asset in AgentSceneOps.AllAssets())
            {
                var parts = new List<PartRecord>();
                Bounds bounds = default;
                bool hasBounds = false;

                foreach (AgentPart part in AgentSceneOps.PartsOf(asset))
                {
                    Transform t = part.transform;
                    parts.Add(new PartRecord
                    {
                        part_id = part.partId,
                        name = t.name,
                        shape = part.shape.ToString(),
                        segments = part.segments,
                        color = "#" + ColorUtility.ToHtmlStringRGB(part.color),
                        center_world = Arr(t.position),
                        rotation_world = Arr(t.rotation),
                        size_world = Arr(t.lossyScale),
                        clipped_geometry_local = part.fragments.Count > 0 ? part.fragments.ToArray() : null
                    });

                    Renderer renderer = part.GetComponent<Renderer>();
                    if (renderer == null)
                        continue;
                    if (!hasBounds)
                    {
                        bounds = renderer.bounds;
                        hasBounds = true;
                    }
                    else
                    {
                        bounds.Encapsulate(renderer.bounds);
                    }
                }

                records.Add(new AssetRecord
                {
                    asset_id = asset.assetId,
                    label = asset.label,
                    prompt = asset.prompt,
                    proxy_id = asset.proxyId,
                    origin_world = Arr(asset.transform.position),
                    rotation_world = Arr(asset.transform.rotation),
                    volume_size = Arr(asset.volumeSize),
                    bounds_center_world = hasBounds ? Arr(bounds.center) : null,
                    bounds_size_world = hasBounds ? Arr(bounds.size) : null,
                    parts = parts.ToArray()
                });
            }

            return records.ToArray();
        }

        private static ProxyRecord[] CollectProxies()
        {
            var records = new List<ProxyRecord>();
            foreach (SpatialProxy proxy in UnityEngine.Object.FindObjectsByType<SpatialProxy>(FindObjectsSortMode.None))
            {
                Transform t = proxy.transform;
                records.Add(new ProxyRecord
                {
                    proxy_id = proxy.ProxyId,
                    label = proxy.label,
                    prompt = proxy.assetPrompt,
                    role = proxy.role.ToString(),
                    center_world = Arr(t.position),
                    rotation_world = Arr(t.rotation),
                    size_world = Arr(Vector3.Scale(proxy.size, t.lossyScale))
                });
            }

            return records.ToArray();
        }

        private static float[] Arr(Vector3 v) => new[] { v.x, v.y, v.z };
        private static float[] Arr(Quaternion q) => new[] { q.x, q.y, q.z, q.w };
    }
}
