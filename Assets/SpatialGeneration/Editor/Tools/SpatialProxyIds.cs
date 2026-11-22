using System.Collections.Generic;
using System.Linq;
using SpatialGeneration.Agent;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SpatialGeneration.Authoring
{
    /// <summary>
    /// Keeps every spatial proxy's ID unique. Duplicating or pasting a proxy copies its serialized ID,
    /// and generated assets, the proxy list and the scored snapshots all identify a proxy by it.
    /// </summary>
    [InitializeOnLoad]
    public static class SpatialProxyIds
    {
        static SpatialProxyIds()
        {
            ObjectChangeEvents.changesPublished += OnChangesPublished;
        }

        // Duplicate and paste arrive as created hierarchies: the new object is the copy and gets the
        // new ID. Other changes ("Paste Component Values", Inspector edits) go through the owner-aware repair.
        private static void OnChangesPublished(ref ObjectChangeEventStream stream)
        {
            var created = new List<GameObject>();
            var changed = new List<GameObject>();
            for (int i = 0; i < stream.length; i++)
            {
                switch (stream.GetEventType(i))
                {
                    case ObjectChangeKind.CreateGameObjectHierarchy:
                        stream.GetCreateGameObjectHierarchyEvent(i, out CreateGameObjectHierarchyEventArgs c);
                        Add(created, c.instanceId);
                        break;
                    case ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                        stream.GetChangeGameObjectStructureHierarchyEvent(i, out ChangeGameObjectStructureHierarchyEventArgs h);
                        Add(changed, h.instanceId);
                        break;
                    case ObjectChangeKind.ChangeGameObjectStructure:
                        stream.GetChangeGameObjectStructureEvent(i, out ChangeGameObjectStructureEventArgs s);
                        Add(changed, s.instanceId);
                        break;
                    case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                        stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out ChangeGameObjectOrComponentPropertiesEventArgs p);
                        Add(changed, p.instanceId);
                        break;
                }
            }

            // A copy that just appeared yields to a proxy that already existed. When every holder of an
            // ID is new (created and duplicated in one update), the owner-aware repair decides.
            var fresh = new HashSet<SpatialProxy>(created.SelectMany(go => go.GetComponentsInChildren<SpatialProxy>(true)));
            foreach (SpatialProxy proxy in fresh)
            {
                if (proxy == null || string.IsNullOrEmpty(proxy.ProxyId)) continue;
                List<SpatialProxy> sameId = ProxiesIn(proxy.gameObject.scene).Where(o => o.ProxyId == proxy.ProxyId).ToList();
                if (sameId.Count < 2) continue;
                if (sameId.Any(o => !fresh.Contains(o))) Reissue(proxy, "duplicate");
                else RepairDuplicates(sameId);
            }

            foreach (GameObject go in changed)
            foreach (SpatialProxy proxy in go.GetComponentsInChildren<SpatialProxy>(true))
            {
                if (proxy == null || string.IsNullOrEmpty(proxy.ProxyId)) continue;
                List<SpatialProxy> sameId = ProxiesIn(proxy.gameObject.scene).Where(o => o.ProxyId == proxy.ProxyId).ToList();
                if (sameId.Count > 1) RepairDuplicates(sameId);
            }
        }

        private static void Add(List<GameObject> list, int instanceId)
        {
            Object target = EditorUtility.EntityIdToObject(instanceId);
            GameObject go = target is Component component ? component.gameObject : target as GameObject;
            if (go != null) list.Add(go);
        }

        /// <summary>Re-issues the proxy's ID if another proxy in its scene already has it.</summary>
        public static bool EnsureUnique(SpatialProxy proxy)
        {
            if (proxy == null || string.IsNullOrEmpty(proxy.ProxyId))
                return false;
            bool clash = ProxiesIn(proxy.gameObject.scene).Any(other => other != proxy && other.ProxyId == proxy.ProxyId);
            if (clash)
                Reissue(proxy, "duplicate");
            return clash;
        }

        /// <summary>
        /// Safety net before a generation run: leaves exactly one owner per ID. The proxy that already
        /// has generated assets keeps it; otherwise the first in hierarchy order does. Returns the
        /// number of proxies that received a new ID.
        /// </summary>
        public static int RepairDuplicates(IEnumerable<SpatialProxy> proxies)
        {
            List<AgentAsset> assets = AgentSceneOps.AllAssets();
            int repaired = 0;
            foreach (IGrouping<string, SpatialProxy> group in proxies.Where(p => p != null && !string.IsNullOrEmpty(p.ProxyId))
                         .GroupBy(p => p.ProxyId).Where(g => g.Count() > 1))
            {
                List<SpatialProxy> ordered = group.OrderBy(HierarchyPath, System.StringComparer.Ordinal).ToList();
                // An asset stores only the proxy ID; its owner is the duplicate standing where it was generated.
                AgentAsset owned = assets.FirstOrDefault(a => a.proxyId == group.Key);
                SpatialProxy keeper = owned == null ? ordered[0]
                    : ordered.OrderBy(p => Vector3.Distance(Footprint(p), owned.transform.position)).First();
                foreach (SpatialProxy copy in ordered.Where(p => p != keeper))
                {
                    Reissue(copy, "repaired");
                    repaired++;
                }
            }

            return repaired;
        }

        private static void Reissue(SpatialProxy proxy, string reason)
        {
            string previous = proxy.ProxyId;
            proxy.RegenerateId();
            EditorUtility.SetDirty(proxy);
            Debug.LogWarning($"Spatial Generation: proxy '{proxy.name}' shared its ID with another proxy; it now has its own ({reason}).");
            InteractionLogger.Log(new InteractionEvent
            {
                type = "proxy_id_reissued",
                proxy_id = proxy.ProxyId,
                label = proxy.label,
                extra = $"previous_id={previous}; reason={reason}"
            });
        }

        /// <summary>Bottom centre of the proxy, where the generated asset's origin is placed.</summary>
        private static Vector3 Footprint(SpatialProxy proxy)
        {
            Transform t = proxy.transform;
            return t.position - t.up * (proxy.size.y * t.lossyScale.y * 0.5f);
        }

        private static IEnumerable<SpatialProxy> ProxiesIn(Scene scene) =>
            Object.FindObjectsByType<SpatialProxy>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(p => p.gameObject.scene == scene);

        private static string HierarchyPath(SpatialProxy proxy)
        {
            var path = new List<string>();
            for (Transform t = proxy.transform; t != null; t = t.parent)
                path.Insert(0, t.GetSiblingIndex().ToString("D6"));
            return string.Join("/", path);
        }
    }
}
