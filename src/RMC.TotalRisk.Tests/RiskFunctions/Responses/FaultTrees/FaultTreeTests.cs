using System;
using System.Linq;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.FaultTrees;

/// <summary>Tests the controlled fault-tree collection, traversal, and serialization surface.</summary>
[TestClass]
public class FaultTreeTests
{
    /// <summary>Verifies the default root and gate-only input ownership.</summary>
    [TestMethod]
    public void Test_Construction_TopEventGateAndGateOnlyInputs()
    {
        // Arrange
        var tree = new FaultTree();

        // Assert defaults.
        Assert.AreEqual("Top event", tree.Root.Name);
        Assert.AreEqual(FaultTreeGateType.Or, tree.Root.GateType);
        Assert.AreEqual(1, tree.Nodes.Count);

        // Act — leaves cannot own inputs.
        var basic = new FaultTreeBasicEventNode("A", new ProbabilitySource(0.2d));
        tree.Add(tree.Root.Id, basic);
        var exception = Assert.ThrowsException<InvalidOperationException>(
            () => tree.Add(basic.Id, new FaultTreeHouseEventNode("H", true)));

        // Assert
        StringAssert.Contains(exception.Message, "only fault-tree gates own inputs");
        Assert.AreEqual(2, tree.Nodes.Count);
    }

    /// <summary>Verifies add, insert, and move preserve deterministic input order.</summary>
    [TestMethod]
    public void Test_AddInsertMove_PreserveInputOrder()
    {
        // Arrange
        var tree = new FaultTree();
        var first = new FaultTreeBasicEventNode("First", new ProbabilitySource(0.1d));
        var last = new FaultTreeBasicEventNode("Last", new ProbabilitySource(0.2d));
        tree.Add(tree.Root.Id, first);
        tree.Add(tree.Root.Id, last);

        // Act
        var inserted = new FaultTreeBasicEventNode("Inserted", new ProbabilitySource(0.3d));
        tree.Insert(last.Id, inserted);
        tree.Move(first.Id, tree.Root.Id, null);

        // Assert
        CollectionAssert.AreEqual(new[] { "Inserted", "Last", "First" },
            tree.Root.Children.Select(child => child.Name).ToArray());
    }

    /// <summary>Verifies root protection and structural cycle rejection through moves.</summary>
    [TestMethod]
    public void Test_Move_RootProtectedAndCyclesRejected()
    {
        // Arrange
        var tree = new FaultTree();
        var outer = new FaultTreeGateNode("Outer", FaultTreeGateType.And);
        var inner = new FaultTreeGateNode("Inner", FaultTreeGateType.Or);
        tree.Add(tree.Root.Id, outer);
        tree.Add(outer.Id, inner);
        tree.Add(inner.Id, new FaultTreeBasicEventNode("A", new ProbabilitySource(0.2d)));

        // Act / Assert
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(
            () => tree.Move(tree.Root.Id, outer.Id)).Message, "cannot be moved");
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(
            () => tree.Move(outer.Id, inner.Id)).Message, "would create a cycle");
    }

    /// <summary>Verifies the traversal and query surface on a small tree.</summary>
    [TestMethod]
    public void Test_TraversalsAndQueries_Deterministic()
    {
        // Arrange
        var tree = new FaultTree();
        var and = new FaultTreeGateNode("And", FaultTreeGateType.And);
        tree.Add(tree.Root.Id, and);
        var a = new FaultTreeBasicEventNode("A", new ProbabilitySource(0.1d));
        var b = new FaultTreeBasicEventNode("B", new ProbabilitySource(0.2d));
        tree.Add(and.Id, a);
        tree.Add(and.Id, b);
        var house = new FaultTreeHouseEventNode("H", false);
        tree.Add(tree.Root.Id, house);

        // Act / Assert
        CollectionAssert.AreEqual(new[] { "Top event", "And", "A", "B", "H" },
            tree.DepthFirstPreOrder().Select(node => node.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "A", "B", "And", "H", "Top event" },
            tree.DepthFirstPostOrder().Select(node => node.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "Top event", "And", "H", "A", "B" },
            tree.BreadthFirst().Select(node => node.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "A", "B", "H" },
            tree.GetLeaves().Select(node => node.Name).ToArray());
        Assert.AreSame(a, tree.FindById(a.Id));
        Assert.AreSame(b, tree.FindByName("B").Single());
        Assert.AreSame(b, tree.FindByNameIgnoreCase("b").Single());
        Assert.AreEqual(2, tree.FindAll(node => node is FaultTreeBasicEventNode).Count);
        CollectionAssert.AreEqual(new[] { "And", "Top event" },
            tree.GetAncestors(a.Id).Select(node => node.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "A", "B" },
            tree.GetDescendants(and.Id).Select(node => node.Name).ToArray());
        Assert.IsTrue(tree.IsReachable(tree.Root.Id, b.Id));
        Assert.IsFalse(tree.IsReachable(and.Id, house.Id));
    }

    /// <summary>Verifies the explicit node-and-input serialization round trip.</summary>
    [TestMethod]
    public void Test_ToXElement_RoundTripsStructure()
    {
        // Arrange
        var tree = new FaultTree();
        var voting = new FaultTreeGateNode("Voting", FaultTreeGateType.KOfN, 2);
        tree.Add(tree.Root.Id, voting);
        tree.Add(voting.Id, new FaultTreeBasicEventNode("A", new ProbabilitySource(0.1d)));
        tree.Add(voting.Id, new FaultTreeBasicEventNode("B", new ProbabilitySource(0.2d)));
        tree.Add(voting.Id, new FaultTreeHouseEventNode("H", true));

        // Act
        XElement serialized = tree.ToXElement();
        var restored = new FaultTree(serialized);

        // Assert
        Assert.AreEqual(tree.Root.Id, restored.Root.Id);
        Assert.AreEqual(tree.Nodes.Count, restored.Nodes.Count);
        var restoredVoting = (FaultTreeGateNode)restored.FindById(voting.Id)!;
        Assert.AreEqual(FaultTreeGateType.KOfN, restoredVoting.GateType);
        Assert.AreEqual(2, restoredVoting.K);
        CollectionAssert.AreEqual(new[] { "A", "B", "H" },
            restoredVoting.Children.Select(node => node.Name).ToArray());
        Assert.IsTrue(((FaultTreeHouseEventNode)restored.FindByName("H").Single()).State);
        Assert.IsNull(serialized.Descendants("FaultTreeBasicEventNode")
            .First().Attribute("K"), "K is written for KOfN gates only.");
    }

    /// <summary>Verifies subtree hashing and structural equality on unowned transfer-free trees.</summary>
    [TestMethod]
    public void Test_SubtreeHashAndStructuralEquals_MetadataInert()
    {
        // Arrange — two content-equal trees with different names and ids.
        FaultTree Build(string label)
        {
            var tree = new FaultTree();
            var and = new FaultTreeGateNode($"And {label}", FaultTreeGateType.And);
            tree.Add(tree.Root.Id, and);
            tree.Add(and.Id, new FaultTreeBasicEventNode($"A {label}", new ProbabilitySource(0.1d)));
            tree.Add(and.Id, new FaultTreeBasicEventNode($"B {label}", new ProbabilitySource(0.2d)));
            return tree;
        }
        FaultTree left = Build("left");
        FaultTree right = Build("right");

        // Act / Assert — identical content hashes identically; a compute edit separates them.
        Assert.IsTrue(left.StructuralEquals(right));
        ((FaultTreeGateNode)right.Root.Children[0]).GateType = FaultTreeGateType.Or;
        Assert.IsFalse(left.StructuralEquals(right));
    }

    /// <summary>Verifies an unowned transfer-bearing tree defers identity to its future owner.</summary>
    [TestMethod]
    public void Test_UnownedTransferIdentity_Throws()
    {
        // Arrange
        var tree = new FaultTree();
        var basic = new FaultTreeBasicEventNode("A", new ProbabilitySource(0.2d));
        tree.Add(tree.Root.Id, basic);
        tree.LinkShared(tree.Root.Id, basic.Id);

        // Act / Assert
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(
            () => tree.SubtreeCanonicalHash(tree.Root.Id)).Message,
            "must be owned by a FaultTreeResponse");
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(
            () => tree.GetTopologicalOrder()).Message,
            "must be owned by a FaultTreeResponse");
    }

    /// <summary>Verifies duplicate persistent ids are rejected on read.</summary>
    [TestMethod]
    public void Test_Deserialization_RejectsDuplicateIds()
    {
        // Arrange
        var tree = new FaultTree();
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("A", new ProbabilitySource(0.2d)));
        XElement serialized = tree.ToXElement();
        XElement duplicated = new XElement(serialized);
        XElement nodes = duplicated.Element("Nodes")!;
        nodes.Add(new XElement(nodes.Elements().Last()));

        // Act / Assert
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(
            () => new FaultTree(duplicated)).Message, "duplicate node id");
    }
}
