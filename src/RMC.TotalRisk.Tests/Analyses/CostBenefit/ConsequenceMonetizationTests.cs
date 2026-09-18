using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.CostBenefit;

namespace RMC.TotalRisk.Tests.Analyses.CostBenefit;

/// <summary>
/// Tests the monetization map: guards, the duplicate-position refusal, and the XML round
/// trip.
/// </summary>
[TestClass]
public class ConsequenceMonetizationTests
{
    /// <summary>Verifies the construction guards, including the one-price-per-type rule.</summary>
    [TestMethod]
    public void Test_Ctor_Guards()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentException>(() => new ConsequenceMonetization(monetaryUnit: " "));
        Assert.ThrowsException<ArgumentException>(() => new ConsequenceMonetization(
            new MonetizationFactor[] { null! }));
        Assert.ThrowsException<ArgumentException>(() => new ConsequenceMonetization(new[]
        {
            new MonetizationFactor(1, 100d),
            new MonetizationFactor(1, 200d),
        }));
    }

    /// <summary>Verifies the defaults: no factors and the dollar unit.</summary>
    [TestMethod]
    public void Test_Ctor_Defaults()
    {
        // Act
        var map = new ConsequenceMonetization();

        // Assert
        Assert.AreEqual(0, map.Factors.Count);
        Assert.AreEqual("$", map.MonetaryUnit);
    }

    /// <summary>Verifies the XML round trip preserves the unit and every factor.</summary>
    [TestMethod]
    public void Test_Serialization_RoundTrip()
    {
        // Arrange
        var map = new ConsequenceMonetization(new[]
        {
            new MonetizationFactor(1, 7.5e6d, "Statistical life", "2023 USDOT"),
            new MonetizationFactor(2, 350d, "Acreage", string.Empty),
        }, "$ (2026)");

        // Act
        var restored = new ConsequenceMonetization(map.ToXElement());

        // Assert
        Assert.AreEqual("$ (2026)", restored.MonetaryUnit);
        Assert.AreEqual(2, restored.Factors.Count);
        Assert.AreEqual(1, restored.Factors[0].ConsequenceType);
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(7.5e6d),
            BitConverter.DoubleToInt64Bits(restored.Factors[0].AmountPerUnit));
        Assert.AreEqual("2023 USDOT", restored.Factors[0].Vintage);
        Assert.ThrowsException<ArgumentNullException>(
            () => new ConsequenceMonetization((System.Xml.Linq.XElement)null!));
    }
}
