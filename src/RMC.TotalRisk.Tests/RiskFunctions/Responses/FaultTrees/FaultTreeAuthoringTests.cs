using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.FaultTrees;

/// <summary>Tests transactional fault-tree fragment, transfer, delete, and pruning operations.</summary>
[TestClass]
public class FaultTreeAuthoringTests
{
    /// <summary>Builds the shared authoring fixture: Top ← And(A, B) with a shared transfer to And.</summary>
    /// <param name="and">The interior gate.</param>
    /// <param name="transferId">The internal shared transfer id.</param>
    /// <returns>The tree.</returns>
    private static FaultTree BuildFixture(out FaultTreeGateNode and, out Guid transferId)
    {
        var tree = new FaultTree();
        and = new FaultTreeGateNode("Joint", FaultTreeGateType.And);
        tree.Add(tree.Root.Id, and);
        tree.Add(and.Id, new FaultTreeBasicEventNode("A", new ProbabilitySource(0.1d)));
        tree.Add(and.Id, new FaultTreeBasicEventNode("B", new ProbabilitySource(0.2d)));
        transferId = tree.LinkShared(tree.Root.Id, and.Id, "Shared joint");
        return tree;
    }

    /// <summary>Verifies copy/paste produces fresh ids and remaps fragment-internal targets.</summary>
    [TestMethod]
    public void Test_CopyPasteClone_FreshIdsAndInternalRemap()
    {
        // Arrange — copy a subtree that contains a transfer to a node inside the fragment.
        var tree = new FaultTree();
        var outer = new FaultTreeGateNode("Outer", FaultTreeGateType.Or);
        tree.Add(tree.Root.Id, outer);
        var basic = new FaultTreeBasicEventNode("A", new ProbabilitySource(0.1d));
        tree.Add(outer.Id, basic);
        tree.LinkShared(outer.Id, basic.Id, "Shared A");

        // Act
        TreeFragment fragment = tree.Copy(outer.Id);
        Guid pastedId = tree.PasteClone(tree.Root.Id, fragment);
        var pasted = (FaultTreeGateNode)tree.FindById(pastedId)!;

        // Assert — fresh ids everywhere; the pasted transfer targets the pasted basic.
        Assert.AreNotEqual(outer.Id, pastedId);
        var pastedTransfer = (FaultTreeTransferNode)pasted.Children[1];
        var pastedBasic = pasted.Children[0];
        Assert.AreNotEqual(basic.Id, pastedBasic.Id);
        Assert.AreEqual(pastedBasic.Id, pastedTransfer.Target.NodeId);
        Assert.AreEqual(7, tree.Nodes.Count);
    }

    /// <summary>Verifies replace applies the reference policy and keeps fresh identity.</summary>
    [TestMethod]
    public void Test_ReplaceSubtree_HonorsReferencePolicy()
    {
        // Arrange
        FaultTree tree = BuildFixture(out FaultTreeGateNode and, out _);
        var replacementSource = new FaultTree();
        var house = new FaultTreeHouseEventNode("H", true);
        replacementSource.Add(replacementSource.Root.Id, house);
        TreeFragment replacement = replacementSource.Copy(house.Id);

        // Act / Assert — referenced subtree rejects by default, then cascades away its transfer.
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(
            () => tree.ReplaceSubtree(and.Id, replacement)).Message, "referenced by internal transfer");
        Guid replacedId = tree.ReplaceSubtree(and.Id, replacement, TreeDeletePolicy.CascadeLinks);
        Assert.IsInstanceOfType<FaultTreeHouseEventNode>(tree.FindById(replacedId));
        Assert.AreEqual(0, tree.GetInternalReferences().Count);
        Assert.AreEqual(2, tree.Nodes.Count);
    }

    /// <summary>Verifies all three delete policies for referenced subtrees.</summary>
    [TestMethod]
    public void Test_Delete_AllThreePolicies()
    {
        // Reject by default.
        FaultTree rejecting = BuildFixture(out FaultTreeGateNode rejectAnd, out _);
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(
            () => rejecting.Delete(rejectAnd.Id)).Message, "referenced by internal transfer");

        // Cascade removes the incoming transfer with the subtree.
        FaultTree cascading = BuildFixture(out FaultTreeGateNode cascadeAnd, out _);
        cascading.Delete(cascadeAnd.Id, TreeDeletePolicy.CascadeLinks);
        Assert.AreEqual(1, cascading.Nodes.Count);

        // Materialize preserves an equivalent explicit clone before removal.
        FaultTree materializing = BuildFixture(out FaultTreeGateNode materializeAnd, out _);
        materializing.Delete(materializeAnd.Id, TreeDeletePolicy.MaterializeLinks);
        var survivor = (FaultTreeGateNode)materializing.Root.Children.Single();
        Assert.AreEqual("Joint", survivor.Name);
        CollectionAssert.AreEqual(new[] { "A", "B" },
            survivor.Children.Select(node => node.Name).ToArray());
    }

    /// <summary>Verifies materialization replaces a transfer with a fresh-id deep clone.</summary>
    [TestMethod]
    public void Test_MaterializeLink_DeepClonesTarget()
    {
        // Arrange
        FaultTree tree = BuildFixture(out FaultTreeGateNode and, out Guid transferId);

        // Act
        Guid materializedId = tree.MaterializeLink(transferId);
        var materialized = (FaultTreeGateNode)tree.FindById(materializedId)!;

        // Assert
        Assert.AreNotEqual(and.Id, materializedId);
        CollectionAssert.AreEqual(new[] { "A", "B" },
            materialized.Children.Select(node => node.Name).ToArray());
        Assert.AreNotEqual(and.Children[0].Id, materialized.Children[0].Id);
        Assert.AreEqual(0, tree.GetInternalReferences().Count);
    }

    /// <summary>Verifies reference queries and reachability treat transfer targets as live.</summary>
    [TestMethod]
    public void Test_ReferenceQueriesAndPrune_TransferTargetsReachable()
    {
        // Arrange — an unreachable helper subtree kept alive only by a transfer.
        FaultTree tree = BuildFixture(out FaultTreeGateNode and, out Guid transferId);
        var orphan = new FaultTreeBasicEventNode("Orphan", new ProbabilitySource(0.4d));
        var holder = new FaultTreeGateNode("Holder", FaultTreeGateType.Or);
        tree.Add(tree.Root.Id, holder);
        tree.Add(holder.Id, orphan);
        tree.Delete(holder.Id, TreeDeletePolicy.CascadeLinks);

        // Act / Assert
        var transfer = (FaultTreeTransferNode)tree.FindById(transferId)!;
        CollectionAssert.AreEqual(new[] { transfer },
            tree.GetIncomingReferences(and.Id).ToArray());
        CollectionAssert.AreEqual(new[] { transfer },
            tree.GetOutgoingReferences(tree.Root.Id).ToArray());
        Assert.AreEqual(1, tree.GetInternalReferences().Count);
        Assert.AreEqual(0, tree.GetExternalReferences().Count);
        Assert.AreEqual(0, tree.GetUnreachableNodes().Count);
        Assert.AreEqual(0, tree.PruneUnreachable().Count);
    }

    /// <summary>Verifies a failed transactional mutation restores the exact prior topology.</summary>
    [TestMethod]
    public void Test_FailedMutation_RollsBackExactly()
    {
        // Arrange
        FaultTree tree = BuildFixture(out FaultTreeGateNode and, out _);
        string[] before = tree.DepthFirstPreOrder().Select(node => $"{node.Name}:{node.Id:N}").ToArray();

        // Act — moving the gate under itself must fail and change nothing.
        Assert.ThrowsException<InvalidOperationException>(() => tree.Move(and.Id, and.Id));

        // Assert
        CollectionAssert.AreEqual(before,
            tree.DepthFirstPreOrder().Select(node => $"{node.Name}:{node.Id:N}").ToArray());
    }

    /// <summary>Builds a labeled response over hazards 0 and 1 for owner-requiring operations.</summary>
    /// <param name="tree">The authored tree.</param>
    /// <param name="name">The response name.</param>
    /// <returns>The owning response.</returns>
    private static FaultTreeResponse Own(FaultTree tree, string name = "Authoring owner")
    {
        return new FaultTreeResponse(new[] { 0d, 1d }, tree)
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }

    /// <summary>
    /// Verifies expanded topological order: an unowned plain tree walks breadth-first, an unowned
    /// transfer-bearing tree refuses, and an owned tree repeats transfer targets and labels
    /// external occurrences with their function identity.
    /// </summary>
    [TestMethod]
    public void Test_GetTopologicalOrder_ExpandsTransfersWithOwnership()
    {
        // Unowned without transfers: plain breadth-first references.
        var plain = new FaultTree();
        plain.Add(plain.Root.Id, new FaultTreeBasicEventNode("A", new ProbabilitySource(0.1d)));
        var plainOrder = plain.GetTopologicalOrder();
        Assert.AreEqual(2, plainOrder.Count);
        Assert.AreEqual(plain.Root.Id, plainOrder[0].NodeId);
        Assert.IsNull(plainOrder[0].FunctionId);

        // Unowned with a transfer: refused with the ownership diagnostic.
        FaultTree unowned = BuildFixture(out _, out _);
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(
            () => unowned.GetTopologicalOrder()).Message, "must be owned");

        // Owned with internal and external transfers: targets repeat, externals carry identity.
        var externalTree = new FaultTree();
        var externalBasic = new FaultTreeBasicEventNode("External A", new ProbabilitySource(0.2d));
        externalTree.Add(externalTree.Root.Id, externalBasic);
        FaultTreeResponse external = Own(externalTree, "Topology external");
        FaultTree tree = BuildFixture(out FaultTreeGateNode and, out _);
        tree.LinkShared(tree.Root.Id, external, externalBasic.Id, "External use");
        FaultTreeResponse owner = Own(tree);
        var order = tree.GetTopologicalOrder();
        Assert.IsTrue(owner.CompiledVariableCount >= 3);
        Assert.AreEqual(2, order.Count(reference => reference.NodeId == and.Id),
            "The shared transfer must repeat its target occurrence.");
        Assert.AreEqual(1, order.Count(reference => reference.FunctionId == external.Id),
            "The external occurrence must carry its function identity.");
    }

    /// <summary>
    /// Verifies unreachable-node detection and pruning after a serialized input edge is lost:
    /// the orphaned subtree is previewed and removed atomically, and pruning is idempotent.
    /// </summary>
    [TestMethod]
    public void Test_PruneUnreachable_RemovesOrphanedSubtree()
    {
        // Arrange — drop the orphan gate's parent edge from the serialized form.
        var tree = new FaultTree();
        var orphan = new FaultTreeGateNode("Orphan", FaultTreeGateType.Or);
        tree.Add(tree.Root.Id, orphan);
        var inside = new FaultTreeBasicEventNode("Inside", new ProbabilitySource(0.1d));
        tree.Add(orphan.Id, inside);
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("Kept", new ProbabilitySource(0.2d)));
        System.Xml.Linq.XElement xml = tree.ToXElement();
        xml.Element("Inputs")!.Elements("Input")
            .Single(input => input.Attribute("ChildNodeId")!.Value == orphan.Id.ToString("D"))
            .Remove();
        var reloaded = new FaultTree(xml);

        // Act
        var unreachable = reloaded.GetUnreachableNodes();
        var removed = reloaded.PruneUnreachable();

        // Assert — the orphan pair is detected, removed, and pruning is then a no-op.
        CollectionAssert.AreEquivalent(new[] { orphan.Id, inside.Id },
            unreachable.Select(node => node.Id).ToArray());
        CollectionAssert.AreEquivalent(new[] { orphan.Id, inside.Id }, removed.ToArray());
        Assert.IsNull(reloaded.FindById(orphan.Id));
        Assert.AreEqual(2, reloaded.Nodes.Count);
        Assert.AreEqual(0, reloaded.PruneUnreachable().Count);
    }

    /// <summary>
    /// Verifies fragment transfer rewriting for targets outside the fragment: a same-tree paste
    /// preserves the internal target, and a cross-tree paste from an owned source promotes the
    /// reference to an external transfer bound to the source's owning response.
    /// </summary>
    [TestMethod]
    public void Test_FragmentTransfers_PreserveOrPromoteOutsideTargets()
    {
        // Arrange — the copied gate's transfer targets a node outside the fragment.
        var sourceTree = new FaultTree();
        var target = new FaultTreeBasicEventNode("Outside target", new ProbabilitySource(0.3d));
        sourceTree.Add(sourceTree.Root.Id, target);
        var holder = new FaultTreeGateNode("Holder", FaultTreeGateType.And);
        sourceTree.Add(sourceTree.Root.Id, holder);
        sourceTree.Add(holder.Id, new FaultTreeBasicEventNode("Local", new ProbabilitySource(0.4d)));
        sourceTree.LinkShared(holder.Id, target.Id, "Outside use");
        FaultTreeResponse sourceOwner = Own(sourceTree, "Fragment source owner");
        TreeFragment fragment = sourceTree.Copy(holder.Id);

        // Act / Assert — same-tree paste preserves the internal outside-fragment target.
        Guid samePasteId = sourceTree.PasteClone(sourceTree.Root.Id, fragment);
        var sameTransfer = (FaultTreeTransferNode)((FaultTreeGateNode)sourceTree
            .FindById(samePasteId)!).Children[1];
        Assert.IsNull(sameTransfer.TargetFunction);
        Assert.AreEqual(target.Id, sameTransfer.Target.NodeId);

        // Cross-tree paste promotes the reference to the owned source function.
        var destination = new FaultTree();
        Guid crossPasteId = destination.PasteClone(destination.Root.Id, fragment);
        var crossTransfer = (FaultTreeTransferNode)((FaultTreeGateNode)destination
            .FindById(crossPasteId)!).Children[1];
        Assert.AreSame(sourceOwner, crossTransfer.TargetFunction);
        Assert.AreEqual(sourceOwner.Id, crossTransfer.Target.FunctionId);
        Assert.AreEqual(target.Id, crossTransfer.Target.NodeId);

        // An external transfer inside a fragment stays bound to its live function.
        var borrowTree = new FaultTree();
        var borrowHolder = new FaultTreeGateNode("Borrow holder", FaultTreeGateType.And);
        borrowTree.Add(borrowTree.Root.Id, borrowHolder);
        borrowTree.LinkShared(borrowHolder.Id, sourceOwner, target.Id, "External borrow");
        TreeFragment borrowFragment = borrowTree.Copy(borrowHolder.Id);
        Guid borrowPasteId = borrowTree.PasteClone(borrowTree.Root.Id, borrowFragment);
        var borrowTransfer = (FaultTreeTransferNode)((FaultTreeGateNode)borrowTree
            .FindById(borrowPasteId)!).Children[0];
        Assert.AreSame(sourceOwner, borrowTransfer.TargetFunction);
    }

    /// <summary>Verifies destination-sibling insertion and the deterministic placement guards.</summary>
    [TestMethod]
    public void Test_PasteClone_BeforeSiblingAndGuards()
    {
        // Arrange
        FaultTree tree = BuildFixture(out FaultTreeGateNode and, out Guid transferId);
        TreeFragment fragment = tree.Copy(and.Children[0].Id);

        // Act — insert before the existing transfer sibling.
        Guid pastedId = tree.PasteClone(tree.Root.Id, fragment, transferId);

        // Assert — position, then the non-gate parent and foreign-sibling guards.
        Assert.AreEqual(pastedId, tree.Root.Children[1].Id);
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(
            () => tree.PasteClone(and.Children[0].Id, fragment)).Message,
            "only fault-tree gates own inputs");
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(
            () => tree.PasteClone(tree.Root.Id, fragment, and.Children[0].Id)).Message,
            "not a child of the destination parent");
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(
            () => tree.ReplaceSubtree(tree.Root.Id, fragment)).Message,
            "top-event gate cannot be replaced");
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(
            () => tree.MaterializeLink(and.Id)).Message,
            "not a fault-tree transfer");
    }

    /// <summary>
    /// Verifies materialization diagnostics for unresolvable targets: an unresolved external
    /// reference and a dangling serialized internal target both fail loudly without mutation.
    /// </summary>
    [TestMethod]
    public void Test_MaterializeLink_UnresolvableTargetsFailLoudly()
    {
        // Unresolved external reference: a by-reference read with no resolver.
        var externalTree = new FaultTree();
        var externalBasic = new FaultTreeBasicEventNode("External A", new ProbabilitySource(0.2d));
        externalTree.Add(externalTree.Root.Id, externalBasic);
        FaultTreeResponse external = Own(externalTree, "Materialize external");
        var ownerTree = new FaultTree();
        ownerTree.LinkShared(ownerTree.Root.Id, external, externalBasic.Id, "External use");
        FaultTreeResponse owner = Own(ownerTree, "Materialize owner");
        var unresolved = new FaultTreeResponse(
            owner.ToXElement(RiskSerializationMode.ByReference));
        FaultTreeTransferNode unresolvedTransfer =
            unresolved.FaultTree.Nodes.OfType<FaultTreeTransferNode>().Single();
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(
            () => unresolved.FaultTree.MaterializeLink(unresolvedTransfer.Id)).Message,
            "unresolved");

        // Dangling internal target: the serialized node id is rewritten to a missing node.
        FaultTree tree = BuildFixture(out _, out Guid transferId);
        System.Xml.Linq.XElement xml = tree.ToXElement();
        System.Xml.Linq.XElement targetElement = xml.Element("Nodes")!
            .Elements(nameof(FaultTreeTransferNode)).Single().Element("Target")!;
        targetElement.SetAttributeValue("NodeId", Guid.NewGuid().ToString("D"));
        targetElement.SetAttributeValue("NodeName", null);
        var dangling = new FaultTree(xml);
        Guid danglingTransferId = dangling.Nodes.OfType<FaultTreeTransferNode>().Single().Id;
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(
            () => dangling.MaterializeLink(danglingTransferId)).Message,
            "could not be resolved");
    }

    /// <summary>Verifies the graph-level mutation checkpoint restores the exact captured state.</summary>
    [TestMethod]
    public void Test_MutationCheckpoint_RestoresCapturedState()
    {
        // Arrange
        FaultTree tree = BuildFixture(out FaultTreeGateNode and, out _);
        string[] before = tree.DepthFirstPreOrder().Select(node => $"{node.Name}:{node.Id:N}").ToArray();
        object checkpoint = tree.CreateMutationCheckpoint();

        // Act — mutate, then restore the checkpoint exactly.
        tree.Add(and.Id, new FaultTreeBasicEventNode("Extra", new ProbabilitySource(0.5d)));
        Assert.AreEqual(before.Length + 1, tree.Nodes.Count);
        tree.RestoreMutationCheckpoint(checkpoint);

        // Assert
        CollectionAssert.AreEqual(before,
            tree.DepthFirstPreOrder().Select(node => $"{node.Name}:{node.Id:N}").ToArray());
        Assert.ThrowsException<ArgumentException>(
            () => tree.RestoreMutationCheckpoint(new object()));
    }
}
