using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.CostBenefit;

namespace RMC.TotalRisk.Tests.Results.CostBenefit;

/// <summary>
/// Tests the incremental cost-effectiveness step: guards and the per-slot echo.
/// </summary>
[TestClass]
public class IncrementalEntryTests
{
    /// <summary>Verifies null names are refused and every slot echoes.</summary>
    [TestMethod]
    public void Test_Ctor_GuardsAndEcho()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(
            () => new IncrementalEntry(null!, "B", 1d, 2d, 2d, 3d));
        Assert.ThrowsException<ArgumentNullException>(
            () => new IncrementalEntry("A", null!, 1d, 2d, 2d, 3d));

        var entry = new IncrementalEntry("A", "B", 10d, 30d, 3d, 2500d);
        Assert.AreEqual("A", entry.FromAlternative);
        Assert.AreEqual("B", entry.ToAlternative);
        Assert.AreEqual(10d, entry.DeltaCost);
        Assert.AreEqual(30d, entry.DeltaBenefit);
        Assert.AreEqual(3d, entry.IncrementalBenefitCostRatio);
        Assert.AreEqual(2500d, entry.IncrementalCostPerLifeSaved);
    }
}
