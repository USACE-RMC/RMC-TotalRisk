using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for <see cref="FailureModeRealization"/> — the per-mode curve container fan-out.
/// </summary>
[TestClass]
public class FailureModeRealizationTests
{
    /// <summary>Verifies construction and the curve pipeline fan-out.</summary>
    [TestMethod]
    public void Test_Construction_AndPipeline()
    {
        // Arrange
        var realization = new FailureModeRealization { Name = "Piping" };
        realization.Curves.Fail.CreateCurve(new List<(double Mass, double Consequence)> { (0.02d, 50d), (0.01d, 5d) }, 200);

        // Act
        realization.ComputeRiskMeasures(consequenceThreshold: 1d, alpha: 0.05d);

        // Assert
        Assert.AreEqual("Piping", realization.Name);
        Assert.AreEqual(0.03d, realization.Curves.Fail.TotalProbability, 1e-15);
        Assert.AreEqual(0d, realization.Curves.Fail.ValueAtRisk, 0d, "α = 0.05 exceeds the 0.03 total probability.");
    }
}
