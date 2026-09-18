using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for the per-input value-of-information entry: constructor coercions, the derived
/// standard deviation, and NaN propagation.
/// </summary>
[TestClass]
public class ValueOfInformationEntryTests
{
    /// <summary>
    /// Verifies the constructor stores the values and derives the standard deviation as the
    /// square root of the resolvable variance.
    /// </summary>
    [TestMethod]
    public void Test_Constructor_StoresAndDerives()
    {
        // Arrange / Act
        var entry = new ValueOfInformationEntry("Dam - Rating Curve", "Dam - Rating Curve", 9d, 0.6d, 100);

        // Assert
        Assert.AreEqual("Dam - Rating Curve", entry.Label);
        Assert.AreEqual("Dam - Rating Curve", entry.GroupLabel);
        Assert.AreEqual(9d, entry.ResolvableVariance, 0d);
        Assert.AreEqual(3d, entry.ResolvableStandardDeviation, 1e-15);
        Assert.AreEqual(0.6d, entry.VarianceShare, 0d);
        Assert.AreEqual(100, entry.Realizations);
    }

    /// <summary>
    /// Verifies the null coercions: a null label becomes empty and a null group label falls
    /// back to the entry label.
    /// </summary>
    [TestMethod]
    public void Test_Constructor_NullCoercions()
    {
        // Arrange / Act
        var unnamed = new ValueOfInformationEntry(null, null, 1d, 0.1d, 10);
        var grouped = new ValueOfInformationEntry("Column", null, 1d, 0.1d, 10);

        // Assert
        Assert.AreEqual(string.Empty, unnamed.Label);
        Assert.AreEqual(string.Empty, unnamed.GroupLabel);
        Assert.AreEqual("Column", grouped.GroupLabel);
    }

    /// <summary>
    /// Verifies NaN propagation: an inestimable variance reports a NaN standard deviation.
    /// </summary>
    [TestMethod]
    public void Test_NaNVariance_NaNStandardDeviation()
    {
        // Arrange / Act
        var entry = new ValueOfInformationEntry("Column", "Group", double.NaN, double.NaN, 5);

        // Assert
        Assert.IsTrue(double.IsNaN(entry.ResolvableStandardDeviation));
    }
}
