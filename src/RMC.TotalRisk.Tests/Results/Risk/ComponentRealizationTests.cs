using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;
using RMC.TotalRisk.Results.Support;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for <see cref="ComponentRealization"/> — construction, extent defaults, and the
/// failure-mode fan-out.
/// </summary>
[TestClass]
public class ComponentRealizationTests
{
    /// <summary>Verifies construction shapes and the extent tracking defaults.</summary>
    [TestMethod]
    public void Test_Construction_ShapesAndDefaults()
    {
        // Act
        var realization = new ComponentRealization(failureModes: 3);

        // Assert
        Assert.AreEqual(3, realization.FailureModes.Count);
        Assert.AreEqual(double.MaxValue, realization.MinN);
        Assert.AreEqual(double.MinValue, realization.MaxN);
        Assert.AreEqual(double.MaxValue, realization.MinH);
        Assert.AreEqual(double.MinValue, realization.MaxH);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new ComponentRealization(-1));
    }

    /// <summary>Verifies the fan-out reaches the failure modes and the memory dump clears them.</summary>
    [TestMethod]
    public void Test_FanOut_ReachesFailureModes()
    {
        // Arrange
        var realization = new ComponentRealization(failureModes: 1);
        realization.Curves.Fail.AddRiskPoint(1d, 0.4d, 0.1d, 10d);
        realization.Curves.Fail.AddRiskPoint(2d, 0.8d, 0.2d, 20d);
        realization.FailureModes[0].Curves.Fail.AddRiskPoint(1d, 0.4d, 0.1d, 10d);
        realization.FailureModes[0].Curves.Fail.AddRiskPoint(2d, 0.8d, 0.2d, 20d);

        var ledger = new QuadratureMassLedger();
        ledger.Record(0.4d, 0.5d, 0d);
        ledger.Record(0.8d, 0.5d, 0d);
        ledger.Seal();

        // Act
        realization.ApplyRecordedMass(ledger);

        // Assert — both scopes were credited before the dump clears them.
        Assert.AreEqual(0.5d, realization.Curves.Fail.RiskPoints[0].HazardProbabilityMass, 1e-15);
        Assert.AreEqual(0.5d, realization.FailureModes[0].Curves.Fail.RiskPoints[0].HazardProbabilityMass, 1e-15);
        realization.DumpMemory();
        Assert.AreEqual(0, realization.Curves.Fail.RiskPoints.Count);
        Assert.AreEqual(0, realization.FailureModes[0].Curves.Fail.RiskPoints.Count);
    }
}
