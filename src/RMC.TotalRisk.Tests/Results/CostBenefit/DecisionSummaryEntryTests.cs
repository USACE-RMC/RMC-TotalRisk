using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.CostBenefit;

namespace RMC.TotalRisk.Tests.Results.CostBenefit;

/// <summary>
/// Tests the decision-summary row: guards, the echoes, and the withheld coercions.
/// </summary>
[TestClass]
public class DecisionSummaryEntryTests
{
    /// <summary>Verifies the guards and the echoes.</summary>
    [TestMethod]
    public void Test_Ctor_GuardsAndEcho()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentException>(() => new DecisionSummaryEntry(
            DecisionStrategy.Laplace, 0, "Epistemic", "c", "", "A", 1d, false));
        Assert.ThrowsException<ArgumentNullException>(() => new DecisionSummaryEntry(
            DecisionStrategy.Laplace, 2, null!, "c", "", "A", 1d, false));
        Assert.ThrowsException<ArgumentNullException>(() => new DecisionSummaryEntry(
            DecisionStrategy.Laplace, 2, "Epistemic", null!, "", "A", 1d, false));

        var entry = new DecisionSummaryEntry(DecisionStrategy.Laplace, 2, "Epistemic",
            "Mean of Total type 0", "α = 0.5", "Alt A", 3.5d, false);
        Assert.AreEqual(DecisionStrategy.Laplace, entry.Strategy);
        Assert.AreEqual(2, entry.Tier);
        Assert.AreEqual("Epistemic", entry.Layer);
        Assert.AreEqual("Mean of Total type 0", entry.CriterionLabel);
        Assert.AreEqual("α = 0.5", entry.ParameterEcho);
        Assert.AreEqual("Alt A", entry.RecommendedAlternative);
        Assert.AreEqual(3.5d, entry.RecommendedValue);
        Assert.IsFalse(entry.RecommendationWithheld);
    }

    /// <summary>Verifies the withheld row's coercions: empty name and echo, flag set.</summary>
    [TestMethod]
    public void Test_Ctor_WithheldCoercions()
    {
        // Act
        var entry = new DecisionSummaryEntry(DecisionStrategy.WaldMaximin, 2, "Epistemic",
            "criterion", null!, null!, double.NaN, true);

        // Assert
        Assert.AreEqual(string.Empty, entry.ParameterEcho);
        Assert.AreEqual(string.Empty, entry.RecommendedAlternative);
        Assert.IsTrue(double.IsNaN(entry.RecommendedValue));
        Assert.IsTrue(entry.RecommendationWithheld);
    }
}
