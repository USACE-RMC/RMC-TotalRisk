using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.FaultTrees.Nodes;

/// <summary>Tests the fault-tree gate node contract.</summary>
[TestClass]
public class FaultTreeGateNodeTests
{
    /// <summary>Verifies construction, compute-property notification, and undefined-value guards.</summary>
    [TestMethod]
    public void Test_GateTypeAndK_NotifyAndGuard()
    {
        // Arrange
        var gate = new FaultTreeGateNode("Voting", FaultTreeGateType.KOfN, 2);
        var raised = new List<string>();
        gate.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        // Act
        gate.GateType = FaultTreeGateType.And;
        gate.GateType = FaultTreeGateType.And;
        gate.K = 3;
        gate.K = 3;

        // Assert
        Assert.AreEqual(FaultTreeGateType.And, gate.GateType);
        Assert.AreEqual(3, gate.K);
        CollectionAssert.AreEqual(new[] { "GateType", "K" }, raised);
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => gate.GateType = (FaultTreeGateType)99);
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => new FaultTreeGateNode("Bad", (FaultTreeGateType)99));
    }
}
