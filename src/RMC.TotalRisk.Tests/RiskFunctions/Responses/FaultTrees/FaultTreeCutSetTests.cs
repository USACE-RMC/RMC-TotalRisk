using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.FaultTrees;

/// <summary>Tests the minimal cut-set inspection result.</summary>
[TestClass]
public class FaultTreeCutSetTests
{
    /// <summary>Verifies cut sets extracted from a coherent response carry authored addresses.</summary>
    [TestMethod]
    public void Test_MinimalCutSets_CarryAuthoredEvents()
    {
        // Arrange — Top = A OR (B AND C).
        var tree = new FaultTree();
        var a = new FaultTreeBasicEventNode("A", new ProbabilitySource(0.1d));
        tree.Add(tree.Root.Id, a);
        var and = new FaultTreeGateNode("Joint", FaultTreeGateType.And);
        tree.Add(tree.Root.Id, and);
        var b = new FaultTreeBasicEventNode("B", new ProbabilitySource(0.2d));
        var c = new FaultTreeBasicEventNode("C", new ProbabilitySource(0.3d));
        tree.Add(and.Id, b);
        tree.Add(and.Id, c);
        var response = new FaultTreeResponse(new[] { 0d, 1d }, tree)
        {
            Name = "Cut sets",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };

        // Act
        var cutSets = response.GetMinimalCutSets();

        // Assert — ordered by cardinality: {A} then {B, C}.
        Assert.AreEqual(2, cutSets.Count);
        Assert.AreEqual(1, cutSets[0].Events.Count);
        Assert.AreEqual(a.Id, cutSets[0].Events[0].NodeId);
        Assert.AreEqual(2, cutSets[1].Events.Count);
        CollectionAssert.AreEquivalent(new[] { b.Id, c.Id },
            new[] { cutSets[1].Events[0].NodeId, cutSets[1].Events[1].NodeId });
        Assert.IsFalse(string.IsNullOrEmpty(cutSets[0].Events[0].CanonicalPath));
    }

    /// <summary>Verifies the member-list null guard.</summary>
    [TestMethod]
    public void Test_Constructor_NullGuards()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() => new FaultTreeCutSet(null!));
    }
}
