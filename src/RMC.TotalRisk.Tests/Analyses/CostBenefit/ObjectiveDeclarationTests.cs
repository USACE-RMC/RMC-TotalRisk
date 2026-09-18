using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.CostBenefit;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Tests.Analyses.CostBenefit;

/// <summary>
/// Tests the objective declaration: guards, echoes, and the XML round trip.
/// </summary>
[TestClass]
public class ObjectiveDeclarationTests
{
    /// <summary>Verifies the construction guards.</summary>
    [TestMethod]
    public void Test_Ctor_Guards()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentException>(() => new ObjectiveDeclaration(" ",
            CostBenefitMetric.ForEconomic(EconomicMetric.NetPresentValue), ObjectiveDirection.Maximize));
        Assert.ThrowsException<ArgumentNullException>(() => new ObjectiveDeclaration("Net present value",
            null!, ObjectiveDirection.Maximize));
    }

    /// <summary>Verifies the properties echo and the declaration validates.</summary>
    [TestMethod]
    public void Test_Ctor_PropertiesEchoed()
    {
        // Act
        var objective = new ObjectiveDeclaration("Net present value",
            CostBenefitMetric.ForEconomic(EconomicMetric.NetPresentValue), ObjectiveDirection.Maximize);

        // Assert
        Assert.AreEqual("Net present value", objective.Name);
        Assert.AreEqual(ObjectiveDirection.Maximize, objective.Direction);
        Assert.IsTrue(objective.Metric.IsEconomic);
        Assert.IsTrue(objective.Validate().IsValid);
    }

    /// <summary>Verifies the XML round trip preserves the name, direction, and metric.</summary>
    [TestMethod]
    public void Test_Serialization_RoundTrip()
    {
        // Arrange
        var objective = new ObjectiveDeclaration("Excess life-loss reduction",
            CostBenefitMetric.ForRiskMeasure(RiskMeasure.Mean, RiskType.Excess, 1,
                MetricBasis.HorizonEquivalentAnnual, form: MetricForm.ReductionVsBaseline),
            ObjectiveDirection.Maximize);

        // Act
        var restored = new ObjectiveDeclaration(objective.ToXElement());

        // Assert
        Assert.AreEqual(objective.Name, restored.Name);
        Assert.AreEqual(objective.Direction, restored.Direction);
        Assert.AreEqual(RiskMeasure.Mean, restored.Metric.Measure);
        Assert.AreEqual(RiskType.Excess, restored.Metric.RiskType);
        Assert.AreEqual(1, restored.Metric.ConsequenceType);
        Assert.AreEqual(MetricBasis.HorizonEquivalentAnnual, restored.Metric.Basis);
        Assert.AreEqual(MetricForm.ReductionVsBaseline, restored.Metric.Form);
        Assert.ThrowsException<ArgumentNullException>(() => new ObjectiveDeclaration(null!));
    }
}
