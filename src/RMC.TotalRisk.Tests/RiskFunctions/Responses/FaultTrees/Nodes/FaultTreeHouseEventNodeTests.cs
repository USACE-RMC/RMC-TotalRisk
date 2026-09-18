using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.FaultTrees.Nodes;

/// <summary>Tests the deterministic house-event node contract.</summary>
[TestClass]
public class FaultTreeHouseEventNodeTests
{
    /// <summary>Verifies the state toggles with notification.</summary>
    [TestMethod]
    public void Test_State_TogglesWithNotification()
    {
        // Arrange
        var node = new FaultTreeHouseEventNode("Gate closed", true);
        var raised = new List<string>();
        node.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        // Act
        node.State = false;
        node.State = false;

        // Assert
        Assert.IsFalse(node.State);
        CollectionAssert.AreEqual(new[] { "State" }, raised);
    }
}
