using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.CostBenefit;

namespace RMC.TotalRisk.Tests.Results.CostBenefit;

/// <summary>
/// Tests the chance-constraint row: guards, the rectangular verdict matrix, and the echoes.
/// </summary>
[TestClass]
public class ChanceConstraintEntryTests
{
    /// <summary>Verifies the guards, the verdict-matrix shape, and the echoes.</summary>
    [TestMethod]
    public void Test_Ctor_GuardsAndEcho()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() => new ChanceConstraintEntry(
            null!, new[] { "A" }, new[] { 0.1d }, new[] { 0.9d }, new[] { 0.9d },
            new[] { new[] { true } }));
        Assert.ThrowsException<ArgumentException>(() => new ChanceConstraintEntry(
            "c", new[] { "A", "B" }, new[] { 0.1d }, new[] { 0.9d, 0.8d }, new[] { 0.9d },
            new[] { new[] { true, false } }));
        Assert.ThrowsException<ArgumentException>(() => new ChanceConstraintEntry(
            "c", new[] { "A" }, new[] { 0.1d }, new[] { 0.9d }, new[] { 0.9d, 0.95d },
            new[] { new[] { true } }));
        Assert.ThrowsException<ArgumentException>(() => new ChanceConstraintEntry(
            "c", new[] { "A", "B" }, new[] { 0.1d, 0.2d }, new[] { 0.9d, 0.8d }, new[] { 0.9d },
            new[] { new[] { true } }));

        var entry = new ChanceConstraintEntry("Mean of Total ≤ 100", new[] { "Baseline", "Alt A" },
            new[] { 0.25d, 0.05d }, new[] { 0.75d, 0.95d }, new[] { 0.9d, 0.5d },
            new[] { new[] { false, true }, new[] { false, true } });
        Assert.AreEqual("Mean of Total ≤ 100", entry.ConstraintLabel);
        Assert.AreEqual(0.25d, entry.ExceedanceFractions[0]);
        Assert.AreEqual(0.95d, entry.SatisfactionProbabilities[1]);
        Assert.AreEqual(0.5d, entry.ConfidenceLevels[1]);
        Assert.IsFalse(entry.Verdicts[0][0]);
        Assert.IsTrue(entry.Verdicts[1][1]);
    }
}
