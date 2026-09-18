using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.CostBenefit;

namespace RMC.TotalRisk.Tests.Results.CostBenefit;

/// <summary>
/// Tests the consequence-reduction row: guards and the per-slot echo.
/// </summary>
[TestClass]
public class ConsequenceReductionTests
{
    /// <summary>Verifies the guards.</summary>
    [TestMethod]
    public void Test_Ctor_Guards()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() => new ConsequenceReduction(null!, 0,
            RiskType.Total, "Damages", "$", 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d));
        Assert.ThrowsException<ArgumentNullException>(() => new ConsequenceReduction("Gate fix", 0,
            RiskType.Total, null!, "$", 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d));
        Assert.ThrowsException<ArgumentNullException>(() => new ConsequenceReduction("Gate fix", 0,
            RiskType.Total, "Damages", null!, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new ConsequenceReduction("Gate fix", -1,
            RiskType.Total, "Damages", "$", 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d));
    }

    /// <summary>Verifies every slot echoes its own value.</summary>
    [TestMethod]
    public void Test_Ctor_PropertiesEchoed()
    {
        // Act
        var row = new ConsequenceReduction("Gate fix", 1, RiskType.Excess, "Life Loss", "lives",
            10d, 7d, 3d, 0.3d, 4d, 2.9d, 3.9d, 2.25e7d);

        // Assert
        Assert.AreEqual("Gate fix", row.AlternativeName);
        Assert.AreEqual(1, row.ConsequenceType);
        Assert.AreEqual(RiskType.Excess, row.Stream);
        Assert.AreEqual("Life Loss", row.Label);
        Assert.AreEqual("lives", row.Unit);
        Assert.AreEqual(10d, row.BaselinePresentValue);
        Assert.AreEqual(7d, row.AlternativePresentValue);
        Assert.AreEqual(3d, row.PresentValueReduction);
        Assert.AreEqual(0.3d, row.EquivalentAnnualReduction);
        Assert.AreEqual(4d, row.CumulativeReduction);
        Assert.AreEqual(2.9d, row.AbsorbingPresentValueReduction);
        Assert.AreEqual(3.9d, row.AbsorbingCumulativeReduction);
        Assert.AreEqual(2.25e7d, row.MonetizedPresentValueReduction);
    }
}
