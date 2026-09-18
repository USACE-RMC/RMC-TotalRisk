using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.FaultTrees.Nodes;

/// <summary>Tests the fault-tree transfer node addressing and mode contract.</summary>
[TestClass]
public class FaultTreeTransferNodeTests
{
    /// <summary>Verifies the shared-logical default and both accepted modes.</summary>
    [TestMethod]
    public void Test_LinkMode_SharedDefaultAndBothModesAccepted()
    {
        // Arrange
        var reference = new TreeNodeReference(null, Guid.NewGuid(), nodeName: "Target");

        // Act
        var defaulted = new FaultTreeTransferNode("Transfer", reference);
        var independent = new FaultTreeTransferNode("Clone", reference,
            linkMode: TreeLinkMode.IndependentClone);

        // Assert
        Assert.AreEqual(TreeLinkMode.SharedLogicalEvent, defaulted.LinkMode);
        Assert.AreEqual(TreeLinkMode.IndependentClone, independent.LinkMode);
        Assert.IsTrue(defaulted.IsTerminal);
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => new FaultTreeTransferNode("Bad", reference, linkMode: (TreeLinkMode)9));
    }

    /// <summary>Verifies internal/external addressing consistency guards.</summary>
    [TestMethod]
    public void Test_Addressing_ConsistencyGuards()
    {
        // Arrange
        var internalReference = new TreeNodeReference(null, Guid.NewGuid(), nodeName: "Local");
        var externalReference = new TreeNodeReference(Guid.NewGuid(), Guid.NewGuid(), "Other", "Remote");
        var external = new FaultTreeResponse { Name = "Other" };

        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(
            () => new FaultTreeTransferNode("Bad", null!));
        Assert.ThrowsException<ArgumentException>(
            () => new FaultTreeTransferNode("Bad", externalReference));
        Assert.ThrowsException<ArgumentException>(
            () => new FaultTreeTransferNode("Bad", internalReference, external));
        var valid = new FaultTreeTransferNode("Good", externalReference, external);
        Assert.AreSame(external, valid.TargetFunction);
    }

    /// <summary>Verifies the target projection repairs names once the transfer is attached.</summary>
    [TestMethod]
    public void Test_Target_ProjectsCurrentNodeMetadata()
    {
        // Arrange
        var tree = new FaultTree();
        var basic = new FaultTreeBasicEventNode("Original", new ProbabilitySource(0.2d));
        tree.Add(tree.Root.Id, basic);
        Guid transferId = tree.LinkShared(tree.Root.Id, basic.Id, "Shared");
        var transfer = (FaultTreeTransferNode)tree.FindById(transferId)!;

        // Act
        basic.Name = "Renamed";

        // Assert
        Assert.AreEqual(basic.Id, transfer.Target.NodeId);
        Assert.AreEqual("Renamed", transfer.Target.NodeName);
        Assert.IsNull(transfer.Target.FunctionId);
    }
}
