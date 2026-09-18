using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.CostBenefit;

namespace RMC.TotalRisk.Tests.Results.CostBenefit;

/// <summary>
/// Tests the ε-sweep results block: guards, the parallel-entry rule, and the echoes.
/// </summary>
[TestClass]
public class EpsilonSweepResultsTests
{
    /// <summary>Verifies null guards and the entries-parallel-grid rule.</summary>
    [TestMethod]
    public void Test_Ctor_Guards()
    {
        // Arrange
        var entry = new EpsilonSweepEntry(1d, "A", 2d, 1d, double.NaN, 1, false, false);

        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() => new EpsilonSweepResults(null!,
            ObjectiveDirection.Minimize, "ε", new[] { 1d }, new[] { entry }, new[] { "A" }));
        Assert.ThrowsException<ArgumentNullException>(() => new EpsilonSweepResults("Primary",
            ObjectiveDirection.Minimize, "ε", null!, new[] { entry }, new[] { "A" }));
        Assert.ThrowsException<ArgumentException>(() => new EpsilonSweepResults("Primary",
            ObjectiveDirection.Minimize, "ε", new[] { 1d, 2d }, new[] { entry }, new[] { "A" }));
    }

    /// <summary>Verifies the echoes and the defensive copies.</summary>
    [TestMethod]
    public void Test_Ctor_PropertiesEchoed()
    {
        // Arrange
        var entry = new EpsilonSweepEntry(1d, "A", 2d, 1d, double.NaN, 1, false, false);
        var grid = new System.Collections.Generic.List<double> { 1d };

        // Act
        var results = new EpsilonSweepResults("Primary", ObjectiveDirection.Maximize, "Swept label",
            grid, new[] { entry }, new[] { "A" });
        grid.Add(9d);

        // Assert
        Assert.AreEqual("Primary", results.PrimaryName);
        Assert.AreEqual(ObjectiveDirection.Maximize, results.PrimaryDirection);
        Assert.AreEqual("Swept label", results.EpsilonMetricLabel);
        Assert.AreEqual(1, results.Grid.Count);
        Assert.AreSame(entry, results.Entries[0]);
        Assert.AreEqual("A", results.NoninferiorAlternatives[0]);
    }
}
