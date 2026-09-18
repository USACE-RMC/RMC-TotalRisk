using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.CostBenefit.Support;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results;

namespace RMC.TotalRisk.Tests.Analyses.CostBenefit.Support;

/// <summary>
/// Tests the weighted-sum multi-criteria engine on hand matrices: weight normalization with
/// its echo, direction-aware min-max normalization, the constant-objective rule, NaN
/// exclusion, ranks among recommendable alternatives, and tie handling.
/// </summary>
[TestClass]
public class McdaEngineTests
{
    /// <summary>
    /// Verifies the normalization arithmetic at hand values: weights {2, 1, 1} normalize to
    /// {0.5, 0.25, 0.25}, a constant objective contributes zero, and scores compose exactly.
    /// </summary>
    [TestMethod]
    public void Test_Score_WeightsNormalizationAndConstantObjective()
    {
        // Arrange — cost (min), benefit (max), and a constant objective.
        var names = new[] { "A", "B" };
        double[][] values =
        {
            new[] { 10d, 100d, 7d },
            new[] { 30d, 40d, 7d },
        };
        var directions = new[]
            { ObjectiveDirection.Minimize, ObjectiveDirection.Maximize, ObjectiveDirection.Minimize };

        // Act
        McdaResults results = McdaEngine.Score(new[] { "Cost", "Benefit", "Constant" }, directions,
            new[] { 2d, 1d, 1d }, names, values, new[] { true, true });

        // Assert — the weight echo and the exact scores: A is best on both live objectives
        // (normalized 1), the constant contributes zero, so A scores 0.75 and B scores 0.
        CollectionAssert.AreEqual(new[] { 0.5d, 0.25d, 0.25d },
            (System.Collections.ICollection)results.NormalizedWeights);
        Assert.AreEqual(0.75d, results.Scores[0], 0d);
        Assert.AreEqual(0d, results.Scores[1], 0d);
        Assert.AreEqual(0d, results.NormalizedValues[0][2], 0d);
        Assert.AreEqual(0d, results.NormalizedValues[1][2], 0d);
        Assert.AreEqual(1, results.Ranks[0]);
        Assert.AreEqual(2, results.Ranks[1]);
    }

    /// <summary>
    /// Verifies the exclusion rules: a NaN objective value excludes the row (NaN score, rank
    /// zero) without moving the others' normalization, a recommendation-excluded row keeps
    /// its score but is unranked, and tied scores share the better rank.
    /// </summary>
    [TestMethod]
    public void Test_Score_ExclusionsAndTies()
    {
        // Arrange — C carries a NaN; D is recommendation-excluded; A and B tie exactly.
        var names = new[] { "A", "B", "C", "D" };
        double[][] values =
        {
            new[] { 10d, 100d },
            new[] { 10d, 100d },
            new[] { double.NaN, 50d },
            new[] { 20d, 200d },
        };
        var directions = new[] { ObjectiveDirection.Minimize, ObjectiveDirection.Maximize };

        // Act
        McdaResults results = McdaEngine.Score(new[] { "Cost", "Benefit" }, directions,
            new[] { 1d, 1d }, names, values, new[] { true, true, true, false });

        // Assert — the NaN row.
        Assert.IsTrue(results.IsExcludedForNaN[2]);
        Assert.IsTrue(double.IsNaN(results.Scores[2]));
        Assert.AreEqual(0, results.Ranks[2]);

        // The recommendation-excluded row keeps its score but is unranked.
        Assert.IsTrue(results.IsExcludedFromRecommendation[3]);
        Assert.IsFalse(double.IsNaN(results.Scores[3]));
        Assert.AreEqual(0, results.Ranks[3]);

        // The exact tie shares rank one.
        Assert.AreEqual(results.Scores[0], results.Scores[1], 0d);
        Assert.AreEqual(1, results.Ranks[0]);
        Assert.AreEqual(1, results.Ranks[1]);
    }
}
