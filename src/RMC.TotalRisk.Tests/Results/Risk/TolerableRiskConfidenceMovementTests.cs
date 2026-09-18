using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for the tolerable-risk confidence movement block: constructor guards, the
/// criterion echo, and movement-list snapshotting.
/// </summary>
[TestClass]
public class TolerableRiskConfidenceMovementTests
{
    /// <summary>
    /// Verifies the constructor stores the criterion echo and snapshots the movement list.
    /// </summary>
    [TestMethod]
    public void Test_Constructor_StoresAndSnapshots()
    {
        // Arrange
        var movements = new[] { 0.2d, double.NaN, 0.05d };

        // Act
        var block = new TolerableRiskConfidenceMovement("Mean", "Excess", 0, 1e-3, 0.7d, 0.42d, movements);
        movements[0] = 99d;

        // Assert
        Assert.AreEqual("Mean", block.Measure);
        Assert.AreEqual("Excess", block.RiskType);
        Assert.AreEqual(0, block.ConsequenceTypeIndex);
        Assert.AreEqual(1e-3, block.Threshold, 0d);
        Assert.AreEqual(0.7d, block.BaselineExceedanceProbability, 0d);
        Assert.AreEqual(0.42d, block.PerfectInformationMovement, 0d);
        Assert.AreEqual(3, block.EntryMovements.Count);
        Assert.AreEqual(0.2d, block.EntryMovements[0], 0d, "The block snapshots the movement values.");
        Assert.IsTrue(double.IsNaN(block.EntryMovements[1]));
    }

    /// <summary>
    /// Verifies the guards and coercions: a null movement list throws and null echoes become
    /// empty strings.
    /// </summary>
    [TestMethod]
    public void Test_Constructor_GuardsAndCoercions()
    {
        // Arrange / Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() =>
            new TolerableRiskConfidenceMovement("Mean", "Excess", 0, 1e-3, 0.5d, 0.5d, null!));
        var blank = new TolerableRiskConfidenceMovement(null, null, 1, 2d, 0.5d, 0.5d, Array.Empty<double>());
        Assert.AreEqual(string.Empty, blank.Measure);
        Assert.AreEqual(string.Empty, blank.RiskType);
    }
}
