using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.CostBenefit.Support;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Tests.Analyses.CostBenefit.Support;

/// <summary>
/// Tests the cost-effectiveness formulary: total expected annual cost, the
/// cost-per-statistical-life-saved family with its clamp and NaN discipline, cost per
/// statistical failure prevented, the disproportionality ratio, and the ALARP band tables
/// and labels.
/// </summary>
[TestClass]
public class CostBenefitFormularyTests
{
    /// <summary>Verifies the total expected annual cost sums and propagates NaN.</summary>
    [TestMethod]
    public void Test_TotalExpectedAnnualCost_SumAndNaN()
    {
        // Act / Assert
        Assert.AreEqual(150d, CostBenefitFormulary.TotalExpectedAnnualCost(120d, 30d));
        Assert.IsTrue(double.IsNaN(CostBenefitFormulary.TotalExpectedAnnualCost(120d, double.NaN)));
    }

    /// <summary>
    /// Verifies the unadjusted ratio at the Appendix L hand values and its NaN discipline: a
    /// non-positive or unavailable life-loss reduction is undefined, never negative.
    /// </summary>
    [TestMethod]
    public void Test_CostPerLifeSavedUnadjusted_HandValueAndNaN()
    {
        // Act / Assert — 120 / 0.004 = 30,000 exactly.
        Assert.AreEqual(30000d, CostBenefitFormulary.CostPerLifeSavedUnadjusted(120d, 0.004d));
        Assert.IsTrue(double.IsNaN(CostBenefitFormulary.CostPerLifeSavedUnadjusted(120d, 0d)));
        Assert.IsTrue(double.IsNaN(CostBenefitFormulary.CostPerLifeSavedUnadjusted(120d, -0.001d)));
        Assert.IsTrue(double.IsNaN(CostBenefitFormulary.CostPerLifeSavedUnadjusted(120d, double.NaN)));
    }

    /// <summary>
    /// Verifies the adjusted ratio at the Appendix L hand values, the zero clamp on the
    /// numerator, and NaN propagation through an unavailable term.
    /// </summary>
    [TestMethod]
    public void Test_CostPerLifeSavedAdjusted_HandValueClampAndNaN()
    {
        // Act / Assert — (120 − 30 − 10) / 0.004 = 20,000 exactly.
        Assert.AreEqual(20000d, CostBenefitFormulary.CostPerLifeSavedAdjusted(120d, 30d, 10d, 0.004d));

        // The negative-numerator proviso: reductions exceeding the cost clamp to exactly zero.
        Assert.AreEqual(0d, CostBenefitFormulary.CostPerLifeSavedAdjusted(120d, 130d, 10d, 0.004d));

        // NaN discipline: the denominator rule and term propagation.
        Assert.IsTrue(double.IsNaN(CostBenefitFormulary.CostPerLifeSavedAdjusted(120d, 30d, 10d, 0d)));
        Assert.IsTrue(double.IsNaN(CostBenefitFormulary.CostPerLifeSavedAdjusted(120d, double.NaN, 10d, 0.004d)));
    }

    /// <summary>
    /// Verifies the equity weighting: the floored individual-risk ratio raised to the
    /// exponent, with the floor engaging on one side, a zero exponent recovering the adjusted
    /// ratio, and NaN propagation.
    /// </summary>
    [TestMethod]
    public void Test_EquityWeightedCostPerLifeSaved_FloorAndExponent()
    {
        // Arrange — adjusted 20,000; baseline risk 1e-3, alternative 1e-4, floor 1e-4.
        double adjusted = 20000d;

        // Act / Assert — weight (1e-3 / 1e-4)^1 = 10 → 2,000.
        Assert.AreEqual(2000d, CostBenefitFormulary.EquityWeightedCostPerLifeSaved(
            adjusted, 1e-3, 1e-4, 1e-4, 1d), 1e-12);

        // The floor engages on the alternative side only: 1e-6 floors to 1e-4, same weight.
        Assert.AreEqual(2000d, CostBenefitFormulary.EquityWeightedCostPerLifeSaved(
            adjusted, 1e-3, 1e-6, 1e-4, 1d), 1e-12);

        // The exponent sweep: n = 0 recovers the adjusted ratio; n = 2 squares the weight.
        Assert.AreEqual(adjusted, CostBenefitFormulary.EquityWeightedCostPerLifeSaved(
            adjusted, 1e-3, 1e-4, 1e-4, 0d));
        Assert.AreEqual(200d, CostBenefitFormulary.EquityWeightedCostPerLifeSaved(
            adjusted, 1e-3, 1e-4, 1e-4, 2d), 1e-12);
        Assert.IsTrue(double.IsNaN(CostBenefitFormulary.EquityWeightedCostPerLifeSaved(
            double.NaN, 1e-3, 1e-4, 1e-4, 1d)));
    }

    /// <summary>Verifies the failure-prevention ratio and its NaN discipline.</summary>
    [TestMethod]
    public void Test_CostPerFailurePrevented_HandValueAndNaN()
    {
        // Act / Assert — 120 / 1e-3 = 120,000 exactly.
        Assert.AreEqual(120000d, CostBenefitFormulary.CostPerFailurePrevented(120d, 1e-3));
        Assert.IsTrue(double.IsNaN(CostBenefitFormulary.CostPerFailurePrevented(120d, 0d)));
        Assert.IsTrue(double.IsNaN(CostBenefitFormulary.CostPerFailurePrevented(120d, -1e-4)));
        Assert.IsTrue(double.IsNaN(CostBenefitFormulary.CostPerFailurePrevented(120d, double.NaN)));
    }

    /// <summary>
    /// Verifies the absorbing adjusted ratio: the present-value composition, the
    /// family-consistency clamp, and the denominator rule.
    /// </summary>
    [TestMethod]
    public void Test_AbsorbingCostPerLifeSavedAdjusted_CompositionClampAndNaN()
    {
        // Act / Assert — (1200 − 300 − 100) / 0.08 = 10,000 exactly.
        Assert.AreEqual(10000d, CostBenefitFormulary.AbsorbingCostPerLifeSavedAdjusted(1200d, 300d, 100d, 0.08d));
        Assert.AreEqual(0d, CostBenefitFormulary.AbsorbingCostPerLifeSavedAdjusted(1200d, 1300d, 100d, 0.08d));
        Assert.IsTrue(double.IsNaN(CostBenefitFormulary.AbsorbingCostPerLifeSavedAdjusted(1200d, 300d, 100d, 0d)));
        Assert.IsTrue(double.IsNaN(CostBenefitFormulary.AbsorbingCostPerLifeSavedAdjusted(1200d, 300d, double.NaN, 0.08d)));
    }

    /// <summary>Verifies the disproportionality ratio and its willingness-to-pay gate.</summary>
    [TestMethod]
    public void Test_DisproportionalityRatio_GateAndValue()
    {
        // Act / Assert
        Assert.AreEqual(20000d / 5.8e6, CostBenefitFormulary.DisproportionalityRatio(20000d, 5.8e6), 1e-15);
        Assert.IsTrue(double.IsNaN(CostBenefitFormulary.DisproportionalityRatio(20000d, double.NaN)));
        Assert.IsTrue(double.IsNaN(CostBenefitFormulary.DisproportionalityRatio(20000d, 0d)));
    }

    /// <summary>Verifies the two regulation band tables and the undefined-proximity guard.</summary>
    [TestMethod]
    public void Test_AlarpBandThresholds_Tables()
    {
        // Act
        var below = CostBenefitFormulary.AlarpBandThresholds(AlarpProximity.JustBelowTolerableLimit);
        var above = CostBenefitFormulary.AlarpBandThresholds(AlarpProximity.JustAboveBroadlyAcceptable);

        // Assert
        CollectionAssert.AreEqual(new[] { 1d, 4d, 20d }, (System.Collections.ICollection)below);
        CollectionAssert.AreEqual(new[] { 0.3d, 1d, 6d }, (System.Collections.ICollection)above);
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => CostBenefitFormulary.AlarpBandThresholds((AlarpProximity)99));
    }

    /// <summary>
    /// Verifies the band labels: a boundary ratio reports the stronger band, NaN reports an
    /// empty label, and malformed tables are refused.
    /// </summary>
    [TestMethod]
    public void Test_AlarpBandLabel_BoundariesAndGuards()
    {
        // Arrange
        var thresholds = CostBenefitFormulary.AlarpBandThresholds(AlarpProximity.JustBelowTolerableLimit);

        // Act / Assert
        Assert.AreEqual("Very Strong", CostBenefitFormulary.AlarpBandLabel(0.5d, thresholds));
        Assert.AreEqual("Very Strong", CostBenefitFormulary.AlarpBandLabel(1d, thresholds));
        Assert.AreEqual("Strong", CostBenefitFormulary.AlarpBandLabel(4d, thresholds));
        Assert.AreEqual("Moderate", CostBenefitFormulary.AlarpBandLabel(20d, thresholds));
        Assert.AreEqual("Poor", CostBenefitFormulary.AlarpBandLabel(20.0001d, thresholds));
        Assert.AreEqual(string.Empty, CostBenefitFormulary.AlarpBandLabel(double.NaN, thresholds));
        Assert.ThrowsException<ArgumentNullException>(
            () => CostBenefitFormulary.AlarpBandLabel(1d, null!));
        Assert.ThrowsException<ArgumentException>(
            () => CostBenefitFormulary.AlarpBandLabel(1d, new[] { 1d, 4d }));
    }
}
