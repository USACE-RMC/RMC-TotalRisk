using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.CostBenefit;

namespace RMC.TotalRisk.Tests.Results.CostBenefit;

/// <summary>
/// Tests the epistemic band row: guards and the value pass-throughs.
/// </summary>
[TestClass]
public class EpistemicMeasureSummaryTests
{
    /// <summary>Verifies the guards and the pass-throughs, NaN slots included.</summary>
    [TestMethod]
    public void Test_Ctor_GuardsAndPassThrough()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() => new EpistemicMeasureSummary(
            null!, "c", ObjectiveDirection.Minimize, 1d, 0d, 1d, 2d, 0.05d, 0.95d, 1d, 2d, 0.1d, 3d, 3));
        Assert.ThrowsException<ArgumentNullException>(() => new EpistemicMeasureSummary(
            "A", null!, ObjectiveDirection.Minimize, 1d, 0d, 1d, 2d, 0.05d, 0.95d, 1d, 2d, 0.1d, 3d, 3));
        Assert.ThrowsException<ArgumentException>(() => new EpistemicMeasureSummary(
            "A", "c", ObjectiveDirection.Minimize, 1d, 0d, 1d, 2d, 0.05d, 0.95d, 1d, 2d, 0.1d, 3d, -1));

        var row = new EpistemicMeasureSummary("Alt A", "Mean of Total type 0",
            ObjectiveDirection.Minimize, 1.5d, 0.5d, 1.4d, 2.5d, 0.05d, 0.95d, double.NaN,
            2.4d, 0.1d, 2.8d, 3);
        Assert.AreEqual("Alt A", row.AlternativeName);
        Assert.AreEqual("Mean of Total type 0", row.CriterionLabel);
        Assert.AreEqual(ObjectiveDirection.Minimize, row.Direction);
        Assert.AreEqual(1.5d, row.WeightedMean);
        Assert.AreEqual(0.5d, row.LowerValue);
        Assert.AreEqual(1.4d, row.Median);
        Assert.AreEqual(2.5d, row.UpperValue);
        Assert.AreEqual(0.05d, row.LowerLevel);
        Assert.AreEqual(0.95d, row.UpperLevel);
        Assert.IsTrue(double.IsNaN(row.Variance), "A NaN variance must pass through untouched.");
        Assert.AreEqual(2.4d, row.ConditionalValueAtRisk);
        Assert.AreEqual(0.1d, row.TailAlpha);
        Assert.AreEqual(2.8d, row.EffectiveRealizationCount);
        Assert.AreEqual(3, row.RealizationCount);
    }
}
