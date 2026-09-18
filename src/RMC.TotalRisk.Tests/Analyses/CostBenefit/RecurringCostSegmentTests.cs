using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.CostBenefit;

namespace RMC.TotalRisk.Tests.Analyses.CostBenefit;

/// <summary>
/// Tests the recurring cost segment: guards, echoes, the optional end year, and the XML
/// round trip.
/// </summary>
[TestClass]
public class RecurringCostSegmentTests
{
    /// <summary>Verifies the construction guards, including the (start, end] ordering rule.</summary>
    [TestMethod]
    public void Test_Ctor_Guards()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new RecurringCostSegment(-1, 10d));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new RecurringCostSegment(0, double.NaN));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new RecurringCostSegment(10, 5d, endYear: 10));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new RecurringCostSegment(10, 5d, endYear: 3));
    }

    /// <summary>Verifies the properties echo, including a signed saving and the unbounded end.</summary>
    [TestMethod]
    public void Test_Ctor_PropertiesEchoed()
    {
        // Act
        var bounded = new RecurringCostSegment(10, -5d, 30, "Operating saving");
        var unbounded = new RecurringCostSegment(0, 10d);

        // Assert
        Assert.AreEqual(10, bounded.StartYear);
        Assert.AreEqual(-5d, bounded.AnnualAmount);
        Assert.AreEqual(30, bounded.EndYear);
        Assert.AreEqual("Operating saving", bounded.Label);
        Assert.IsNull(unbounded.EndYear);
        Assert.AreEqual(string.Empty, unbounded.Label);
    }

    /// <summary>Verifies the XML round trip preserves both the bounded and unbounded forms.</summary>
    [TestMethod]
    public void Test_Serialization_RoundTrip()
    {
        // Arrange
        var bounded = new RecurringCostSegment(3, 12.345678901234567d, 40, "O&M");
        var unbounded = new RecurringCostSegment(0, 10d);

        // Act
        var boundedRestored = new RecurringCostSegment(bounded.ToXElement());
        var unboundedRestored = new RecurringCostSegment(unbounded.ToXElement());

        // Assert
        Assert.AreEqual(bounded.StartYear, boundedRestored.StartYear);
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(bounded.AnnualAmount),
            BitConverter.DoubleToInt64Bits(boundedRestored.AnnualAmount));
        Assert.AreEqual(bounded.EndYear, boundedRestored.EndYear);
        Assert.AreEqual(bounded.Label, boundedRestored.Label);
        Assert.IsNull(unboundedRestored.EndYear, "An absent end-year attribute must read back as unbounded.");
        Assert.ThrowsException<ArgumentNullException>(() => new RecurringCostSegment(null!));
    }
}
