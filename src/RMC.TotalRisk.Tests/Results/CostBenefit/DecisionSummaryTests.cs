using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.CostBenefit;

namespace RMC.TotalRisk.Tests.Results.CostBenefit;

/// <summary>
/// Tests the decision summary: guards, parallelism, and the defensive copies.
/// </summary>
[TestClass]
public class DecisionSummaryTests
{
    /// <summary>Verifies the guards, the parallelism rule, and the copied lists.</summary>
    [TestMethod]
    public void Test_Ctor_GuardsAndCopies()
    {
        // Arrange
        var entry = new DecisionSummaryEntry(DecisionStrategy.ExpectedValue, 1, "Aleatory",
            "criterion", "", "Alt A", 1d, false);

        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() => new DecisionSummary(
            null!, new[] { "A" }, new[] { 1 }, new[] { false }));
        Assert.ThrowsException<ArgumentException>(() => new DecisionSummary(
            new[] { entry }, new[] { "A", "B" }, new[] { 1 }, new[] { false, false }));
        Assert.ThrowsException<ArgumentException>(() => new DecisionSummary(
            new[] { entry }, new[] { "A", "B" }, new[] { 1, 0 }, new[] { false }));

        var counts = new System.Collections.Generic.List<int> { 2, 0 };
        var summary = new DecisionSummary(new[] { entry }, new[] { "Baseline", "Alt A" },
            counts, new[] { false, true });
        counts[0] = -1;
        Assert.AreEqual(1, summary.Entries.Count);
        Assert.AreSame(entry, summary.Entries[0]);
        Assert.AreEqual(2, summary.RecommendationCounts[0], "The count list must be defensively copied.");
        Assert.IsTrue(summary.FailsDoNoHarm[1]);
    }
}
