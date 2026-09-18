using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.CostBenefit;

namespace RMC.TotalRisk.Tests.Analyses.CostBenefit;

/// <summary>
/// Tests the partitioned-risk declaration: guards, the descending-boundary matrix, and the
/// XML round trip.
/// </summary>
[TestClass]
public class PmrmPartitionTests
{
    /// <summary>Verifies the null guard and the validation matrix.</summary>
    [TestMethod]
    public void Test_Ctor_GuardAndValidateMatrix()
    {
        // Act / Assert — construction refuses only null; the matrix rules live in Validate.
        Assert.ThrowsException<ArgumentNullException>(() => new PmrmPartition((IReadOnlyList<double>)null!));

        var empty = new PmrmPartition(Array.Empty<double>());
        (bool emptyValid, var emptyMessages) = empty.Validate();
        Assert.IsFalse(emptyValid);
        StringAssert.StartsWith(emptyMessages[0], "Error: The partition requires at least one exceedance boundary.");

        var outOfRange = new PmrmPartition(new[] { 1.5d });
        Assert.IsFalse(outOfRange.Validate().IsValid);

        var ascending = new PmrmPartition(new[] { 0.001d, 0.01d });
        (bool ascendingValid, var ascendingMessages) = ascending.Validate();
        Assert.IsFalse(ascendingValid);
        StringAssert.StartsWith(ascendingMessages[0],
            "Error: The partition boundaries must be strictly descending exceedance probabilities.");

        var valid = new PmrmPartition(new[] { 0.1d, 0.01d, 0.001d });
        Assert.IsTrue(valid.Validate().IsValid);
    }

    /// <summary>Verifies the XML round trip is bit-exact per boundary.</summary>
    [TestMethod]
    public void Test_Serialization_RoundTrip()
    {
        // Arrange
        var partition = new PmrmPartition(new[] { 0.1d, 0.012345678901234567d, 0.001d });

        // Act
        var restored = new PmrmPartition(partition.ToXElement());

        // Assert
        Assert.AreEqual(3, restored.ExceedanceBoundaries.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(partition.ExceedanceBoundaries[i]),
                BitConverter.DoubleToInt64Bits(restored.ExceedanceBoundaries[i]));
        }
        Assert.ThrowsException<ArgumentNullException>(() => new PmrmPartition((System.Xml.Linq.XElement)null!));
    }
}
