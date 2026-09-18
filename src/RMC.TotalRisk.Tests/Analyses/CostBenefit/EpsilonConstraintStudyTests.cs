using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Mathematics.Optimization;
using RMC.TotalRisk.Analyses.CostBenefit;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Tests.Analyses.CostBenefit;

/// <summary>
/// Tests the ε-constraint study declaration: guards, the three templates, and the XML round
/// trip with and without an explicit grid.
/// </summary>
[TestClass]
public class EpsilonConstraintStudyTests
{
    /// <summary>Builds a minimal primary objective.</summary>
    /// <returns>The objective.</returns>
    private static ObjectiveDeclaration Primary()
    {
        return new ObjectiveDeclaration("Total expected annual cost",
            CostBenefitMetric.ForEconomic(EconomicMetric.TotalExpectedAnnualCost),
            ObjectiveDirection.Minimize);
    }

    /// <summary>Verifies the construction guards.</summary>
    [TestMethod]
    public void Test_Ctor_Guards()
    {
        // Arrange
        var epsilon = CostBenefitMetric.ForRiskMeasure(RiskMeasure.ConditionalMean, RiskType.Excess);

        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() => new EpsilonConstraintStudy(null!, epsilon));
        Assert.ThrowsException<ArgumentNullException>(() => new EpsilonConstraintStudy(Primary(), null!));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new EpsilonConstraintStudy(
            Primary(), epsilon, gridPoints: 1));
        Assert.ThrowsException<ArgumentException>(() => new EpsilonConstraintStudy(
            Primary(), epsilon, fixedConstraints: new CostBenefitConstraint[] { null! }));
    }

    /// <summary>
    /// Verifies the tolerable-life-risk template: minimize total expected annual cost, the
    /// guideline as a default argument on the Excess life-loss stream in every epoch, and
    /// the conditional-mean tail metric swept by default.
    /// </summary>
    [TestMethod]
    public void Test_CreateTolerableLifeRiskStudy_TemplateShape()
    {
        // Act
        var study = EpsilonConstraintStudy.CreateTolerableLifeRiskStudy(lifeSafetyConsequenceType: 1);

        // Assert
        Assert.IsTrue(study.Primary.Metric.IsEconomic);
        Assert.AreEqual(EconomicMetric.TotalExpectedAnnualCost, study.Primary.Metric.EconomicMetric);
        Assert.AreEqual(ObjectiveDirection.Minimize, study.Primary.Direction);
        Assert.AreEqual(RiskMeasure.ConditionalMean, study.EpsilonObjective.Measure);
        Assert.AreEqual(RiskType.Excess, study.EpsilonObjective.RiskType);
        Assert.AreEqual(1, study.EpsilonObjective.ConsequenceType);
        Assert.AreEqual(1, study.FixedConstraints.Count);
        var guideline = study.FixedConstraints[0];
        Assert.AreEqual(RiskMeasure.Mean, guideline.Metric.Measure);
        Assert.AreEqual(RiskType.Excess, guideline.Metric.RiskType);
        Assert.AreEqual(1, guideline.Metric.ConsequenceType);
        Assert.AreEqual(ConstraintType.LesserThanOrEqualTo, guideline.Sense);
        Assert.AreEqual(1e-3, guideline.Threshold);
        Assert.AreEqual(ConstraintScope.EveryEpoch, guideline.Scope);
        Assert.IsTrue(study.Validate().IsValid);
    }

    /// <summary>Verifies the mean-variance and reliability template shapes.</summary>
    [TestMethod]
    public void Test_CreateMeanVarianceAndReliability_TemplateShapes()
    {
        // Act
        var meanVariance = EpsilonConstraintStudy.CreateMeanVarianceStudy();
        var reliability = EpsilonConstraintStudy.CreateReliabilityStudy();

        // Assert — mean risk swept against its standard deviation.
        Assert.AreEqual(RiskMeasure.Mean, meanVariance.Primary.Metric.Measure);
        Assert.AreEqual(ObjectiveDirection.Minimize, meanVariance.Primary.Direction);
        Assert.AreEqual(RiskMeasure.StandardDeviation, meanVariance.EpsilonObjective.Measure);

        // Cost swept against the annualized failure probability.
        Assert.AreEqual(EconomicMetric.PresentValueOfTotalCost, reliability.Primary.Metric.EconomicMetric);
        Assert.AreEqual(EconomicMetric.AnnualizedFailureProbability,
            reliability.EpsilonObjective.EconomicMetric);
    }

    /// <summary>Verifies the XML round trip with and without an explicit grid.</summary>
    [TestMethod]
    public void Test_Serialization_RoundTrip()
    {
        // Arrange
        var explicitGrid = new EpsilonConstraintStudy(Primary(),
            CostBenefitMetric.ForRiskMeasure(RiskMeasure.StandardDeviation, RiskType.Total),
            new[] { 10d, 20d, 30d }, 10,
            new[]
            {
                new CostBenefitConstraint(CostBenefitMetric.ForEconomic(EconomicMetric.AnnualizedFailureProbability),
                    ConstraintType.LesserThanOrEqualTo, 1e-4, ConstraintScope.EveryEpoch),
            });
        var autoGrid = new EpsilonConstraintStudy(Primary(),
            CostBenefitMetric.ForRiskMeasure(RiskMeasure.StandardDeviation, RiskType.Total),
            gridPoints: 25);

        // Act
        var explicitRestored = new EpsilonConstraintStudy(explicitGrid.ToXElement());
        var autoRestored = new EpsilonConstraintStudy(autoGrid.ToXElement());

        // Assert — the explicit grid, primary, ε metric, and fixed constraints survive.
        Assert.IsNotNull(explicitRestored.EpsilonGrid);
        CollectionAssert.AreEqual(new[] { 10d, 20d, 30d }, new List<double>(explicitRestored.EpsilonGrid));
        Assert.AreEqual("Total expected annual cost", explicitRestored.Primary.Name);
        Assert.AreEqual(RiskMeasure.StandardDeviation, explicitRestored.EpsilonObjective.Measure);
        Assert.AreEqual(1, explicitRestored.FixedConstraints.Count);
        Assert.AreEqual(EconomicMetric.AnnualizedFailureProbability,
            explicitRestored.FixedConstraints[0].Metric.EconomicMetric);

        // A null grid means automatic and reads back as null with its point count.
        Assert.IsNull(autoRestored.EpsilonGrid);
        Assert.AreEqual(25, autoRestored.GridPoints);
        Assert.ThrowsException<ArgumentNullException>(() => new EpsilonConstraintStudy(null!));
    }
}
