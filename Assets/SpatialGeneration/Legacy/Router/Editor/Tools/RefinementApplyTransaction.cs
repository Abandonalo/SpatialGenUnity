using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>Keeps a scene replacement all-or-nothing and groups its Undo history.</summary>
public sealed class RefinementApplyTransaction
{
    private readonly IReadOnlyList<Transform> sources;
    private readonly GameObject result;
    private readonly int undoGroup;
    private bool finished;

    public RefinementApplyTransaction(
        IReadOnlyList<Transform> sources,
        GameObject result,
        int undoGroup)
    {
        this.sources = sources;
        this.result = result;
        this.undoGroup = undoGroup;
    }

    public void Commit()
    {
        if (finished)
            return;
        foreach (Transform source in sources)
        {
            if (source == null)
                continue;
            if (undoGroup >= 0)
                Undo.RecordObject(source.gameObject, "Hide Refined Source");
            source.gameObject.SetActive(false);
        }
        if (undoGroup >= 0)
            Undo.CollapseUndoOperations(undoGroup);
        finished = true;
    }

    public void Rollback()
    {
        if (finished)
            return;
        if (undoGroup >= 0)
            Undo.RevertAllDownToGroup(undoGroup);
        if (result != null)
            Object.DestroyImmediate(result);
        // All sources passed to this transaction were active when refinement began.
        foreach (Transform source in sources)
            if (source != null) source.gameObject.SetActive(true);
        finished = true;
    }
}
