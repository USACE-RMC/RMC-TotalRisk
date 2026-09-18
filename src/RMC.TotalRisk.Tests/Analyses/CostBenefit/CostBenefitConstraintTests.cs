using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Mathematics.Optimization;
using RMC.TotalRisk.Analyses.CostBenefit;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Tests.Analyses.CostBenefit;

/// <summary>
/// Tests the constraint declaration: guards, the inequality-only sense rule, and the XML
/// round trip.
/// </summary>
[TestClass]
public class CostBenefitConstraintTests
{
    /// <summary>Verifies the construction guard and the default scope.</summary>
    [TestMethod]
    public void Test_Ctor_GuardAndDefaultScope()
    {
        // Act
        var constraint = new CostBenefitConstraint(
            CostBenefitMetric.ForRiskMeasure(RiskMeasure.Mean, RiskType.Excess, 1),
            ConstraintType.LesserThanOrEqualTo, 1e-3);

        // Assert
        Assert.AreEqual(ConstraintScope.EveryEpoch, constraint.Scope);
        Assert.AreEqual(1e-3, constraint.Threshold);
        Assert.IsTrue(constraint.Validate().IsValid);
        Assert.ThrowsException<ArgumentNullException>(() => new CostBenefitConstraint(
            null!, ConstraintType.LesserThanOrEqualTo, 1e-3));
    }

    /// <summary>Verifies the validation matrix: equality sense and non-finite threshold refuse.</summary>
    [TestMethod]
    public void Test_Validate_Matrix()
    {
        // Arrange
        var metric = CostBenefitMetric.ForEconomic(EconomicMetric.AnnualizedFailureProbability);

        // Act
        var equality = new CostBenefitConstraint(metric, ConstraintType.EqualTo, 1e-4);
        var nonFinite = new CostBenefitConstraint(metric, ConstraintType.LesserThanOrEqualTo, double.NaN);

        // Assert
        (bool equalityValid, var equalityMessages) = equality.Validate();
        Assert.IsFalse(equalityValid);
        StringAssert.StartsWith(equalityMessages[0], "Error: The constraint sense must be one of the two inequalities.");
        (bool finiteValid, var finiteMessages) = nonFinite.Validate();
        Assert.IsFalse(finiteValid);
        StringAssert.StartsWith(finiteMessages[0], "Error: The constraint threshold must be finite.");
    }

    /// <summary>Verifies the XML round trip preserves the sense, threshold, scope, and metric.</summary>
    [TestMethod]
    public void Test_Serialization_RoundTrip()
    {
        // Arrange
        var constraint = new CostBenefitConstraint(
            CostBenefitMetric.ForRiskMeasure(RiskMeasure.ConditionalValueAtRisk, RiskType.Total, 0,
                MetricBasis.AnnualizedPerEpoch, alpha: 0.01d),
            ConstraintType.GreaterThanOrEqualTo, 2.5d, ConstraintScope.Horizon);

        // Act
        var restored = new CostBenefitConstraint(constraint.ToXElement());

        // Assert
        Assert.AreEqual(ConstraintType.GreaterThanOrEqualTo, restored.Sense);
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(2.5d),
            BitConverter.DoubleToInt64Bits(restored.Threshold));
        Assert.AreEqual(ConstraintScope.Horizon, restored.Scope);
        Assert.AreEqual(RiskMeasure.ConditionalValueAtRisk, restored.Metric.Measure);
        Assert.AreEqual(0.01d, restored.Metric.Alpha);
        Assert.ThrowsException<ArgumentNullException>(() => new CostBenefitConstraint(null!));
    }

    /// <summary>
    /// Verifies the metric-aware scope default: a null scope resolves to every epoch for an
    /// annualized metric and to the horizon for a whole-horizon metric, so natural
    /// declarations need no explicit scope.
    /// </summary>
    [TestMethod]
    public void Test_Ctor_ScopeDefault_MetricAware()
    {
        // Act
        var annualized = new CostBenefitConstraint(
            CostBenefitMetric.ForRiskMeasure(RiskMeasure.Mean, RiskType.Excess),
            ConstraintType.LesserThanOrEqualTo, 1e-3);
        var economic = new CostBenefitConstraint(
            CostBenefitMetric.ForEconomic(EconomicMetric.AnnualizedFailureProbability),
            ConstraintType.LesserThanOrEqualTo, 1e-4);
        var horizonBasis = new CostBenefitConstraint(
            CostBenefitMetric.ForRiskMeasure(RiskMeasure.Mean, RiskType.Total,
                basis: MetricBasis.HorizonPresentValue),
            ConstraintType.LesserThanOrEqualTo, 100d);

        // Assert — and every defaulted declaration validates.
        Assert.AreEqual(ConstraintScope.EveryEpoch, annualized.Scope);
        Assert.AreEqual(ConstraintScope.Horizon, economic.Scope);
        Assert.AreEqual(ConstraintScope.Horizon, horizonBasis.Scope);
        Assert.IsTrue(annualized.Validate().IsValid);
        Assert.IsTrue(economic.Validate().IsValid);
        Assert.IsTrue(horizonBasis.Validate().IsValid);
    }

    /// <summary>
    /// Verifies the scope-and-metric compatibility rule: a whole-horizon metric checked per
    /// epoch is refused, and an annualized metric checked at the horizon is refused.
    /// </summary>
    [TestMethod]
    public void Test_Validate_ScopeMetricMismatch_Refused()
    {
        // Act
        (bool horizonAtEveryEpochValid, List<string> horizonMessages) = new CostBenefitConstraint(
            CostBenefitMetric.ForEconomic(EconomicMetric.PresentValueOfTotalCost),
            ConstraintType.LesserThanOrEqualTo, 100d, ConstraintScope.EveryEpoch).Validate();
        (bool annualizedAtHorizonValid, List<string> annualizedMessages) = new CostBenefitConstraint(
            CostBenefitMetric.ForRiskMeasure(RiskMeasure.Mean, RiskType.Excess),
            ConstraintType.LesserThanOrEqualTo, 1e-3, ConstraintScope.Horizon).Validate();

        // Assert
        Assert.IsFalse(horizonAtEveryEpochValid);
        Assert.IsTrue(horizonMessages[0].StartsWith(
            "Error: The constraint checks a whole-horizon metric", StringComparison.Ordinal));
        Assert.IsFalse(annualizedAtHorizonValid);
        Assert.IsTrue(annualizedMessages[0].StartsWith(
            "Error: The constraint checks an annualized metric", StringComparison.Ordinal));
    }
}
