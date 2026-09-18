using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>Tests the configuration-risk query result container.</summary>
[TestClass]
public class ConfigurationRiskResultsTests
{
    /// <summary>Verifies the stored rows, labels, and override echoes.</summary>
    [TestMethod]
    public void Test_Constructor_StoresRowsAndLabels()
    {
        var system = Entry("System");
        var component = Entry("Dam");

        var results = new ConfigurationRiskResults(system, new[] { component },
            new[] { "Life Loss" }, new[] { "lives" }, new[] { "'Tree' house event 'Gate' = True" });

        Assert.AreSame(system, results.System);
        Assert.AreEqual(1, results.Components.Count);
        Assert.AreSame(component, results.Components[0]);
        Assert.AreEqual("Life Loss", results.ConsequenceLabels[0]);
        Assert.AreEqual("lives", results.ConsequenceUnits[0]);
        Assert.AreEqual("'Tree' house event 'Gate' = True", results.AppliedOverrides[0]);
    }

    /// <summary>Verifies null arguments are rejected.</summary>
    [TestMethod]
    public void Test_Constructor_Guards()
    {
        var system = Entry("System");
        var components = new[] { Entry("Dam") };
        var labels = new[] { "Damages" };
        var units = new[] { "$" };
        var applied = new[] { "label" };

        Assert.ThrowsException<ArgumentNullException>(() =>
            new ConfigurationRiskResults(null!, components, labels, units, applied));
        Assert.ThrowsException<ArgumentNullException>(() =>
            new ConfigurationRiskResults(system, null!, labels, units, applied));
        Assert.ThrowsException<ArgumentNullException>(() =>
            new ConfigurationRiskResults(system, components, null!, units, applied));
        Assert.ThrowsException<ArgumentNullException>(() =>
            new ConfigurationRiskResults(system, components, labels, null!, applied));
        Assert.ThrowsException<ArgumentNullException>(() =>
            new ConfigurationRiskResults(system, components, labels, units, null!));
    }

    /// <summary>Builds one plain row.</summary>
    /// <param name="name">The row label.</param>
    /// <returns>The row.</returns>
    private static ConfigurationRiskEntry Entry(string name)
    {
        return new ConfigurationRiskEntry(name, 0.1d, 0.2d, new[] { 10d }, new[] { 20d });
    }
}
