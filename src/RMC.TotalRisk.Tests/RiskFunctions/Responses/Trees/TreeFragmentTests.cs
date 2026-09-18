using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.Trees;

/// <summary>Tests immutable in-memory tree-fragment metadata.</summary>
[TestClass]
public class TreeFragmentTests
{
    /// <summary>Verifies copy records unique source ids in deterministic subtree order.</summary>
    [TestMethod]
    public void Test_Copy_RecordsReadOnlySourceIdentity()
    {
        // Arrange
        var tree = new EventTree();
        var parent = new ChanceNode("Parent", new ProbabilitySource(0.4d));
        var child = new ChanceNode("Child", new ProbabilitySource(0.2d));
        tree.Add(tree.Root.Id, parent);
        tree.Add(parent.Id, child);

        // Act
        TreeFragment fragment = tree.Copy(parent.Id);

        // Assert
        Assert.AreEqual(parent.Id, fragment.SourceRootId);
        Assert.AreEqual(2, fragment.NodeCount);
        CollectionAssert.AreEqual(new[] { parent.Id, child.Id },
            new List<Guid>(fragment.SourceNodeIds));
        var collection = (ICollection<Guid>)fragment.SourceNodeIds;
        Assert.IsTrue(collection.IsReadOnly);
        Assert.ThrowsException<NotSupportedException>(() => collection.Add(Guid.NewGuid()));
    }
}
