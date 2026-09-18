using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for the value-of-information result container: constructor guards, list
/// snapshotting, the derived total standard deviation, and the ranked views' NaN-last stable
/// ordering.
/// </summary>
[TestClass]
public class ValueOfInformationResultsTests
{
    /// <summary>Builds a three-entry, two-group result for the ranking tests.</summary>
    private static ValueOfInformationResults Build()
    {
        var entries = new[]
        {
            new ValueOfInformationEntry("A", "G1", 1d, 0.1d, 100),
            new ValueOfInformationEntry("B", "G2", double.NaN, double.NaN, 100),
            new ValueOfInformationEntry("C", "G1", 4d, 0.4d, 100),
        };
        var groups = new[]
        {
            new ValueOfInformationGroup("G1", new[] { "A", "C" }, 5d, 0.5d),
            new ValueOfInformationGroup("G2", new[] { "B" }, double.NaN, double.NaN),
        };
        var movements = new[]
        {
            new TolerableRiskConfidenceMovement("Mean", "Excess", 0, 1e-3, 0.6d, 0.48d, new[] { 0.1d, double.NaN, 0.2d }),
        };
        return new ValueOfInformationResults("Mean — Total — System", RiskType.Total, RiskMeasure.Mean,
            0, 20, 100, 10d, entries, groups, movements);
    }

    /// <summary>
    /// Verifies the constructor stores the query identity and derives the total standard
    /// deviation.
    /// </summary>
    [TestMethod]
    public void Test_Constructor_StoresAndDerives()
    {
        // Arrange / Act
        var results = Build();

        // Assert
        Assert.AreEqual("Mean — Total — System", results.OutputLabel);
        Assert.AreEqual(RiskType.Total, results.RiskType);
        Assert.AreEqual(RiskMeasure.Mean, results.Measure);
        Assert.AreEqual(0, results.ConsequenceType);
        Assert.AreEqual(20, results.Bins);
        Assert.AreEqual(100, results.Realizations);
        Assert.AreEqual(10d, results.TotalVariance, 0d);
        Assert.AreEqual(Math.Sqrt(10d), results.TotalStandardDeviation, 1e-15);
        Assert.AreEqual(3, results.Entries.Count);
        Assert.AreEqual(2, results.Groups.Count);
        Assert.AreEqual(1, results.CriterionMovements.Count);
    }

    /// <summary>
    /// Verifies the null guards on every list argument.
    /// </summary>
    [TestMethod]
    public void Test_Constructor_NullLists_Throw()
    {
        // Arrange
        var entries = Array.Empty<ValueOfInformationEntry>();
        var groups = Array.Empty<ValueOfInformationGroup>();
        var movements = Array.Empty<TolerableRiskConfidenceMovement>();

        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() => new ValueOfInformationResults(
            null, RiskType.Total, RiskMeasure.Mean, 0, 20, 0, 0d, null!, groups, movements));
        Assert.ThrowsException<ArgumentNullException>(() => new ValueOfInformationResults(
            null, RiskType.Total, RiskMeasure.Mean, 0, 20, 0, 0d, entries, null!, movements));
        Assert.ThrowsException<ArgumentNullException>(() => new ValueOfInformationResults(
            null, RiskType.Total, RiskMeasure.Mean, 0, 20, 0, 0d, entries, groups, null!));
    }

    /// <summary>
    /// Verifies the ranked entry view: descending resolvable variance with NaN entries last
    /// and the original list untouched.
    /// </summary>
    [TestMethod]
    public void Test_RankedEntries_DescendingNaNLast()
    {
        // Arrange
        var results = Build();

        // Act
        var ranked = results.RankedEntries();

        // Assert
        Assert.AreEqual("C", ranked[0].Label);
        Assert.AreEqual("A", ranked[1].Label);
        Assert.AreEqual("B", ranked[2].Label, "NaN entries sink to the end.");
        Assert.AreEqual("A", results.Entries[0].Label, "The stored walk order is untouched.");
    }

    /// <summary>
    /// Verifies the ranked group view: descending resolvable variance with NaN groups last.
    /// </summary>
    [TestMethod]
    public void Test_RankedGroups_DescendingNaNLast()
    {
        // Arrange
        var results = Build();

        // Act
        var ranked = results.RankedGroups();

        // Assert
        Assert.AreEqual("G1", ranked[0].Label);
        Assert.AreEqual("G2", ranked[1].Label);
    }

    /// <summary>
    /// Verifies ties keep the stored order — the ranked views use a stable comparison.
    /// </summary>
    [TestMethod]
    public void Test_RankedEntries_TiesKeepWalkOrder()
    {
        // Arrange
        var entries = new[]
        {
            new ValueOfInformationEntry("First", "G", 2d, 0.2d, 10),
            new ValueOfInformationEntry("Second", "G", 2d, 0.2d, 10),
        };
        var results = new ValueOfInformationResults(null, RiskType.Total, RiskMeasure.Mean, 0, 20, 10, 10d,
            entries, Array.Empty<ValueOfInformationGroup>(), Array.Empty<TolerableRiskConfidenceMovement>());

        // Act
        var ranked = results.RankedEntries();

        // Assert
        Assert.AreEqual("First", ranked[0].Label);
        Assert.AreEqual("Second", ranked[1].Label);
    }
}
