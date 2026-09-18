using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.CostBenefit;

namespace RMC.TotalRisk.Tests.Analyses.CostBenefit;

/// <summary>
/// Tests the dated capital entry: guards, echoes, and the XML round trip.
/// </summary>
[TestClass]
public class CapitalCostEntryTests
{
    /// <summary>Verifies the construction guards.</summary>
    [TestMethod]
    public void Test_Ctor_Guards()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new CapitalCostEntry(-1, 100d));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new CapitalCostEntry(0, double.NaN));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new CapitalCostEntry(0, double.PositiveInfinity));
    }

    /// <summary>Verifies the properties echo, including a signed credit and the empty-label default.</summary>
    [TestMethod]
    public void Test_Ctor_PropertiesEchoed()
    {
        // Act
        var entry = new CapitalCostEntry(10, -200d, "Salvage");
        var bare = new CapitalCostEntry(0, 1000d);

        // Assert
        Assert.AreEqual(10, entry.Year);
        Assert.AreEqual(-200d, entry.Amount);
        Assert.AreEqual("Salvage", entry.Label);
        Assert.AreEqual(string.Empty, bare.Label);
    }

    /// <summary>Verifies the XML round trip is bit-exact and a null element is refused.</summary>
    [TestMethod]
    public void Test_Serialization_RoundTrip()
    {
        // Arrange
        var entry = new CapitalCostEntry(7, 1234.5678901234567d, "Gate replacement");

        // Act
        var restored = new CapitalCostEntry(entry.ToXElement());

        // Assert
        Assert.AreEqual(entry.Year, restored.Year);
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(entry.Amount),
            BitConverter.DoubleToInt64Bits(restored.Amount));
        Assert.AreEqual(entry.Label, restored.Label);
        Assert.ThrowsException<ArgumentNullException>(() => new CapitalCostEntry(null!));
    }
}
