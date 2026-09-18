using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.EventTrees.Nodes;

/// <summary>Tests internal and external event-tree link nodes in both link modes.</summary>
[TestClass]
public class EventTreeLinkNodeTests
{
    /// <summary>Verifies internal links retain their immutable node address and default classification.</summary>
    [TestMethod]
    public void Test_InternalConstructor_SetsIndependentTarget()
    {
        var target = new TreeNodeReference(null, Guid.NewGuid(), nodeName: "Target");

        var link = new EventTreeLinkNode("Reuse", target);

        Assert.AreEqual(TreeLinkMode.IndependentClone, link.LinkMode);
        Assert.AreSame(target, link.Target);
        Assert.IsNull(link.TargetFunction);
        Assert.IsTrue(link.IsFailure);
    }

    /// <summary>Verifies an external link projects current live function metadata into its address.</summary>
    [TestMethod]
    public void Test_ExternalConstructor_UsesLiveTargetFunction()
    {
        var targetTree = new EventTree();
        var targetNode = new ChanceNode("Branch", new ProbabilitySource(0.25d));
        targetTree.Add(targetTree.Root.Id, targetNode);
        var targetFunction = Response("Stored tree", targetTree);
        var reference = new TreeNodeReference(targetFunction.Id, targetNode.Id,
            targetFunction.Name, targetNode.Name);
        var link = new EventTreeLinkNode("External reuse", reference, targetFunction);

        targetFunction.Name = "Renamed stored tree";
        targetNode.Name = "Renamed branch";

        Assert.AreSame(targetFunction, link.TargetFunction);
        Assert.AreEqual(targetFunction.Id, link.Target.FunctionId);
        Assert.AreEqual("Renamed stored tree", link.Target.FunctionName);
        Assert.AreEqual("Renamed branch", link.Target.NodeName);
    }

    /// <summary>Verifies event trees accept shared-logical link semantics.</summary>
    [TestMethod]
    public void Test_SharedLogicalMode_IsAccepted()
    {
        var target = new TreeNodeReference(null, Guid.NewGuid(), nodeName: "Target");

        var link = new EventTreeLinkNode("Shared reuse", target,
            linkMode: TreeLinkMode.SharedLogicalEvent);

        Assert.AreEqual(TreeLinkMode.SharedLogicalEvent, link.LinkMode);
        Assert.AreSame(target, link.Target);
        Assert.IsNull(link.TargetFunction);
        Assert.IsTrue(link.IsFailure);
    }

    /// <summary>Verifies an undefined link mode is rejected at construction.</summary>
    [TestMethod]
    public void Test_UndefinedLinkMode_Throws()
    {
        var target = new TreeNodeReference(null, Guid.NewGuid());

        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new EventTreeLinkNode("Invalid", target, linkMode: (TreeLinkMode)7));
    }

    /// <summary>Builds a labeled response for link-node fixtures.</summary>
    private static EventTreeResponse Response(string name, EventTree tree)
    {
        return new EventTreeResponse(new[] { 0d, 1d }, tree)
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }
}
