using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.CostBenefit.Support;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results;

namespace RMC.TotalRisk.Tests.Analyses.CostBenefit.Support;

/// <summary>
/// Tests the frontier engine on hand matrices: strict and weak dominance with exact ties
/// both kept, mixed directions, NaN exclusion, and the cost-ranked incremental table whose
/// cost-per-life-saved column is the shared trade-off helper.
/// </summary>
[TestClass]
public class ParetoFrontierEngineTests
{
    /// <summary>
    /// Verifies the weak-dominance screen: a strictly dominated row is removed, exact ties
    /// are both kept, mixed directions orient per objective, and excluded rows neither
    /// dominate nor survive.
    /// </summary>
    [TestMethod]
    public void Test_NonDominated_StrictWeakTieMixedAndExcluded()
    {
        // Arrange — objectives (cost min, benefit max). A: (10, 100); B: (10, 100) exact tie;
        // C: (12, 90) dominated by A; D: (8, 100) dominates nothing it shouldn't but is best
        // on cost; E: excluded despite coordinates that would dominate everything.
        double[][] values =
        {
            new[] { 10d, 100d },
            new[] { 10d, 100d },
            new[] { 12d, 90d },
            new[] { 8d, 100d },
            new[] { 0d, 1000d },
        };
        var directions = new[] { ObjectiveDirection.Minimize, ObjectiveDirection.Maximize };
        var excluded = new[] { false, false, false, false, true };

        // Act
        bool[] nonDominated = ParetoFrontierEngine.NonDominated(values, directions, excluded);

        // Assert — the exact tie survives on both rows; D weakly dominates A and B (equal
        // benefit, strictly lower cost), so only C, D resolve strictly.
        Assert.IsFalse(nonDominated[0], "A is weakly dominated by D (equal benefit, lower cost).");
        Assert.IsFalse(nonDominated[1], "B is weakly dominated by D.");
        Assert.IsFalse(nonDominated[2], "C is strictly dominated.");
        Assert.IsTrue(nonDominated[3]);
        Assert.IsFalse(nonDominated[4], "An excluded row never survives.");

        // Exact ties both kept: with only the two tied rows, neither dominates.
        bool[] ties = ParetoFrontierEngine.NonDominated(
            new[] { new[] { 10d, 100d }, new[] { 10d, 100d } }, directions, new bool[2]);
        Assert.IsTrue(ties[0]);
        Assert.IsTrue(ties[1]);
    }

    /// <summary>
    /// Verifies the incremental table: the cost-ranked walk over the included set, the
    /// increment columns, the zero-cost-step NaN, and the bit-exact identity between the
    /// cost-per-life-saved column and the shared trade-off helper.
    /// </summary>
    [TestMethod]
    public void Test_IncrementalAnalysis_WalkRatiosAndTradeOffIdentity()
    {
        // Arrange — three included alternatives out of cost order, one excluded.
        string[] names = { "C", "A", "B", "X" };
        double[] costs = { 30d, 10d, 20d, 15d };
        double[] benefits = { 90d, 40d, 70d, 1000d };
        double[] lives = { 0.009d, 0.004d, 0.007d, 1d };
        var include = new[] { true, true, true, false };

        // Act
        List<IncrementalEntry> entries = ParetoFrontierEngine.IncrementalAnalysis(names, costs,
            benefits, lives, include);

        // Assert — cost-ranked A → B → C.
        Assert.AreEqual(2, entries.Count);
        Assert.AreEqual("A", entries[0].FromAlternative);
        Assert.AreEqual("B", entries[0].ToAlternative);
        Assert.AreEqual(10d, entries[0].DeltaCost, 0d);
        Assert.AreEqual(30d, entries[0].DeltaBenefit, 0d);
        Assert.AreEqual(3d, entries[0].IncrementalBenefitCostRatio, 0d);
        Assert.AreEqual(
            EpsilonSweepEngine.TradeOffRatio(costs[1], costs[2], -lives[1], -lives[2]),
            entries[0].IncrementalCostPerLifeSaved, 0d);
        Assert.AreEqual("B", entries[1].FromAlternative);
        Assert.AreEqual("C", entries[1].ToAlternative);
        Assert.AreEqual(
            EpsilonSweepEngine.TradeOffRatio(costs[2], costs[0], -lives[2], -lives[0]),
            entries[1].IncrementalCostPerLifeSaved, 0d);

        // A zero cost step makes the benefit ratio NaN; a non-positive lives step NaNs the
        // trade-off column.
        List<IncrementalEntry> flat = ParetoFrontierEngine.IncrementalAnalysis(
            new[] { "A", "B" }, new[] { 10d, 10d }, new[] { 40d, 50d }, new[] { 0.004d, 0.003d },
            new[] { true, true });
        Assert.AreEqual(1, flat.Count);
        Assert.IsTrue(double.IsNaN(flat[0].IncrementalBenefitCostRatio));
        Assert.IsTrue(double.IsNaN(flat[0].IncrementalCostPerLifeSaved));
    }
}
