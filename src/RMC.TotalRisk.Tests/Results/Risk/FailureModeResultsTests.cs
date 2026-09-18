using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for <see cref="FailureModeResults"/> — the per-mode Excess/Fail summary capture.
/// </summary>
[TestClass]
public class FailureModeResultsTests
{
    /// <summary>Verifies the capture summarizes the Excess and Fail streams.</summary>
    [TestMethod]
    public void Test_Capture_ExcessAndFail()
    {
        // Arrange
        var realization = new FailureModeRealization { Name = "Overtopping" };
        realization.Curves.Fail.CreateCurve(new List<(double Mass, double Consequence)> { (0.01d, 100d), (0.02d, 10d) }, 200);
        realization.Curves.Excess.CreateCurve(new List<(double Mass, double Consequence)> { (0.01d, 90d), (0.02d, 8d) }, 200);

        // Act
        var results = new FailureModeResults(realization);

        // Assert
        Assert.AreEqual(realization.Curves.Fail.TotalProbability, results.Fail.TotalProbability, 0d);
        Assert.AreEqual(realization.Curves.Fail.Mean, results.Fail.Mean, 0d);
        Assert.AreEqual(realization.Curves.Excess.Mean, results.Excess.Mean, 0d);
        Assert.ThrowsException<ArgumentNullException>(() => new FailureModeResults(null!));
    }
}
