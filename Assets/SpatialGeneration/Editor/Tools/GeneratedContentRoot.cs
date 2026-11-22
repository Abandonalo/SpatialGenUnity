using UnityEditor;
using UnityEngine;

namespace SpatialGeneration.Authoring
{
    /// <summary>
    /// The scene root that holds the active generated content, shared by the agent backend and the
    /// legacy router pipeline. Earlier agent runs are archived beside it (GenerationVersions).
    /// </summary>
    public static class GeneratedContentRoot
    {
        public const string Name = RegionSelectionManager.GeneratedContentRootName;

        public static GameObject Find() => GameObject.Find(Name);

        /// <param name="undoable">False for the agent backend, whose runs are versions rather than Undo steps.</param>
        public static GameObject Ensure(bool undoable = true)
        {
            GameObject root = Find();
            if (root != null)
                return root;

            root = new GameObject(Name);
            if (undoable)
                Undo.RegisterCreatedObjectUndo(root, "Create GeneratedContent Root");
            return root;
        }

        /// <summary>Removes everything under the root as one Undo step (researcher utility).</summary>
        public static void Clear()
        {
            GameObject root = Find();
            if (root == null)
                return;

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Cleanup GeneratedContent");
            DestroyChildren(root);
            Undo.CollapseUndoOperations(group);

            InteractionLogger.Log(new InteractionEvent { type = "cleanup" });
        }

        /// <summary>Destroys every child of <paramref name="root"/> as Undo operations in the current group.</summary>
        public static void DestroyChildren(GameObject root)
        {
            for (int i = root.transform.childCount - 1; i >= 0; i--)
                Undo.DestroyObjectImmediate(root.transform.GetChild(i).gameObject);
        }
    }
}
