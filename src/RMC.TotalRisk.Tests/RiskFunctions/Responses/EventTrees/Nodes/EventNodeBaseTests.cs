using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.EventTrees.Nodes;

/// <summary>Tests common event-node identity, metadata, inspection, and notification.</summary>
[TestClass]
public class EventNodeBaseTests
{
    /// <summary>Verifies metadata notification and controlled relationship inspection.</summary>
    [TestMethod]
    public void Test_CommonSurface_NotifiesAndInspects()
    {
        var tree = new EventTree();
        var node = new ChanceNode("Old", new ProbabilitySource(0.2d));
        var changed = new List<string?>();
        node.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        tree.Add(tree.Root.Id, node);

        node.Name = "New";
        node.Description = "Description";
        node.IsFailure = false;

        Assert.AreSame(tree.Root, node.Parent);
        Assert.IsTrue(node.IsTerminal);
        CollectionAssert.Contains(changed, nameof(node.Name));
        CollectionAssert.Contains(changed, nameof(node.Description));
        CollectionAssert.Contains(changed, nameof(node.IsFailure));
    }
}
