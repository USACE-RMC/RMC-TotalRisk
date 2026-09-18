using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for the candidate-study rollup: constructor guards, member snapshotting, and the
/// derived standard deviation.
/// </summary>
[TestClass]
public class ValueOfInformationGroupTests
{
    /// <summary>
    /// Verifies the constructor stores the rollup and snapshots the member list against later
    /// mutation.
    /// </summary>
    [TestMethod]
    public void Test_Constructor_SnapshotsMembers()
    {
        // Arrange
        var members = new List<string> { "Column [1]", "Column [2]" };

        // Act
        var group = new ValueOfInformationGroup("Dam - Fragility", members, 16d, 0.4d);
        members.Add("Intruder");

        // Assert
        Assert.AreEqual("Dam - Fragility", group.Label);
        Assert.AreEqual(2, group.MemberLabels.Count);
        Assert.AreEqual(16d, group.ResolvableVariance, 0d);
        Assert.AreEqual(4d, group.ResolvableStandardDeviation, 1e-15);
        Assert.AreEqual(0.4d, group.VarianceShare, 0d);
    }

    /// <summary>
    /// Verifies the guards and coercions: a null member list throws and a null label becomes
    /// empty.
    /// </summary>
    [TestMethod]
    public void Test_Constructor_GuardsAndCoercions()
    {
        // Arrange / Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() => new ValueOfInformationGroup("G", null!, 1d, 0.5d));
        var unnamed = new ValueOfInformationGroup(null, Array.Empty<string>(), double.NaN, double.NaN);
        Assert.AreEqual(string.Empty, unnamed.Label);
        Assert.IsTrue(double.IsNaN(unnamed.ResolvableStandardDeviation));
    }
}
