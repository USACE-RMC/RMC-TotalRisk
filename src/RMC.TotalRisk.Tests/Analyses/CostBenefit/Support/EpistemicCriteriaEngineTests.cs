using System;
using Numerics.Data.Statistics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.CostBenefit.Support;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results;

namespace RMC.TotalRisk.Tests.Analyses.CostBenefit.Support;

/// <summary>
/// Tests the Tier-2 weighted criterion arithmetic: the tolerable-risk mirror (null slots, NaN
/// pairwise filtering, strict exceedance, the zero-weight convention), the satisfaction
/// fraction, the upstream-delegating weighted reductions, the epistemic tail average with its
/// exact boundary split, the zero-weight-ignoring extremes, and the Hurwicz endpoints.
/// </summary>
[TestClass]
public class EpistemicCriteriaEngineTests
{
    /// <summary>Builds a summary whose Total-stream mean is the given value.</summary>
    /// <param name="mean">The mean.</param>
    /// <returns>The summary.</returns>
    private static SystemRiskResults WithTotalMean(double mean)
    {
        var summary = new SystemRiskResults();
        summary.Total.Mean = mean;
        return summary;
    }

    /// <summary>
    /// Verifies the criterion sampler mirrors the tolerable-risk loop: null slots skipped,
    /// NaN values dropped with their weights, survivors packed with their weights.
    /// </summary>
    [TestMethod]
    public void Test_CriterionSample_A7Mirror()
    {
        // Arrange — a null slot, a NaN measure carrying a large weight, and two survivors.
        var summaries = new SystemRiskResults?[]
        {
            null, WithTotalMean(2d), WithTotalMean(double.NaN), WithTotalMean(4d),
        };
        var weights = new[] { 7d, 0.5d, 9d, 0.25d };
        var values = new double[summaries.Length];
        var sampleWeights = new double[summaries.Length];

        // Act
        int used = EpistemicCriteriaEngine.CriterionSample(summaries, weights, RiskType.Total, 0,
            RiskMeasure.Mean, values, sampleWeights);

        // Assert
        Assert.AreEqual(2, used);
        Assert.AreEqual(2d, values[0]);
        Assert.AreEqual(4d, values[1]);
        Assert.AreEqual(0.5d, sampleWeights[0]);
        Assert.AreEqual(0.25d, sampleWeights[1]);
    }

    /// <summary>
    /// Verifies the exceedance fraction's arithmetic: strict exceedance, dropped NaN weight,
    /// the null-weights equal-weight path, and NaN at zero surviving weight.
    /// </summary>
    [TestMethod]
    public void Test_ExceedanceFraction_StrictAndZeroWeight()
    {
        // Arrange
        var summaries = new SystemRiskResults?[]
        {
            null, WithTotalMean(2d), WithTotalMean(double.NaN), WithTotalMean(4d),
        };
        var weights = new[] { 7d, 0.5d, 9d, 0.25d };

        // Act / Assert — 4 exceeds 3 with weight 0.25 of the surviving 0.75.
        Assert.AreEqual(0.25d / 0.75d, EpistemicCriteriaEngine.ExceedanceFraction(summaries,
            weights, RiskType.Total, 0, RiskMeasure.Mean, 3d), 0d);
        // Strictness: a value equal to the threshold does not exceed it.
        Assert.AreEqual(0d, EpistemicCriteriaEngine.ExceedanceFraction(summaries, weights,
            RiskType.Total, 0, RiskMeasure.Mean, 4d), 0d);
        // Equal weights when the ensemble declares none.
        Assert.AreEqual(0.5d, EpistemicCriteriaEngine.ExceedanceFraction(summaries, null,
            RiskType.Total, 0, RiskMeasure.Mean, 3d), 0d);
        // All-NaN: zero surviving weight is unmeasurable.
        var unmeasurable = new SystemRiskResults?[] { WithTotalMean(double.NaN) };
        Assert.IsTrue(double.IsNaN(EpistemicCriteriaEngine.ExceedanceFraction(unmeasurable, null,
            RiskType.Total, 0, RiskMeasure.Mean, 3d)));
    }

    /// <summary>
    /// Verifies the satisfaction fraction counts at-or-above values, complementing the strict
    /// exceedance at ties.
    /// </summary>
    [TestMethod]
    public void Test_SatisfactionFraction_AtOrAbove()
    {
        // Arrange
        var summaries = new SystemRiskResults?[] { WithTotalMean(2d), WithTotalMean(4d) };

        // Act / Assert — at the tie value, satisfaction counts it while exceedance does not.
        Assert.AreEqual(0.5d, EpistemicCriteriaEngine.SatisfactionFraction(summaries, null,
            RiskType.Total, 0, RiskMeasure.Mean, 4d), 0d);
        Assert.AreEqual(0d, EpistemicCriteriaEngine.ExceedanceFraction(summaries, null,
            RiskType.Total, 0, RiskMeasure.Mean, 4d), 0d);
    }

    /// <summary>
    /// Verifies the weighted reductions delegate to the named upstream statistics bit-exactly
    /// and report NaN at zero surviving weight or a single realization's variance.
    /// </summary>
    [TestMethod]
    public void Test_WeightedReductions_UpstreamParity()
    {
        // Arrange
        var values = new[] { 3d, 1d, 4d, 1.5d };
        var weights = new[] { 1d, 2d, 0.5d, 1.5d };
        var levels = new[] { 0.05d, 0.95d, 0.5d };

        // Act / Assert — the same upstream calls, bit for bit.
        Assert.AreEqual(Statistics.Mean(values, weights),
            EpistemicCriteriaEngine.WeightedMean(values, weights, values.Length), 0d);
        Assert.AreEqual(Statistics.Variance(values, weights, WeightType.Reliability),
            EpistemicCriteriaEngine.WeightedVariance(values, weights, values.Length), 0d);
        var expected = Statistics.Percentile(values, levels, weights, dataIsSorted: false);
        var actual = EpistemicCriteriaEngine.WeightedPercentiles(values, weights, values.Length, levels);
        CollectionAssert.AreEqual(expected, actual);

        // Degenerates: zero surviving weight, and a single realization's variance.
        Assert.IsTrue(double.IsNaN(EpistemicCriteriaEngine.WeightedMean(values, weights, 0)));
        Assert.IsTrue(double.IsNaN(EpistemicCriteriaEngine.WeightedVariance(new[] { 2d }, new[] { 1d }, 1)));
        Assert.AreEqual(Math.Pow(1d + 2d + 0.5d, 2) / (1d + 4d + 0.25d),
            EpistemicCriteriaEngine.KishEffectiveCount(new[] { 1d, 2d, 0.5d }, 3), 0d);
    }

    /// <summary>
    /// Verifies the epistemic tail average: the exact boundary split carrying the target
    /// weight, index tie-breaking, the direction-aware adverse side, and the degenerates.
    /// </summary>
    [TestMethod]
    public void Test_TailAverage_BoundarySplitAndTies()
    {
        // Arrange — total weight 4, tail 0.375 → target 1.5: the worst Minimize value (10)
        // contributes its full weight 1 and the next (8) exactly half of its weight.
        var values = new[] { 10d, 8d, 2d };
        var weights = new[] { 1d, 1d, 2d };

        // Act / Assert — (1·10 + 0.5·8)/1.5, the exact split arithmetic.
        Assert.AreEqual((1d * 10d + 0.5d * 8d) / 1.5d,
            EpistemicCriteriaEngine.TailAverage(values, weights, 3, 0.375d, ObjectiveDirection.Minimize), 0d);
        // The favorable tail under Maximize is the small-value side.
        Assert.AreEqual(2d,
            EpistemicCriteriaEngine.TailAverage(values, weights, 3, 0.5d, ObjectiveDirection.Maximize), 0d);
        // Value ties break by realization index: the first 5 fills the whole tail.
        Assert.AreEqual(5d, EpistemicCriteriaEngine.TailAverage(new[] { 5d, 5d, 1d },
            new[] { 1d, 1d, 1d }, 3, 1d / 3d, ObjectiveDirection.Minimize), 0d);
        // Degenerates: a single realization, equal values, and an empty sample.
        Assert.AreEqual(7d, EpistemicCriteriaEngine.TailAverage(new[] { 7d }, new[] { 2d }, 1,
            0.1d, ObjectiveDirection.Minimize), 0d);
        Assert.AreEqual(3d, EpistemicCriteriaEngine.TailAverage(new[] { 3d, 3d }, new[] { 1d, 1d }, 2,
            0.25d, ObjectiveDirection.Minimize), 0d);
        Assert.IsTrue(double.IsNaN(EpistemicCriteriaEngine.TailAverage(values, weights, 0, 0.1d,
            ObjectiveDirection.Minimize)));
    }

    /// <summary>
    /// Verifies the weighted extremes ignore zero-weight realizations and the Hurwicz blend
    /// reproduces the extremes bit-exactly at its endpoints.
    /// </summary>
    [TestMethod]
    public void Test_ExtremesAndHurwicz_ZeroWeightsAndEndpoints()
    {
        // Arrange — the worst raw value carries zero weight and must be ignored.
        var values = new[] { 100d, 1d, 50d };
        var weights = new[] { 0d, 1d, 1d };

        // Act
        double worst = EpistemicCriteriaEngine.WeightedExtreme(values, weights, 3, worst: true,
            ObjectiveDirection.Minimize);
        double best = EpistemicCriteriaEngine.WeightedExtreme(values, weights, 3, worst: false,
            ObjectiveDirection.Minimize);

        // Assert
        Assert.AreEqual(50d, worst, 0d, "A zero-weight realization is ignored.");
        Assert.AreEqual(1d, best, 0d);
        Assert.AreEqual(best, EpistemicCriteriaEngine.HurwiczBlend(best, worst, 1d), 0d,
            "α = 1 must reproduce the favorable extreme bit-exactly.");
        Assert.AreEqual(worst, EpistemicCriteriaEngine.HurwiczBlend(best, worst, 0d), 0d,
            "α = 0 must reproduce the adverse extreme bit-exactly.");
        Assert.AreEqual(0.25d * best + 0.75d * worst,
            EpistemicCriteriaEngine.HurwiczBlend(best, worst, 0.25d), 0d);
        Assert.IsTrue(double.IsNaN(EpistemicCriteriaEngine.WeightedExtreme(values,
            new[] { 0d, 0d, 0d }, 3, worst: true, ObjectiveDirection.Minimize)));
    }
}
