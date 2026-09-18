using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for the Richardson discretization estimate: the exact second-order sequence
/// identities and the degenerate NaN cases.
/// </summary>
[TestClass]
public class DiscretizationEstimateTests
{
    /// <summary>
    /// Verifies the exact quadratic sequence: for V(B) = V* + c/B² at B, B/2, B/4 the
    /// extrapolation recovers V* exactly, the error estimate is the true error, and the
    /// observed ratio is exactly four.
    /// </summary>
    [TestMethod]
    public void Test_QuadraticSequence_ExactIdentities()
    {
        // Arrange — V* = 10, c = 400, B = 20: V20 = 11, V10 = 14, V5 = 26.
        var estimate = new DiscretizationEstimate(11d, 14d, 26d);

        // Assert
        Assert.AreEqual(10d, estimate.ExtrapolatedValue, 1e-15, "The extrapolation recovers the exact limit.");
        Assert.AreEqual(1d, estimate.EstimatedError, 1e-15, "The error estimate equals the true error in-regime.");
        Assert.AreEqual(0.1d, estimate.EstimatedRelativeError, 1e-15);
        Assert.AreEqual(4d, estimate.ObservedRatio, 1e-15, "The second-order rate shows a ratio of four.");
        Assert.AreEqual(11d, estimate.Value, 0d);
        Assert.AreEqual(14d, estimate.HalfValue, 0d);
        Assert.AreEqual(26d, estimate.QuarterValue, 0d);
    }

    /// <summary>
    /// Verifies the degenerate cases: identical values give a zero error with a NaN ratio, and
    /// a zero extrapolated value gives a NaN relative error.
    /// </summary>
    [TestMethod]
    public void Test_DegenerateCases_NaN()
    {
        // Arrange / Act
        var flat = new DiscretizationEstimate(5d, 5d, 5d);
        var zero = new DiscretizationEstimate(0d, 0d, 3d);

        // Assert
        Assert.AreEqual(0d, flat.EstimatedError, 0d);
        Assert.IsTrue(double.IsNaN(flat.ObservedRatio), "A zero fine difference has no ratio.");
        Assert.AreEqual(5d, flat.ExtrapolatedValue, 0d);
        Assert.IsTrue(double.IsNaN(zero.EstimatedRelativeError), "A zero reference has no relative error.");
    }
}
