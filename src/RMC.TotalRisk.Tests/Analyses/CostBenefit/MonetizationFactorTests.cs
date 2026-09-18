using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.CostBenefit;

namespace RMC.TotalRisk.Tests.Analyses.CostBenefit;

/// <summary>
/// Tests the per-type monetization factor: guards, echoes, and the XML round trip.
/// </summary>
[TestClass]
public class MonetizationFactorTests
{
    /// <summary>Verifies the construction guards.</summary>
    [TestMethod]
    public void Test_Ctor_Guards()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new MonetizationFactor(-1, 1000d));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new MonetizationFactor(0, 0d));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new MonetizationFactor(0, -5d));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new MonetizationFactor(0, double.NaN));
    }

    /// <summary>Verifies the properties echo, including the vintage stamp.</summary>
    [TestMethod]
    public void Test_Ctor_PropertiesEchoed()
    {
        // Act
        var factor = new MonetizationFactor(1, 7.5e6d, "Statistical life", "2023 USDOT");

        // Assert
        Assert.AreEqual(1, factor.ConsequenceType);
        Assert.AreEqual(7.5e6d, factor.AmountPerUnit);
        Assert.AreEqual("Statistical life", factor.Label);
        Assert.AreEqual("2023 USDOT", factor.Vintage);
    }

    /// <summary>Verifies the XML round trip is bit-exact.</summary>
    [TestMethod]
    public void Test_Serialization_RoundTrip()
    {
        // Arrange
        var factor = new MonetizationFactor(2, 1234.5678901234567d, "Acreage", "2026 survey");

        // Act
        var restored = new MonetizationFactor(factor.ToXElement());

        // Assert
        Assert.AreEqual(factor.ConsequenceType, restored.ConsequenceType);
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(factor.AmountPerUnit),
            BitConverter.DoubleToInt64Bits(restored.AmountPerUnit));
        Assert.AreEqual(factor.Label, restored.Label);
        Assert.AreEqual(factor.Vintage, restored.Vintage);
        Assert.ThrowsException<ArgumentNullException>(() => new MonetizationFactor(null!));
    }
}
