using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for the secondary-axis discretization diagnostic container: constructor guards,
/// coercions, and list snapshotting.
/// </summary>
[TestClass]
public class SecondaryDiscretizationDiagnosticTests
{
    /// <summary>
    /// Verifies the constructor stores the identity and snapshots the risk-estimate list.
    /// </summary>
    [TestMethod]
    public void Test_Constructor_StoresAndSnapshots()
    {
        // Arrange
        var probability = new DiscretizationEstimate(1e-3, 1.1e-3, 1.5e-3);
        var risk = new List<DiscretizationEstimate> { new DiscretizationEstimate(5d, 5.2d, 6d) };

        // Act
        var diagnostic = new SecondaryDiscretizationDiagnostic("Dam", 20, 10, 5, probability, risk);
        risk.Add(new DiscretizationEstimate(0d, 0d, 0d));

        // Assert
        Assert.AreEqual("Dam", diagnostic.ComponentName);
        Assert.AreEqual(20, diagnostic.Bins);
        Assert.AreEqual(10, diagnostic.HalfBins);
        Assert.AreEqual(5, diagnostic.QuarterBins);
        Assert.AreSame(probability, diagnostic.FailureProbability);
        Assert.AreEqual(1, diagnostic.MeanRisk.Count, "The list is snapshotted at construction.");
    }

    /// <summary>
    /// Verifies the guards and coercions: null estimates throw and a null name becomes empty.
    /// </summary>
    [TestMethod]
    public void Test_Constructor_GuardsAndCoercions()
    {
        // Arrange
        var estimate = new DiscretizationEstimate(1d, 1d, 1d);
        var list = new[] { estimate };

        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() =>
            new SecondaryDiscretizationDiagnostic("Dam", 20, 10, 5, null!, list));
        Assert.ThrowsException<ArgumentNullException>(() =>
            new SecondaryDiscretizationDiagnostic("Dam", 20, 10, 5, estimate, null!));
        var unnamed = new SecondaryDiscretizationDiagnostic(null, 12, 6, 3, estimate, list);
        Assert.AreEqual(string.Empty, unnamed.ComponentName);
    }
}
