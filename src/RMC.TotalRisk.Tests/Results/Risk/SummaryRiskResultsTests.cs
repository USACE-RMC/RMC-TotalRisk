using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for <see cref="SummaryRiskResults"/> — the compact curve summary capture.
/// </summary>
[TestClass]
public class SummaryRiskResultsTests
{
    /// <summary>Verifies the capture copies the full risk-measure catalog from a finished curve.</summary>
    [TestMethod]
    public void Test_Capture_CopiesCatalog()
    {
        // Arrange
        var curve = new Curve { IsExhaustive = false };
        curve.CreateCurve(new List<(double Mass, double Consequence)> { (0.02d, 10d), (0.08d, 2d) }, 200);
        curve.ComputeRiskMeasures(consequenceThreshold: 1d, alpha: 0.05d);

        // Act
        var summary = new SummaryRiskResults(curve);

        // Assert
        Assert.AreEqual(curve.TotalProbability, summary.TotalProbability, 0d);
        Assert.AreEqual(curve.ConditionalMean, summary.ConditionalMean, 0d);
        Assert.AreEqual(curve.Mean, summary.Mean, 0d);
        Assert.AreEqual(curve.StandardDeviation, summary.StandardDeviation, 0d);
        Assert.AreEqual(curve.Skewness, summary.Skewness, 0d);
        Assert.AreEqual(curve.Kurtosis, summary.Kurtosis, 0d);
        Assert.AreEqual(curve.ConsequenceThresholdProbability, summary.ConsequenceThresholdProbability, 0d);
        Assert.AreEqual(curve.ValueAtRisk, summary.ValueAtRisk, 0d);
        Assert.AreEqual(curve.ConditionalValueAtRisk, summary.ConditionalValueAtRisk, 0d);
        Assert.IsTrue(double.IsNaN(summary.HazardThresholdProbability), "No hazard threshold was supplied.");
        Assert.ThrowsException<ArgumentNullException>(() => new SummaryRiskResults(null!));
    }
}
