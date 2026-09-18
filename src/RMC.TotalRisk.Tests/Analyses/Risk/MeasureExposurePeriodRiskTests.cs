using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Data.Statistics;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Tests.Analyses.Risk;

/// <summary>
/// Unit tests for <see cref="RiskAnalysis.MeasureExposurePeriodRisk"/> — the exposure-period and
/// life-cycle conversions: argument guards and null conditions, the exact binomial and annuity
/// identities against independently evaluated forms, the equivalent-annual identity, and the
/// stored-weight honoring.
/// </summary>
[TestClass]
public class MeasureExposurePeriodRiskTests
{
    /// <summary>Builds the uncertain-consequence single-component analysis.</summary>
    private static RiskAnalysis Build(int realizations, bool meanOnly = false)
    {
        var hazard = new TabularHazard
        {
            Name = "Stage Frequency",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            NoUncertaintyFunction = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0.999d, new Deterministic(0d)),
                    new UncertainOrdinate(0.5d, new Deterministic(10d)),
                    new UncertainOrdinate(0.001d, new Deterministic(30d)),
                },
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
        };
        var fragility = new TabularResponse
        {
            Name = "Fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(10d, new Triangular(0d, 0.05d, 0.1d)),
                    new UncertainOrdinate(20d, new Triangular(0.7d, 0.9d, 1d)),
                },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Triangular),
        };
        var consequence = new TabularConsequence
        {
            Name = "Failure Loss",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Normal(0d, 0d)), new UncertainOrdinate(30d, new Normal(1000d, 100d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Normal),
        };

        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = hazard;
        component.AddFailureMode(new FailureMode(null, null, fragility, consequence));
        var analysis = new RiskAnalysis(new[] { component });
        analysis.Options.EstimateMeanRiskOnly = meanOnly;
        if (!meanOnly) analysis.Options.Realizations = realizations;
        return analysis;
    }

    /// <summary>Verifies the argument guards and the null conditions.</summary>
    [TestMethod]
    public void Test_Guards_AndNullConditions()
    {
        // Arrange
        var analysis = Build(100);

        // Unestimated → null; guards throw regardless.
        Assert.IsNull(analysis.MeasureExposurePeriodRisk(50));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => analysis.MeasureExposurePeriodRisk(0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => analysis.MeasureExposurePeriodRisk(50, -0.01d));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => analysis.MeasureExposurePeriodRisk(50, double.NaN));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => analysis.MeasureExposurePeriodRisk(50, 0.03d, RiskType.Total, componentIndex: 5));

        // A mean-only run has no ensemble → null.
        var meanOnly = Build(100, meanOnly: true);
        meanOnly.RunAsync().GetAwaiter().GetResult();
        Assert.IsNull(meanOnly.MeasureExposurePeriodRisk(50));
    }

    /// <summary>
    /// Verifies the exact identities on a stored ensemble: the per-realization binomial and
    /// annuity conversions match independently evaluated closed forms, the reductions match
    /// direct recomputation, the equivalent annual reproduces the annual expected consequence,
    /// the undiscounted present value equals the cumulative bit-for-bit, and a one-year period
    /// reproduces the annual failure probability.
    /// </summary>
    [TestMethod]
    public void Test_ExactIdentities_Unweighted()
    {
        // Arrange
        const int T = 50;
        const double r = 0.03d;
        var analysis = Build(100);
        analysis.RunAsync().GetAwaiter().GetResult();
        var results = analysis.RiskResults!;

        // Act
        var query = analysis.MeasureExposurePeriodRisk(T, r)!;

        // Assert — recompute per realization through the independent closed forms
        // (Math.Pow, not the query's log-space route).
        var expectedPt = new List<double>();
        var expectedPv = new List<double>();
        double annuity = (1d - Math.Pow(1d + r, -T)) / r;
        for (int i = 0; i < results.Count; i++)
        {
            double p = results[i]!.Fail.TotalProbability;
            double m = results[i]!.Total.Mean;
            expectedPt.Add(1d - Math.Pow(1d - p, T));
            expectedPv.Add(m * annuity);
        }
        Assert.AreEqual(100, query.ValidRealizations);

        double meanPt = 0d;
        double meanPv = 0d;
        for (int i = 0; i < expectedPt.Count; i++) { meanPt += expectedPt[i]; meanPv += expectedPv[i]; }
        meanPt /= expectedPt.Count;
        meanPv /= expectedPv.Count;
        Assert.AreEqual(meanPt, query.PeriodFailureProbability.Mean, 1e-12 * meanPt,
            "The P_T mean must match the independent binomial evaluation.");
        Assert.AreEqual(meanPv, query.PresentValueOfExpectedConsequences.Mean, 1e-12 * meanPv,
            "The present-value mean must match the independent annuity evaluation.");
        double lowerLevel = (1d - analysis.Options.ConfidenceIntervalWidth) / 2d;
        var levels = new[] { lowerLevel, 0.5d, 1d - lowerLevel };
        var ptPercentiles = Statistics.Percentile(expectedPt, levels);
        Assert.AreEqual(ptPercentiles[0], query.PeriodFailureProbability.Lower, 1e-12 * Math.Abs(ptPercentiles[0]));
        Assert.AreEqual(ptPercentiles[2], query.PeriodFailureProbability.Upper, 1e-12 * Math.Abs(ptPercentiles[2]));

        // The equivalent annual reproduces the annual expected consequence (the identity).
        double meanM = meanPv / annuity;
        Assert.AreEqual(meanM, query.EquivalentAnnualConsequence.Mean, 1e-12 * meanM);
        // The cumulative is T·m.
        Assert.AreEqual(T * meanM, query.CumulativeExpectedConsequence.Mean, 1e-9 * T * meanM);

        // Undiscounted: the present value IS the cumulative, bit for bit.
        var undiscounted = analysis.MeasureExposurePeriodRisk(T, 0d)!;
        Assert.AreEqual(undiscounted.CumulativeExpectedConsequence.Mean,
            undiscounted.PresentValueOfExpectedConsequences.Mean, 0d);

        // A one-year period reproduces the annual failure probability.
        var oneYear = analysis.MeasureExposurePeriodRisk(1)!;
        double annualMean = 0d;
        for (int i = 0; i < results.Count; i++) annualMean += results[i]!.Fail.TotalProbability;
        annualMean /= results.Count;
        Assert.AreEqual(annualMean, oneYear.PeriodFailureProbability.Mean, 1e-14 * annualMean);

        // The period probability grows with the period.
        Assert.IsTrue(query.PeriodFailureProbability.Mean > oneYear.PeriodFailureProbability.Mean);

        // The single component's scope equals the system scope on this fixture.
        var componentScope = analysis.MeasureExposurePeriodRisk(T, r, RiskType.Total, componentIndex: 0)!;
        Assert.AreEqual(query.PeriodFailureProbability.Mean, componentScope.PeriodFailureProbability.Mean, 0d);
    }

    /// <summary>
    /// Verifies stored realization weights are honored: the weighted means match direct
    /// weighted recomputation and the percentiles match the upstream weighted reducer exactly.
    /// </summary>
    [TestMethod]
    public void Test_Weighted_HonorsStoredWeights()
    {
        // Arrange — a run with post-hoc weights (the house pattern).
        const int T = 30;
        var analysis = Build(100);
        analysis.RunAsync().GetAwaiter().GetResult();
        var results = analysis.RiskResults!;
        var weights = new double[100];
        for (int i = 0; i < weights.Length; i++) weights[i] = 0.25d + (37 * i) % 11;
        results.SetRealizationWeights(weights);

        // Act
        var query = analysis.MeasureExposurePeriodRisk(T)!;

        // Assert — the weighted mean against direct recomputation.
        var expectedPt = new List<double>();
        for (int i = 0; i < results.Count; i++)
        {
            expectedPt.Add(1d - Math.Pow(1d - results[i]!.Fail.TotalProbability, T));
        }
        double weightedSum = 0d;
        double weightTotal = 0d;
        for (int i = 0; i < expectedPt.Count; i++)
        {
            weightedSum += weights[i] * expectedPt[i];
            weightTotal += weights[i];
        }
        double expectedMean = weightedSum / weightTotal;
        Assert.AreEqual(expectedMean, query.PeriodFailureProbability.Mean, 1e-12 * expectedMean);

        // The weighted percentiles against the upstream weighted reducer.
        double lowerLevel = (1d - analysis.Options.ConfidenceIntervalWidth) / 2d;
        var levels = new[] { lowerLevel, 0.5d, 1d - lowerLevel };
        var expectedPercentiles = Statistics.Percentile(expectedPt, levels, weights);
        Assert.AreEqual(expectedPercentiles[0], query.PeriodFailureProbability.Lower, 1e-12 * Math.Abs(expectedPercentiles[0]));
        Assert.AreEqual(expectedPercentiles[1], query.PeriodFailureProbability.Median, 1e-12 * Math.Abs(expectedPercentiles[1]));
        Assert.AreEqual(expectedPercentiles[2], query.PeriodFailureProbability.Upper, 1e-12 * Math.Abs(expectedPercentiles[2]));
    }
}
