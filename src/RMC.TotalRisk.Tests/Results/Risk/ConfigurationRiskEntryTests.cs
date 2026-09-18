using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>Tests the configuration-risk scope row.</summary>
[TestClass]
public class ConfigurationRiskEntryTests
{
    /// <summary>Verifies the stored values and the derived change and ratio.</summary>
    [TestMethod]
    public void Test_Constructor_ComputesChangesAndRatio()
    {
        var entry = new ConfigurationRiskEntry("Dam", 0.2d, 0.5d,
            new[] { 100d, 10d }, new[] { 250d, 4d });

        Assert.AreEqual("Dam", entry.Name);
        Assert.AreEqual(0.2d, entry.BaselineFailureProbability);
        Assert.AreEqual(0.5d, entry.ConfiguredFailureProbability);
        Assert.AreEqual(0.3d, entry.FailureProbabilityChange, 1e-15d);
        Assert.AreEqual(2.5d, entry.FailureProbabilityRatio, 1e-15d);
        Assert.AreEqual(150d, entry.ExpectedConsequenceChanges[0], 1e-12d);
        Assert.AreEqual(-6d, entry.ExpectedConsequenceChanges[1], 1e-12d);
    }

    /// <summary>Verifies the zero-baseline ratio reports NaN.</summary>
    [TestMethod]
    public void Test_Ratio_ZeroBaseline_IsNaN()
    {
        var entry = new ConfigurationRiskEntry("Dam", 0d, 0.5d, new[] { 0d }, new[] { 1d });

        Assert.IsTrue(double.IsNaN(entry.FailureProbabilityRatio));
    }

    /// <summary>Verifies the consequence lists are defensively copied.</summary>
    [TestMethod]
    public void Test_ConsequenceLists_AreDefensiveCopies()
    {
        var baseline = new[] { 100d };
        var configured = new[] { 200d };
        var entry = new ConfigurationRiskEntry("Dam", 0.1d, 0.2d, baseline, configured);

        baseline[0] = -1d;
        configured[0] = -1d;

        Assert.AreEqual(100d, entry.BaselineExpectedConsequences[0]);
        Assert.AreEqual(200d, entry.ConfiguredExpectedConsequences[0]);
    }

    /// <summary>Verifies null and misaligned arguments are rejected.</summary>
    [TestMethod]
    public void Test_Constructor_Guards()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            new ConfigurationRiskEntry(null!, 0d, 0d, new[] { 0d }, new[] { 0d }));
        Assert.ThrowsException<ArgumentNullException>(() =>
            new ConfigurationRiskEntry("Dam", 0d, 0d, null!, new[] { 0d }));
        Assert.ThrowsException<ArgumentNullException>(() =>
            new ConfigurationRiskEntry("Dam", 0d, 0d, new[] { 0d }, null!));
        Assert.ThrowsException<ArgumentException>(() =>
            new ConfigurationRiskEntry("Dam", 0d, 0d, new[] { 0d }, new[] { 0d, 1d }));
    }
}
