using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.Risk;

namespace RMC.TotalRisk.Tests.Analyses.Risk;

/// <summary>Tests the house-event override input record.</summary>
[TestClass]
public class HouseEventStateTests
{
    /// <summary>Verifies construction stores the address and state.</summary>
    [TestMethod]
    public void Test_Constructor_StoresAddressAndState()
    {
        var functionId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();

        var overrideState = new HouseEventState(functionId, nodeId, true);

        Assert.AreEqual(functionId, overrideState.FunctionId);
        Assert.AreEqual(nodeId, overrideState.NodeId);
        Assert.IsTrue(overrideState.State);
    }

    /// <summary>Verifies empty ids are rejected.</summary>
    [TestMethod]
    public void Test_Constructor_EmptyIds_Throw()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            new HouseEventState(Guid.Empty, Guid.NewGuid(), false));
        Assert.ThrowsException<ArgumentException>(() =>
            new HouseEventState(Guid.NewGuid(), Guid.Empty, false));
    }

    /// <summary>Verifies the XML round trip and the malformed-id refusal.</summary>
    [TestMethod]
    public void Test_Serialization_RoundTrip()
    {
        // Arrange
        var state = new HouseEventState(Guid.NewGuid(), Guid.NewGuid(), true);

        // Act
        var restored = new HouseEventState(state.ToXElement());

        // Assert
        Assert.AreEqual(state.FunctionId, restored.FunctionId);
        Assert.AreEqual(state.NodeId, restored.NodeId);
        Assert.IsTrue(restored.State);
        Assert.ThrowsException<ArgumentNullException>(() => new HouseEventState(null!));

        // A malformed id parses empty and refuses through the construction guards.
        var malformed = state.ToXElement();
        malformed.SetAttributeValue(nameof(HouseEventState.FunctionId), "not-a-guid");
        Assert.ThrowsException<ArgumentException>(() => new HouseEventState(malformed));
    }
}
