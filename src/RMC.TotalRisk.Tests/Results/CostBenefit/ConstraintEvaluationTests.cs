using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.CostBenefit;

namespace RMC.TotalRisk.Tests.Results.CostBenefit;

/// <summary>
/// Tests the constraint-evaluation row: guards, the echo, and the defensive copy.
/// </summary>
[TestClass]
public class ConstraintEvaluationTests
{
    /// <summary>Verifies guards, the echo, and the copied flag list.</summary>
    [TestMethod]
    public void Test_Ctor_GuardsAndEcho()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() => new ConstraintEvaluation(null!, new[] { true }));
        Assert.ThrowsException<ArgumentNullException>(() => new ConstraintEvaluation("label", null!));

        var flags = new System.Collections.Generic.List<bool> { true, false };
        var evaluation = new ConstraintEvaluation("Mean of Total ≤ 1 (EveryEpoch)", flags);
        flags.Add(true);
        Assert.AreEqual("Mean of Total ≤ 1 (EveryEpoch)", evaluation.Label);
        Assert.AreEqual(2, evaluation.Satisfied.Count);
        Assert.IsTrue(evaluation.Satisfied[0]);
        Assert.IsFalse(evaluation.Satisfied[1]);
    }
}
