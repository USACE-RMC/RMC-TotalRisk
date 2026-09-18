using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for <see cref="ComponentResults"/> — the five-stream component summary capture
/// with its per-mode summaries.
/// </summary>
[TestClass]
public class ComponentResultsTests
{
    /// <summary>Verifies the capture summarizes every stream and every failure mode in order.</summary>
    [TestMethod]
    public void Test_Capture_StreamsAndModes()
    {
        // Arrange
        var realization = new ComponentRealization(failureModes: 2);
        realization.Curves.Total.CreateCurve(new List<(double Mass, double Consequence)> { (0.5d, 10d), (0.5d, 2d) }, 200);
        realization.FailureModes[1].Curves.Fail.CreateCurve(new List<(double Mass, double Consequence)> { (0.03d, 40d), (0.02d, 20d) }, 200);

        // Act
        var results = new ComponentResults(realization);

        // Assert
        Assert.AreEqual(realization.Curves.Total.Mean, results.Total.Mean, 0d);
        Assert.AreEqual(2, results.FailureModeResults.Count);
        Assert.AreEqual(realization.FailureModes[1].Curves.Fail.TotalProbability, results.FailureModeResults[1].Fail.TotalProbability, 0d);
        Assert.ThrowsException<ArgumentNullException>(() => new ComponentResults(null!));
    }
}
