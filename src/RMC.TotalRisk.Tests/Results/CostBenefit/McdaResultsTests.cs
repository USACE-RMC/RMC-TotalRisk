using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.CostBenefit;

namespace RMC.TotalRisk.Tests.Results.CostBenefit;

/// <summary>
/// Tests the multi-criteria results block: guards, the parallel-axis rules, and the echoes.
/// </summary>
[TestClass]
public class McdaResultsTests
{
    /// <summary>Verifies the guards: null lists and misaligned axes.</summary>
    [TestMethod]
    public void Test_Ctor_Guards()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() => new McdaResults(null!,
            new[] { ObjectiveDirection.Minimize }, new[] { 1d }, new[] { "A" },
            new IReadOnlyList<double>[] { new[] { 1d } }, new[] { 1d }, new[] { 1 },
            new[] { false }, new[] { false }));
        Assert.ThrowsException<ArgumentException>(() => new McdaResults(new[] { "Cost" },
            new[] { ObjectiveDirection.Minimize }, new[] { 1d, 2d }, new[] { "A" },
            new IReadOnlyList<double>[] { new[] { 1d } }, new[] { 1d }, new[] { 1 },
            new[] { false }, new[] { false }));
        Assert.ThrowsException<ArgumentException>(() => new McdaResults(new[] { "Cost" },
            new[] { ObjectiveDirection.Minimize }, new[] { 1d }, new[] { "A" },
            new IReadOnlyList<double>[] { new[] { 1d } }, new[] { 1d, 2d }, new[] { 1 },
            new[] { false }, new[] { false }));
    }

    /// <summary>Verifies the echoes.</summary>
    [TestMethod]
    public void Test_Ctor_PropertiesEchoed()
    {
        // Act
        var results = new McdaResults(new[] { "Cost" }, new[] { ObjectiveDirection.Minimize },
            new[] { 1d }, new[] { "A", "B" },
            new IReadOnlyList<double>[] { new[] { 1d }, new[] { 0d } },
            new[] { 1d, 0d }, new[] { 1, 2 }, new[] { false, false }, new[] { false, true });

        // Assert
        Assert.AreEqual("Cost", results.ObjectiveNames[0]);
        Assert.AreEqual(1d, results.NormalizedWeights[0]);
        Assert.AreEqual(0d, results.NormalizedValues[1][0]);
        Assert.AreEqual(1d, results.Scores[0]);
        Assert.AreEqual(2, results.Ranks[1]);
        Assert.IsFalse(results.IsExcludedForNaN[0]);
        Assert.IsTrue(results.IsExcludedFromRecommendation[1]);
    }
}
