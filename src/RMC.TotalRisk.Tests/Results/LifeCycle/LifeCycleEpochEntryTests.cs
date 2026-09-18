using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.LifeCycle;

namespace RMC.TotalRisk.Tests.Results.LifeCycle;

/// <summary>
/// Tests the life-cycle epoch scope row: guards, echoes, and the defensive snapshot.
/// </summary>
[TestClass]
public class LifeCycleEpochEntryTests
{
    /// <summary>Verifies null arguments are refused.</summary>
    [TestMethod]
    public void Test_Ctor_NullArguments_Throw()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(
            () => new LifeCycleEpochEntry(null!, 0.1d, new[] { 1d }));
        Assert.ThrowsException<ArgumentNullException>(
            () => new LifeCycleEpochEntry("System", 0.1d, null!));
    }

    /// <summary>Verifies the properties echo the arguments.</summary>
    [TestMethod]
    public void Test_Ctor_PropertiesEchoed()
    {
        // Act
        var entry = new LifeCycleEpochEntry("Dam", 0.05d, new[] { 500d, 2d });

        // Assert
        Assert.AreEqual("Dam", entry.Name);
        Assert.AreEqual(0.05d, entry.FailureProbability);
        Assert.AreEqual(2, entry.ExpectedConsequences.Count);
        Assert.AreEqual(500d, entry.ExpectedConsequences[0]);
        Assert.AreEqual(2d, entry.ExpectedConsequences[1]);
    }

    /// <summary>Verifies the consequence list is a defensive snapshot.</summary>
    [TestMethod]
    public void Test_Ctor_Snapshot_InputMutationInert()
    {
        // Arrange
        var means = new List<double> { 500d };
        var entry = new LifeCycleEpochEntry("System", 0.05d, means);

        // Act
        means[0] = 999d;
        means.Add(1d);

        // Assert
        Assert.AreEqual(1, entry.ExpectedConsequences.Count);
        Assert.AreEqual(500d, entry.ExpectedConsequences[0]);
    }

    /// <summary>Verifies the stream lists echo when supplied and default empty when omitted.</summary>
    [TestMethod]
    public void Test_Ctor_StreamLists_EchoedAndEmptyByDefault()
    {
        // Act
        var bare = new LifeCycleEpochEntry("System", 0.05d, new[] { 500d });
        var full = new LifeCycleEpochEntry("System", 0.05d, new[] { 500d, 2d },
            new[] { 400d, 1.5d }, new[] { 100d, 0.5d });

        // Assert — the pre-stream form carries empty stream lists.
        Assert.AreEqual(0, bare.ExcessExpectedConsequences.Count);
        Assert.AreEqual(0, bare.FailExpectedConsequences.Count);

        // The stream-bearing form echoes per type.
        Assert.AreEqual(2, full.ExcessExpectedConsequences.Count);
        Assert.AreEqual(400d, full.ExcessExpectedConsequences[0]);
        Assert.AreEqual(1.5d, full.ExcessExpectedConsequences[1]);
        Assert.AreEqual(2, full.FailExpectedConsequences.Count);
        Assert.AreEqual(100d, full.FailExpectedConsequences[0]);
        Assert.AreEqual(0.5d, full.FailExpectedConsequences[1]);
    }

    /// <summary>Verifies a supplied stream list must align with the Total-stream list.</summary>
    [TestMethod]
    public void Test_Ctor_StreamMisalignment_Throws()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentException>(() => new LifeCycleEpochEntry(
            "System", 0.05d, new[] { 500d, 2d }, excessExpectedConsequences: new[] { 400d }));
        Assert.ThrowsException<ArgumentException>(() => new LifeCycleEpochEntry(
            "System", 0.05d, new[] { 500d, 2d }, failExpectedConsequences: new[] { 100d, 0.5d, 3d }));
    }
}
