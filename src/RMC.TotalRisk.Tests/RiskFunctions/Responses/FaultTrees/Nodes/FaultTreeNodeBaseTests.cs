using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.FaultTrees.Nodes;

/// <summary>Tests the shared authored fault-tree node contract.</summary>
[TestClass]
public class FaultTreeNodeBaseTests
{
    /// <summary>Verifies metadata setters raise change notification and null-coalesce.</summary>
    [TestMethod]
    public void Test_MetadataSetters_RaiseAndCoalesce()
    {
        // Arrange
        var node = new FaultTreeHouseEventNode("House", true);
        var raised = new List<string>();
        node.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        // Act
        node.Name = "Renamed";
        node.Name = "Renamed";
        node.Description = "Described";
        node.Name = null!;

        // Assert
        Assert.AreEqual("", node.Name);
        Assert.AreEqual("Described", node.Description);
        CollectionAssert.AreEqual(new[] { "Name", "Description", "Name" }, raised);
        Assert.IsTrue(node.IsTerminal);
        Assert.IsNull(node.Parent);
        Assert.AreEqual(0, node.Children.Count);
    }
}
