using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.CostBenefit;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Tests.Analyses.CostBenefit;

/// <summary>
/// Tests the metric selector union: the two factories, guards, the validation matrix, and
/// the XML round trip of both kinds.
/// </summary>
[TestClass]
public class CostBenefitMetricTests
{
    /// <summary>Verifies the economics factory and its echo.</summary>
    [TestMethod]
    public void Test_ForEconomic_Echoed()
    {
        // Act
        var metric = CostBenefitMetric.ForEconomic(EconomicMetric.BenefitCostRatio);

        // Assert
        Assert.IsTrue(metric.IsEconomic);
        Assert.AreEqual(EconomicMetric.BenefitCostRatio, metric.EconomicMetric);
        Assert.IsTrue(metric.Validate().IsValid);
    }

    /// <summary>Verifies the risk-measure factory defaults, echoes, and guards.</summary>
    [TestMethod]
    public void Test_ForRiskMeasure_DefaultsAndGuards()
    {
        // Act
        var defaulted = CostBenefitMetric.ForRiskMeasure(RiskMeasure.Mean, RiskType.Excess);
        var full = CostBenefitMetric.ForRiskMeasure(RiskMeasure.ConditionalValueAtRisk, RiskType.Total, 1,
            MetricBasis.HorizonEquivalentAnnual, LifeCycleAccounting.Absorbing,
            MetricForm.ReductionVsBaseline, 0.01d);

        // Assert — the defaults: annualized level under non-absorbing accounting, study α.
        Assert.IsFalse(defaulted.IsEconomic);
        Assert.AreEqual(RiskMeasure.Mean, defaulted.Measure);
        Assert.AreEqual(RiskType.Excess, defaulted.RiskType);
        Assert.AreEqual(0, defaulted.ConsequenceType);
        Assert.AreEqual(MetricBasis.AnnualizedPerEpoch, defaulted.Basis);
        Assert.AreEqual(LifeCycleAccounting.NonAbsorbing, defaulted.Accounting);
        Assert.AreEqual(MetricForm.Level, defaulted.Form);
        Assert.IsTrue(double.IsNaN(defaulted.Alpha));
        Assert.IsTrue(defaulted.Validate().IsValid);

        // The full form echoes.
        Assert.AreEqual(1, full.ConsequenceType);
        Assert.AreEqual(MetricBasis.HorizonEquivalentAnnual, full.Basis);
        Assert.AreEqual(LifeCycleAccounting.Absorbing, full.Accounting);
        Assert.AreEqual(MetricForm.ReductionVsBaseline, full.Form);
        Assert.AreEqual(0.01d, full.Alpha);

        // The factory guards.
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => CostBenefitMetric.ForRiskMeasure(RiskMeasure.Mean, RiskType.Total, consequenceType: -1));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => CostBenefitMetric.ForRiskMeasure(RiskMeasure.Mean, RiskType.Total, alpha: 1.5d));
    }

    /// <summary>Verifies the XML round trip of both kinds preserves every member.</summary>
    [TestMethod]
    public void Test_Serialization_RoundTrip_BothKinds()
    {
        // Arrange
        var economic = CostBenefitMetric.ForEconomic(EconomicMetric.CostPerStatisticalLifeSavedAdjusted);
        var riskMeasure = CostBenefitMetric.ForRiskMeasure(RiskMeasure.ConditionalMean, RiskType.Excess, 1,
            MetricBasis.AnnualizedPerEpoch, LifeCycleAccounting.NonAbsorbing, MetricForm.Level, 0.01d);

        // Act
        var economicRestored = new CostBenefitMetric(economic.ToXElement());
        var riskRestored = new CostBenefitMetric(riskMeasure.ToXElement());

        // Assert
        Assert.IsTrue(economicRestored.IsEconomic);
        Assert.AreEqual(EconomicMetric.CostPerStatisticalLifeSavedAdjusted, economicRestored.EconomicMetric);
        Assert.IsFalse(riskRestored.IsEconomic);
        Assert.AreEqual(RiskMeasure.ConditionalMean, riskRestored.Measure);
        Assert.AreEqual(RiskType.Excess, riskRestored.RiskType);
        Assert.AreEqual(1, riskRestored.ConsequenceType);
        Assert.AreEqual(MetricBasis.AnnualizedPerEpoch, riskRestored.Basis);
        Assert.AreEqual(LifeCycleAccounting.NonAbsorbing, riskRestored.Accounting);
        Assert.AreEqual(MetricForm.Level, riskRestored.Form);
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(0.01d),
            BitConverter.DoubleToInt64Bits(riskRestored.Alpha));
        Assert.ThrowsException<ArgumentNullException>(() => new CostBenefitMetric(null!));
    }

    /// <summary>Verifies the NaN study-level alpha survives the round trip.</summary>
    [TestMethod]
    public void Test_Serialization_NaNAlpha_RoundTrips()
    {
        // Arrange
        var metric = CostBenefitMetric.ForRiskMeasure(RiskMeasure.ValueAtRisk, RiskType.Total);

        // Act
        var restored = new CostBenefitMetric(metric.ToXElement());

        // Assert
        Assert.IsTrue(double.IsNaN(restored.Alpha), "NaN must read back as the study's level.");
    }

    /// <summary>
    /// Verifies the horizon-basis legality rules: the trajectory aggregates back the Mean
    /// measure on the Total, Excess, and Fail streams only, so any other measure or stream
    /// on a horizon basis is refused, while the annualized basis serves every measure and
    /// stream.
    /// </summary>
    [TestMethod]
    public void Test_Validate_HorizonBasisLegality()
    {
        // Act
        (bool sdOnHorizonValid, List<string> sdMessages) = CostBenefitMetric.ForRiskMeasure(
            RiskMeasure.StandardDeviation, RiskType.Total, basis: MetricBasis.HorizonPresentValue).Validate();
        (bool backgroundOnHorizonValid, List<string> streamMessages) = CostBenefitMetric.ForRiskMeasure(
            RiskMeasure.Mean, RiskType.Background, basis: MetricBasis.HorizonCumulative).Validate();
        (bool meanOnHorizonValid, _) = CostBenefitMetric.ForRiskMeasure(
            RiskMeasure.Mean, RiskType.Excess, basis: MetricBasis.HorizonEquivalentAnnual,
            accounting: LifeCycleAccounting.Absorbing).Validate();
        (bool annualizedValid, _) = CostBenefitMetric.ForRiskMeasure(
            RiskMeasure.StandardDeviation, RiskType.Background).Validate();

        // Assert
        Assert.IsFalse(sdOnHorizonValid);
        Assert.IsTrue(sdMessages[0].StartsWith("Error: The metric declares the StandardDeviation measure",
            StringComparison.Ordinal));
        Assert.IsFalse(backgroundOnHorizonValid);
        Assert.IsTrue(streamMessages[0].StartsWith("Error: The metric declares the Background stream",
            StringComparison.Ordinal));
        Assert.IsTrue(meanOnHorizonValid, "Mean on an aggregate stream is the legal horizon reading.");
        Assert.IsTrue(annualizedValid, "The annualized basis serves every measure and stream.");
    }
}
