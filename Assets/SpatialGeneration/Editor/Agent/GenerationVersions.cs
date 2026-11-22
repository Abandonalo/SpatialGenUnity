using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SpatialGeneration.Authoring;

namespace SpatialGeneration.Agent
{
    /// <summary>
    /// Keeps every generation run of a condition so a participant can continue from any of
    /// them. The active run is the scene's <c>GeneratedContent</c>; earlier runs are kept,
    /// inactive, under <see cref="ContainerName"/>.
    /// </summary>
    public static class GenerationVersions
    {
        public const string ContainerName = "SpatialGeneration Versions";

        public static string RunName(int run) => run > 0 ? $"GeneratedContent (run {run})" : "GeneratedContent (earlier)";

        /// <summary>Moves the active GeneratedContent out of the way under the given run number.</summary>
        public static void ArchiveActive(int run)
        {
            GameObject root = GameObject.Find(GeneratedContentRoot.Name);
            if (root == null)
                return;

            GameObject container = EnsureContainer();
            string name = RunName(run);
            Transform existing = container.transform.Find(name);
            if (existing != null)
                existing.name = name + " (replaced)";

            root.name = name;
            root.transform.SetParent(container.transform, true);
            root.SetActive(false);
            MarkDirty(container);
        }

        /// <summary>Makes an archived run the active GeneratedContent, archiving the current one.</summary>
        public static bool Activate(int run, int currentRun)
        {
            GameObject container = GameObject.Find(ContainerName);
            Transform archived = container != null ? container.transform.Find(RunName(run)) : null;
            if (archived == null)
                return false;

            ArchiveActive(currentRun);
            archived.SetParent(null, true);
            archived.name = GeneratedContentRoot.Name;
            archived.gameObject.SetActive(true);
            MarkDirty(archived.gameObject);
            return true;
        }

        /// <summary>Destroys the active GeneratedContent (a run that produced nothing). Not undoable.</summary>
        public static void DiscardActive()
        {
            GameObject root = GameObject.Find(GeneratedContentRoot.Name);
            if (root == null)
                return;
            Scene scene = root.scene;
            Object.DestroyImmediate(root);
            if (scene.IsValid())
                EditorSceneManager.MarkSceneDirty(scene);
        }

        /// <summary>Makes an archived run active again when there is no active one (after <see cref="DiscardActive"/>).</summary>
        public static bool Restore(int run)
        {
            if (GameObject.Find(GeneratedContentRoot.Name) != null)
                return false;
            GameObject container = GameObject.Find(ContainerName);
            Transform archived = container != null ? container.transform.Find(RunName(run)) : null;
            if (archived == null)
                return false;
            archived.SetParent(null, true);
            archived.name = GeneratedContentRoot.Name;
            archived.gameObject.SetActive(true);
            MarkDirty(archived.gameObject);
            return true;
        }

        private static GameObject EnsureContainer()
        {
            GameObject container = GameObject.Find(ContainerName);
            if (container != null)
                return container;
            container = new GameObject(ContainerName);
            return container;
        }

        private static void MarkDirty(GameObject go)
        {
            if (go != null && go.scene.IsValid())
                EditorSceneManager.MarkSceneDirty(go.scene);
        }
    }
}
