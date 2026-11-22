using UnityEditor;

namespace SpatialGeneration.Agent
{
    /// <summary>
    /// Routes Ctrl+Z / Ctrl+Y to the study. An applied refinement is one Undo step named
    /// <see cref="RefinementReview.ApplyUndoName"/>; reverting or restoring it changes the scene the
    /// participant leaves behind, so it is counted, logged and snapshotted like the apply itself.
    /// </summary>
    [InitializeOnLoad]
    internal static class StudyUndoTracker
    {
        static StudyUndoTracker()
        {
            Undo.undoRedoEvent -= OnUndoRedo;
            Undo.undoRedoEvent += OnUndoRedo;
        }

        private static void OnUndoRedo(in UndoRedoInfo info)
        {
            if (info.undoName == RefinementReview.ApplyUndoName)
            {
                if (info.isRedo) StudyController.OnApplyRedone();
                else StudyController.OnApplyUndone();
            }
            else
            {
                StudyController.OnOtherUndo(info.undoName, info.isRedo);
            }
        }
    }
}
