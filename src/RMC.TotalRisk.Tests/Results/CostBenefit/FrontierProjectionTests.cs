using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.CostBenefit;

namespace RMC.TotalRisk.Tests.Results.CostBenefit;

/// <summary>
/// Tests the frontier projection: guards, the parallel-list rule, and the echoes.
/// </summary>
[TestClass]
public class FrontierProjectionTests
{
    /// <summary>Verifies null guards and the parallel-list rule.</summary>
    [TestMethod]
    public void Test_Ctor_Guards()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() => new FrontierProjection(null!, "x",
            ObjectiveDirection.Minimize, "y", ObjectiveDirection.Maximize, new[] { "A" },
            new[] { 1d }, new[] { 2d }, new[] { true }, new[] { false }));
        Assert.ThrowsException<ArgumentException>(() => new FrontierProjection("P", "x",
            ObjectiveDirection.Minimize, "y", ObjectiveDirection.Maximize, new[] { "A" },
            new[] { 1d, 2d }, new[] { 2d }, new[] { true }, new[] { false }));
    }

    /// <summary>Verifies the axis and point echoes.</summary>
    [TestMethod]
    public void Test_Ctor_PropertiesEchoed()
    {
        // Act
        var projection = new FrontierProjection("Cost vs benefit", "Cost", ObjectiveDirection.Minimize,
            "Benefit", ObjectiveDirection.Maximize, new[] { "A", "B" }, new[] { 1d, 2d },
            new[] { 10d, 20d }, new[] { false, true }, new[] { false, false });

        // Assert
        Assert.AreEqual("Cost vs benefit", projection.Label);
        Assert.AreEqual("Cost", projection.XLabel);
        Assert.AreEqual(ObjectiveDirection.Minimize, projection.XDirection);
        Assert.AreEqual("Benefit", projection.YLabel);
        Assert.AreEqual(ObjectiveDirection.Maximize, projection.YDirection);
        Assert.AreEqual(2, projection.AlternativeNames.Count);
        Assert.AreEqual(2d, projection.XValues[1]);
        Assert.AreEqual(20d, projection.YValues[1]);
        Assert.IsTrue(projection.IsNonDominated[1]);
        Assert.IsFalse(projection.IsExcludedForNaN[0]);
    }
}
