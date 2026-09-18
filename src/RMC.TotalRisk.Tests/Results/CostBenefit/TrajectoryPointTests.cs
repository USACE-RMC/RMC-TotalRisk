using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.CostBenefit;

namespace RMC.TotalRisk.Tests.Results.CostBenefit;

/// <summary>
/// Tests the plot-ready trajectory point: guards, echoes, and the defensive snapshots.
/// </summary>
[TestClass]
public class TrajectoryPointTests
{
    /// <summary>Verifies the guards.</summary>
    [TestMethod]
    public void Test_Ctor_Guards()
    {
        // Arrange
        var means = new[] { 1d };

        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() => new TrajectoryPoint(null!, 0, 10,
            0.01d, 0.1d, means, means, means));
        Assert.ThrowsException<ArgumentNullException>(() => new TrajectoryPoint("Baseline", 0, 10,
            0.01d, 0.1d, null!, means, means));
        Assert.ThrowsException<ArgumentNullException>(() => new TrajectoryPoint("Baseline", 0, 10,
            0.01d, 0.1d, means, null!, means));
        Assert.ThrowsException<ArgumentNullException>(() => new TrajectoryPoint("Baseline", 0, 10,
            0.01d, 0.1d, means, means, null!));
    }

    /// <summary>Verifies the echoes and the defensive snapshots.</summary>
    [TestMethod]
    public void Test_Ctor_EchoedAndSnapshotsHeld()
    {
        // Arrange
        var total = new List<double> { 500d };

        // Act
        var point = new TrajectoryPoint("Gate fix", 10, 5, 0.02d, 0.15d,
            total, new[] { 400d }, new[] { 100d });
        total[0] = 999d;

        // Assert
        Assert.AreEqual("Gate fix", point.AlternativeName);
        Assert.AreEqual(10, point.StartYear);
        Assert.AreEqual(5, point.SpanYears);
        Assert.AreEqual(0.02d, point.FailureProbability);
        Assert.AreEqual(0.15d, point.CumulativeFailureProbability);
        Assert.AreEqual(500d, point.TotalExpectedConsequences[0]);
        Assert.AreEqual(400d, point.ExcessExpectedConsequences[0]);
        Assert.AreEqual(100d, point.FailExpectedConsequences[0]);
    }
}
