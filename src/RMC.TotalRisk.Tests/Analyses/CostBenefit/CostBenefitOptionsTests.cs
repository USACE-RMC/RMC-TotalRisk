using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Mathematics.Optimization;
using RMC.TotalRisk.Analyses.CostBenefit;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Tests.Analyses.CostBenefit;

/// <summary>
/// Tests the study declarations: the defaults, guards, the option-local validation matrix,
/// and the XML round trip of the full declaration set.
/// </summary>
[TestClass]
public class CostBenefitOptionsTests
{
    /// <summary>
    /// Verifies the defaults: the Total benefit stream, non-absorbing headline accounting,
    /// the 1% exceedance level, no shipped prices, the enforce policy, and the default
    /// objective vector (cost, monetized benefit, dispersion).
    /// </summary>
    [TestMethod]
    public void Test_Ctor_Defaults()
    {
        // Act
        var options = new CostBenefitOptions(50);

        // Assert
        Assert.AreEqual(50, options.PeriodYears);
        Assert.AreEqual(0d, options.DiscountRate);
        Assert.AreEqual(0, options.EvaluationYears.Count);
        Assert.AreEqual(RiskType.Total, options.BenefitRiskType);
        Assert.AreEqual(LifeCycleAccounting.NonAbsorbing, options.Accounting);
        Assert.AreEqual(1, options.AlphaLevels.Count);
        Assert.AreEqual(0.01d, options.AlphaLevels[0]);
        Assert.IsNull(options.Monetization);
        Assert.AreEqual(-1, options.LifeSafetyConsequenceType);
        Assert.IsTrue(double.IsNaN(options.WillingnessToPay), "No willingness to pay ships as a default.");
        Assert.AreEqual(string.Empty, options.WillingnessToPayVintage);
        Assert.AreEqual(AlarpProximity.JustBelowTolerableLimit, options.AlarpProximity);
        Assert.IsNull(options.AlarpBandThresholds);
        Assert.AreEqual(1e-4, options.IndividualRiskLimit);
        Assert.AreEqual(1d, options.EquityExponent);
        Assert.IsTrue(double.IsNaN(options.BaselineIndividualRisk));
        Assert.IsTrue(double.IsNaN(options.AlternativeIndividualRisk));
        Assert.AreEqual(DoNoHarmPolicy.Enforce, options.DoNoHarm);
        Assert.AreEqual(0, options.Constraints.Count);
        Assert.IsNull(options.EpsilonStudy);
        Assert.IsNull(options.McdaWeights);
        Assert.IsNull(options.Utility);
        Assert.IsNull(options.PmrmPartition);
        Assert.AreEqual(0.5d, options.HurwiczAlpha);
        Assert.AreEqual(1d, options.DispersionK);
        Assert.AreEqual(1, options.ChanceConstraintConfidenceLevels.Count);
        Assert.AreEqual(0.9d, options.ChanceConstraintConfidenceLevels[0]);
        Assert.AreEqual(0.1d, options.EpistemicTailAlpha);

        // The default objective vector: cost (min), monetized benefit (max), dispersion (min).
        Assert.AreEqual(3, options.Objectives.Count);
        Assert.AreEqual(EconomicMetric.PresentValueOfTotalCost, options.Objectives[0].Metric.EconomicMetric);
        Assert.AreEqual(ObjectiveDirection.Minimize, options.Objectives[0].Direction);
        Assert.AreEqual(EconomicMetric.MonetizedPresentValueBenefit, options.Objectives[1].Metric.EconomicMetric);
        Assert.AreEqual(ObjectiveDirection.Maximize, options.Objectives[1].Direction);
        Assert.AreEqual(RiskMeasure.StandardDeviation, options.Objectives[2].Metric.Measure);
        Assert.AreEqual(ObjectiveDirection.Minimize, options.Objectives[2].Direction);
        Assert.IsTrue(options.Validate().IsValid);
    }

    /// <summary>Verifies the construction guards mirror the life-cycle definition's.</summary>
    [TestMethod]
    public void Test_Ctor_Guards()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new CostBenefitOptions(0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new CostBenefitOptions(50, -0.01d));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new CostBenefitOptions(50, 0d, new[] { 50 }));
        Assert.ThrowsException<ArgumentException>(() => new CostBenefitOptions(50,
            objectives: new ObjectiveDeclaration[] { null! }));
        Assert.ThrowsException<ArgumentException>(() => new CostBenefitOptions(50,
            constraints: new CostBenefitConstraint[] { null! }));
    }

    /// <summary>Verifies the option-local validation matrix's representative rules.</summary>
    [TestMethod]
    public void Test_Validate_Matrix()
    {
        // Act / Assert — a bad exceedance level.
        (bool alphaValid, var alphaMessages) = new CostBenefitOptions(50, alphaLevels: new[] { 1.5d }).Validate();
        Assert.IsFalse(alphaValid);
        StringAssert.StartsWith(alphaMessages[0], "Error: Every declared exceedance level must lie in (0, 1).");

        // A weighted-sum weight count off the objective count.
        (bool weightValid, var weightMessages) = new CostBenefitOptions(50,
            mcdaWeights: new[] { 1d, 1d }).Validate();
        Assert.IsFalse(weightValid);
        StringAssert.StartsWith(weightMessages[0], "Error: The weighted-sum weight count must equal the objective count.");

        // A non-positive weight.
        Assert.IsFalse(new CostBenefitOptions(50, mcdaWeights: new[] { 1d, -1d, 1d }).Validate().IsValid);

        // A malformed band-threshold override.
        Assert.IsFalse(new CostBenefitOptions(50, alarpBandThresholds: new[] { 1d, 4d }).Validate().IsValid);
        Assert.IsFalse(new CostBenefitOptions(50, alarpBandThresholds: new[] { 4d, 1d, 20d }).Validate().IsValid);
        Assert.IsTrue(new CostBenefitOptions(50, alarpBandThresholds: new[] { 1d, 4d, 20d }).Validate().IsValid);

        // A negative willingness to pay, and out-of-range blends and levels.
        Assert.IsFalse(new CostBenefitOptions(50, willingnessToPay: -1d).Validate().IsValid);
        Assert.IsFalse(new CostBenefitOptions(50, hurwiczAlpha: 1.5d).Validate().IsValid);
        Assert.IsFalse(new CostBenefitOptions(50, epistemicTailAlpha: 0d).Validate().IsValid);
        Assert.IsFalse(new CostBenefitOptions(50,
            chanceConstraintConfidenceLevels: new[] { 1d }).Validate().IsValid);
        Assert.IsFalse(new CostBenefitOptions(50, lifeSafetyConsequenceType: -2).Validate().IsValid);

        // Child declarations bubble their own rules.
        Assert.IsFalse(new CostBenefitOptions(50,
            utility: new UtilityDeclaration(UtilityFunctionForm.PowerCrra, -1d)).Validate().IsValid);
        Assert.IsFalse(new CostBenefitOptions(50,
            pmrmPartition: new PmrmPartition(Array.Empty<double>())).Validate().IsValid);
        Assert.IsFalse(new CostBenefitOptions(50, constraints: new[]
        {
            new CostBenefitConstraint(CostBenefitMetric.ForEconomic(EconomicMetric.NetPresentValue),
                ConstraintType.EqualTo, 0d),
        }).Validate().IsValid);
    }

    /// <summary>Verifies the XML round trip of a fully declared option set.</summary>
    [TestMethod]
    public void Test_Serialization_RoundTrip_FullDeclarations()
    {
        // Arrange
        var options = new CostBenefitOptions(50, 0.035d, new[] { 10, 25 },
            benefitRiskType: RiskType.Excess,
            accounting: LifeCycleAccounting.Absorbing,
            alphaLevels: new[] { 0.01d, 0.002d },
            monetization: new ConsequenceMonetization(new[] { new MonetizationFactor(1, 7.5e6d, "VSL", "2023") }),
            lifeSafetyConsequenceType: 1,
            willingnessToPay: 5.8e6d,
            willingnessToPayVintage: "2009 USDOT",
            alarpProximity: AlarpProximity.JustAboveBroadlyAcceptable,
            alarpBandThresholds: new[] { 0.3d, 1d, 6d },
            individualRiskLimit: 2e-4,
            equityExponent: 1.5d,
            baselineIndividualRisk: 5e-4,
            alternativeIndividualRisk: 1e-4,
            doNoHarm: DoNoHarmPolicy.WarnOnly,
            constraints: new[]
            {
                new CostBenefitConstraint(CostBenefitMetric.ForRiskMeasure(RiskMeasure.Mean, RiskType.Excess, 1),
                    ConstraintType.LesserThanOrEqualTo, 1e-3),
            },
            epsilonStudy: EpsilonConstraintStudy.CreateTolerableLifeRiskStudy(1),
            mcdaWeights: new[] { 2d, 1d, 1d },
            utility: new UtilityDeclaration(UtilityFunctionForm.ExponentialCara, 0.001d),
            pmrmPartition: new PmrmPartition(new[] { 0.1d, 0.001d }),
            hurwiczAlpha: 0.25d,
            dispersionK: 2d,
            chanceConstraintConfidenceLevels: new[] { 0.8d, 0.95d },
            epistemicTailAlpha: 0.05d);

        // Act
        var restored = new CostBenefitOptions(options.ToXElement());

        // Assert — every scalar, list, and child declaration survives.
        Assert.AreEqual(50, restored.PeriodYears);
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(0.035d),
            BitConverter.DoubleToInt64Bits(restored.DiscountRate));
        CollectionAssert.AreEqual(new[] { 10, 25 }, new List<int>(restored.EvaluationYears));
        Assert.AreEqual(RiskType.Excess, restored.BenefitRiskType);
        Assert.AreEqual(LifeCycleAccounting.Absorbing, restored.Accounting);
        CollectionAssert.AreEqual(new[] { 0.01d, 0.002d }, new List<double>(restored.AlphaLevels));
        Assert.IsNotNull(restored.Monetization);
        Assert.AreEqual(1, restored.Monetization.Factors.Count);
        Assert.AreEqual("2023", restored.Monetization.Factors[0].Vintage);
        Assert.AreEqual(1, restored.LifeSafetyConsequenceType);
        Assert.AreEqual(5.8e6d, restored.WillingnessToPay);
        Assert.AreEqual("2009 USDOT", restored.WillingnessToPayVintage);
        Assert.AreEqual(AlarpProximity.JustAboveBroadlyAcceptable, restored.AlarpProximity);
        Assert.IsNotNull(restored.AlarpBandThresholds);
        CollectionAssert.AreEqual(new[] { 0.3d, 1d, 6d }, new List<double>(restored.AlarpBandThresholds));
        Assert.AreEqual(2e-4, restored.IndividualRiskLimit);
        Assert.AreEqual(1.5d, restored.EquityExponent);
        Assert.AreEqual(5e-4, restored.BaselineIndividualRisk);
        Assert.AreEqual(1e-4, restored.AlternativeIndividualRisk);
        Assert.AreEqual(DoNoHarmPolicy.WarnOnly, restored.DoNoHarm);
        Assert.AreEqual(3, restored.Objectives.Count);
        Assert.AreEqual(1, restored.Constraints.Count);
        Assert.AreEqual(1e-3, restored.Constraints[0].Threshold);
        Assert.IsNotNull(restored.EpsilonStudy);
        Assert.AreEqual(1, restored.EpsilonStudy.FixedConstraints.Count);
        Assert.IsNotNull(restored.McdaWeights);
        CollectionAssert.AreEqual(new[] { 2d, 1d, 1d }, new List<double>(restored.McdaWeights));
        Assert.IsNotNull(restored.Utility);
        Assert.AreEqual(0.001d, restored.Utility.RiskAversion);
        Assert.IsNotNull(restored.PmrmPartition);
        Assert.AreEqual(2, restored.PmrmPartition.ExceedanceBoundaries.Count);
        Assert.AreEqual(0.25d, restored.HurwiczAlpha);
        Assert.AreEqual(2d, restored.DispersionK);
        CollectionAssert.AreEqual(new[] { 0.8d, 0.95d },
            new List<double>(restored.ChanceConstraintConfidenceLevels));
        Assert.AreEqual(0.05d, restored.EpistemicTailAlpha);
        Assert.ThrowsException<ArgumentNullException>(() => new CostBenefitOptions(null!));
    }

    /// <summary>
    /// Verifies the default form round-trips: absent optional declarations read back as
    /// absent, and the serialized default objectives restore with identical content.
    /// </summary>
    [TestMethod]
    public void Test_Serialization_RoundTrip_Defaults()
    {
        // Arrange
        var options = new CostBenefitOptions(30);

        // Act
        var restored = new CostBenefitOptions(options.ToXElement());

        // Assert
        Assert.AreEqual(30, restored.PeriodYears);
        Assert.IsNull(restored.Monetization);
        Assert.IsNull(restored.AlarpBandThresholds);
        Assert.IsNull(restored.EpsilonStudy);
        Assert.IsNull(restored.McdaWeights);
        Assert.IsNull(restored.Utility);
        Assert.IsNull(restored.PmrmPartition);
        Assert.IsTrue(double.IsNaN(restored.WillingnessToPay));
        Assert.AreEqual(3, restored.Objectives.Count);
        Assert.AreEqual(EconomicMetric.MonetizedPresentValueBenefit,
            restored.Objectives[1].Metric.EconomicMetric);
        Assert.IsTrue(restored.Validate().IsValid);
    }
}
