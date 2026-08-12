using NUnit.Framework;
using UnityEngine;

public class RefinementApplyTransactionTests
{
    [Test]
    public void RollbackLeavesOriginalHierarchyActiveAndDestroysTemporaryResult()
    {
        var source = new GameObject("Original");
        var temporary = new GameObject("TemporaryRefinement");
        var transaction = new RefinementApplyTransaction(
            new[] { source.transform }, temporary, undoGroup: -1);

        transaction.Rollback();

        Assert.IsTrue(source.activeSelf);
        Assert.IsTrue(temporary == null, "temporary result should be destroyed");
        Object.DestroyImmediate(source);
    }

    [Test]
    public void CommitHidesOriginalOnlyAfterValidationPathCompletes()
    {
        var source = new GameObject("Original");
        var result = new GameObject("Result");
        var transaction = new RefinementApplyTransaction(
            new[] { source.transform }, result, undoGroup: -1);

        Assert.IsTrue(source.activeSelf);
        transaction.Commit();
        Assert.IsFalse(source.activeSelf);

        Object.DestroyImmediate(source);
        Object.DestroyImmediate(result);
    }
}
