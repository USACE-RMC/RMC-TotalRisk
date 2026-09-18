using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.CostBenefit;

namespace RMC.TotalRisk.Tests.Analyses.CostBenefit;

/// <summary>
/// Tests the tagged cost stream: the empty default, null-entry guards, snapshots, and the
/// XML round trip across all three kinds.
/// </summary>
[TestClass]
public class CostStreamTests
{
    /// <summary>Verifies the default stream is empty across all three kinds.</summary>
    [TestMethod]
    public void Test_Ctor_EmptyDefault()
    {
        // Act
        var stream = new CostStream();

        // Assert
        Assert.AreEqual(0, stream.Capital.Count);
        Assert.AreEqual(0, stream.OperationsAndMaintenance.Count);
        Assert.AreEqual(0, stream.OperatingChanges.Count);
    }

    /// <summary>Verifies null entries are refused in every kind.</summary>
    [TestMethod]
    public void Test_Ctor_NullEntries_Throw()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentException>(() => new CostStream(
            new CapitalCostEntry[] { null! }));
        Assert.ThrowsException<ArgumentException>(() => new CostStream(
            null, new RecurringCostSegment[] { null! }));
        Assert.ThrowsException<ArgumentException>(() => new CostStream(
            null, null, new RecurringCostSegment[] { null! }));
    }

    /// <summary>Verifies the stored lists are defensive snapshots.</summary>
    [TestMethod]
    public void Test_Ctor_Snapshots_InputMutationInert()
    {
        // Arrange
        var capital = new List<CapitalCostEntry> { new(0, 1000d) };
        var stream = new CostStream(capital);

        // Act
        capital.Clear();

        // Assert
        Assert.AreEqual(1, stream.Capital.Count);
    }

    /// <summary>Verifies the XML round trip preserves every kind and entry value.</summary>
    [TestMethod]
    public void Test_Serialization_RoundTrip()
    {
        // Arrange
        var stream = new CostStream(
            new[] { new CapitalCostEntry(0, 1000d, "Initial"), new CapitalCostEntry(20, -200d, "Salvage") },
            new[] { new RecurringCostSegment(0, 10d, null, "O&M") },
            new[] { new RecurringCostSegment(10, -5d, 50, "Operating saving") });

        // Act
        var restored = new CostStream(stream.ToXElement());

        // Assert
        Assert.AreEqual(2, restored.Capital.Count);
        Assert.AreEqual(0, restored.Capital[0].Year);
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(-200d),
            BitConverter.DoubleToInt64Bits(restored.Capital[1].Amount));
        Assert.AreEqual(1, restored.OperationsAndMaintenance.Count);
        Assert.IsNull(restored.OperationsAndMaintenance[0].EndYear);
        Assert.AreEqual(1, restored.OperatingChanges.Count);
        Assert.AreEqual(50, restored.OperatingChanges[0].EndYear);
        Assert.AreEqual("Operating saving", restored.OperatingChanges[0].Label);
        Assert.ThrowsException<ArgumentNullException>(() => new CostStream((System.Xml.Linq.XElement)null!));
    }
}
