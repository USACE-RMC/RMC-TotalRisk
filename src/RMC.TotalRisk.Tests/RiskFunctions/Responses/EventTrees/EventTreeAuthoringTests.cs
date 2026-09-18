using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.EventTrees;

/// <summary>
/// Tests event-tree fragment authoring, replacement, materialization, pruning, reference queries,
/// expanded topological ordering, and transaction rollback.
/// </summary>
[TestClass]
public class EventTreeAuthoringTests
{
    /// <summary>Verifies paste snapshots local content, remaps internal targets, and allocates fresh ids.</summary>
    [TestMethod]
    public void Test_CopyPasteClone_InternalReferencesRemapAndIdentityIsInvariant()
    {
        // Arrange
        var tree = new EventTree();
        var sequence = new ChanceNode("Sequence", new ProbabilitySource(0.4d)) { IsFailure = false };
        var direct = new ChanceNode("Direct", new ProbabilitySource(0.25d));
        tree.Add(tree.Root.Id, sequence);
        tree.Add(sequence.Id, direct);
        tree.LinkIndependent(sequence.Id, direct.Id, "Repeated direct");
        tree.Add(tree.Root.Id, new RemainderNode("No sequence"));
        _ = Response(tree);
        byte[] copiedIdentity = tree.SubtreeCanonicalHash(sequence.Id);
        TreeFragment fragment = tree.Copy(sequence.Id);

        // Act
        sequence.Name = "Edited after copy";
        direct.ProbabilitySource = new ProbabilitySource(0.35d);
        Guid pastedId = tree.PasteClone(tree.Root.Id, fragment);

        // Assert
        EventNodeBase pasted = tree.FindById(pastedId)!;
        Assert.AreEqual("Sequence", pasted.Name);
        EventNodeBase pastedDirect = pasted.Children.OfType<ChanceNode>().Single();
        EventTreeLinkNode pastedLink = pasted.Children.OfType<EventTreeLinkNode>().Single();
        Assert.AreEqual(0.25d,
            ((ChanceNode)pastedDirect).ProbabilitySource.ScalarProbability!.Value);
        Assert.AreEqual(pastedDirect.Id, pastedLink.Target.NodeId);
        Assert.IsFalse(fragment.SourceNodeIds.Contains(pasted.Id));
        Assert.IsFalse(fragment.SourceNodeIds.Contains(pastedDirect.Id));
        CollectionAssert.AreEqual(copiedIdentity, tree.SubtreeCanonicalHash(pasted.Id));
    }

    /// <summary>Verifies replace rejects references transactionally and cascade removes them explicitly.</summary>
    [TestMethod]
    public void Test_ReplaceSubtree_ReferencePoliciesAreExplicit()
    {
        // Arrange
        var tree = new EventTree();
        var target = new ChanceNode("Old", new ProbabilitySource(0.2d));
        tree.Add(tree.Root.Id, target);
        Guid linkId = tree.LinkIndependent(tree.Root.Id, target.Id, "Old reuse");
        tree.Add(tree.Root.Id, new RemainderNode("No failure"));
        EventTreeResponse response = Response(tree);
        TreeFragment replacement = Fragment(0.6d);
        string before = tree.ToXElement().ToString(SaveOptions.DisableFormatting);
        byte[] hashBefore = response.CanonicalHash();

        // Act / Assert
        Assert.ThrowsException<InvalidOperationException>(() =>
            tree.ReplaceSubtree(target.Id, replacement));
        Assert.AreEqual(before, tree.ToXElement().ToString(SaveOptions.DisableFormatting));
        CollectionAssert.AreEqual(hashBefore, response.CanonicalHash());

        Guid replacementId = tree.ReplaceSubtree(target.Id, replacement,
            TreeDeletePolicy.CascadeLinks);
        Assert.IsNull(tree.FindById(target.Id));
        Assert.IsNull(tree.FindById(linkId));
        Assert.IsNotNull(tree.FindById(replacementId));
        Assert.AreEqual(0.6d, response.SampleResponseFunction()[0].Y, 1e-14d);
    }

    /// <summary>Verifies replace can materialize incoming links before removing the old target.</summary>
    [TestMethod]
    public void Test_ReplaceSubtree_MaterializeLinks_PreservesIncomingOccurrence()
    {
        // Arrange
        var tree = new EventTree();
        var target = new ChanceNode("Old", new ProbabilitySource(0.2d)) { IsFailure = false };
        tree.Add(tree.Root.Id, target);
        Guid linkId = tree.LinkIndependent(tree.Root.Id, target.Id, "Preserved failure");
        tree.Add(tree.Root.Id, new RemainderNode("No failure"));
        EventTreeResponse response = Response(tree);
        TreeFragment replacement = Fragment(0.6d);

        // Act
        Guid replacementId = tree.ReplaceSubtree(target.Id, replacement,
            TreeDeletePolicy.MaterializeLinks);

        // Assert
        Assert.IsNull(tree.FindById(target.Id));
        Assert.IsNull(tree.FindById(linkId));
        Assert.AreEqual(0, tree.GetInternalReferences().Count);
        Assert.IsNotNull(tree.FindById(replacementId));
        ChanceNode materialized = tree.Nodes.OfType<ChanceNode>()
            .Single(node => node.Id != replacementId);
        Assert.AreEqual("Preserved failure", materialized.Name);
        Assert.IsTrue(materialized.IsFailure);
        Assert.AreEqual(0.8d, response.SampleResponseFunction()[0].Y, 1e-14d);
    }

    /// <summary>Verifies an external link materializes to an explicit numeric-equivalent clone.</summary>
    [TestMethod]
    public void Test_MaterializeLink_ExternalTargetPreservesConditionalProbability()
    {
        // Arrange
        var targetTree = new EventTree();
        var targetNode = new ChanceNode("Target", new ProbabilitySource(0.3d));
        targetTree.Add(targetTree.Root.Id, targetNode);
        EventTreeResponse target = Response(targetTree, "External target");
        var ownerTree = new EventTree();
        Guid linkId = ownerTree.LinkIndependent(ownerTree.Root.Id, target, targetNode.Id,
            "External occurrence");
        ownerTree.Add(ownerTree.Root.Id, new RemainderNode("No failure"));
        EventTreeResponse owner = Response(ownerTree, "Owner");
        double before = owner.SampleResponseFunction()[0].Y;

        // Act
        Guid materializedId = ownerTree.MaterializeLink(linkId);

        // Assert
        Assert.IsNull(ownerTree.FindById(linkId));
        Assert.AreNotEqual(targetNode.Id, materializedId);
        Assert.AreEqual(0, ownerTree.GetExternalReferences().Count);
        var materialized = (ChanceNode)ownerTree.FindById(materializedId)!;
        Assert.AreEqual("External occurrence", materialized.Name);
        Assert.AreEqual(0.3d, materialized.ProbabilitySource.ScalarProbability!.Value);
        Assert.AreEqual(before, owner.SampleResponseFunction()[0].Y);
    }

    /// <summary>Verifies unresolved external materialization reports context and rolls back.</summary>
    [TestMethod]
    public void Test_MaterializeLink_UnresolvedTargetReportsDiagnosticAndRestoresTree()
    {
        // Arrange
        var targetTree = new EventTree();
        var targetNode = new ChanceNode("Target", new ProbabilitySource(0.3d));
        targetTree.Add(targetTree.Root.Id, targetNode);
        EventTreeResponse target = Response(targetTree, "External target");
        var ownerTree = new EventTree();
        Guid linkId = ownerTree.LinkIndependent(ownerTree.Root.Id, target, targetNode.Id,
            "External occurrence");
        ownerTree.Add(ownerTree.Root.Id, new RemainderNode("No failure"));
        EventTreeResponse owner = Response(ownerTree, "Owner");
        XElement persisted = owner.ToXElement(RiskSerializationMode.ByReference);
        var unresolved = new EventTreeResponse(persisted);
        string before = unresolved.EventTree.ToXElement(RiskSerializationMode.ByReference)
            .ToString(SaveOptions.DisableFormatting);

        // Act
        InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(() =>
            unresolved.EventTree.MaterializeLink(linkId));

        // Assert
        StringAssert.Contains(exception.Message, "MaterializeLink");
        StringAssert.Contains(exception.Message, "unresolved");
        Assert.AreEqual(before, unresolved.EventTree.ToXElement(RiskSerializationMode.ByReference)
            .ToString(SaveOptions.DisableFormatting));
    }

    /// <summary>Verifies delete materialization preserves each incoming link as an explicit clone.</summary>
    [TestMethod]
    public void Test_Delete_MaterializeLinks_CompletesPolicyWithoutDanglingTargets()
    {
        // Arrange
        var tree = new EventTree();
        var target = new ChanceNode("Reusable", new ProbabilitySource(0.25d)) { IsFailure = false };
        tree.Add(tree.Root.Id, target);
        Guid linkId = tree.LinkIndependent(tree.Root.Id, target.Id, "Failure occurrence");
        tree.Add(tree.Root.Id, new RemainderNode("No failure"));
        EventTreeResponse response = Response(tree);

        // Act
        tree.Delete(target.Id, TreeDeletePolicy.MaterializeLinks);

        // Assert
        Assert.IsNull(tree.FindById(target.Id));
        Assert.IsNull(tree.FindById(linkId));
        Assert.AreEqual(0, tree.GetInternalReferences().Count);
        ChanceNode materialized = tree.Nodes.OfType<ChanceNode>().Single();
        Assert.AreEqual("Failure occurrence", materialized.Name);
        Assert.IsTrue(materialized.IsFailure);
        Assert.AreEqual(0.25d, response.SampleResponseFunction()[0].Y, 1e-14d);
    }

    /// <summary>Verifies deterministic expanded order and internal/external reference queries.</summary>
    [TestMethod]
    public void Test_TopologyAndReferenceQueries_ExpandIndependentOccurrencesDeterministically()
    {
        // Arrange
        var externalTree = new EventTree();
        var externalNode = new ChanceNode("External", new ProbabilitySource(0.1d));
        externalTree.Add(externalTree.Root.Id, externalNode);
        EventTreeResponse external = Response(externalTree, "External function");
        var tree = new EventTree();
        var local = new ChanceNode("Local", new ProbabilitySource(0.2d));
        tree.Add(tree.Root.Id, local);
        Guid internalLinkId = tree.LinkIndependent(tree.Root.Id, local.Id, "Local reuse");
        Guid externalLinkId = tree.LinkIndependent(tree.Root.Id, external, externalNode.Id,
            "External reuse");
        var remainder = new RemainderNode("Other");
        tree.Add(tree.Root.Id, remainder);
        _ = Response(tree, "Owner");

        // Act
        IReadOnlyList<TreeNodeReference> order = tree.GetTopologicalOrder();

        // Assert
        CollectionAssert.AreEqual(new[]
        {
            tree.Root.Id, local.Id, local.Id, externalNode.Id, remainder.Id,
        }, order.Select(item => item.NodeId).ToArray());
        Assert.IsNull(order[1].FunctionId);
        Assert.AreEqual(external.Id, order[3].FunctionId);
        Assert.AreEqual(internalLinkId, tree.GetInternalReferences().Single().Id);
        Assert.AreEqual(externalLinkId, tree.GetExternalReferences().Single().Id);
        Assert.AreEqual(internalLinkId, tree.GetIncomingReferences(local.Id).Single().Id);
        CollectionAssert.AreEqual(new[] { internalLinkId, externalLinkId },
            tree.GetOutgoingReferences(tree.Root.Id).Select(link => link.Id).ToArray());
        CollectionAssert.AreEqual(order.Select(item => item.NodeId).ToArray(),
            tree.GetTopologicalOrder().Select(item => item.NodeId).ToArray());
    }

    /// <summary>Verifies link targets count as reachable and pruning leaves compute and sampler state intact.</summary>
    [TestMethod]
    public void Test_PruneUnreachable_LinkTargetIsReachableAndOrphanIsRemoved()
    {
        // Arrange
        var authored = new EventTree();
        var target = new ChanceNode("Linked target", new ProbabilitySource(0.2d));
        authored.Add(authored.Root.Id, target);
        authored.LinkIndependent(authored.Root.Id, target.Id, "Only reachable occurrence");
        authored.Add(authored.Root.Id, new RemainderNode("Other"));
        XElement xml = authored.ToXElement();
        XElement targetElement = xml.Element("Nodes")!.Elements(nameof(ChanceNode))
            .Single(element => element.Attribute("Id")!.Value == target.Id.ToString("D"));
        var orphan = new XElement(targetElement);
        Guid orphanId = Guid.NewGuid();
        orphan.SetAttributeValue("Id", orphanId.ToString("D"));
        orphan.SetAttributeValue("Name", "Disconnected orphan");
        orphan.SetAttributeValue("OutputPort", "99");
        xml.Element("Nodes")!.Add(orphan);
        xml.Element("Edges")!.Elements("Edge")
            .Single(edge => edge.Attribute("ChildNodeId")!.Value == target.Id.ToString("D"))
            .Remove();
        var tree = new EventTree(xml);
        EventTreeResponse response = Response(tree);
        response.SetupSampler(8, 12345, SamplingScheme.LatinHypercube);
        double before = response.SampleResponseFunction(0)[0].Y;
        byte[] hashBefore = response.CanonicalHash();

        // Act
        IReadOnlyList<EventNodeBase> unreachable = tree.GetUnreachableNodes();
        IReadOnlyList<Guid> removed = tree.PruneUnreachable();

        // Assert
        CollectionAssert.AreEqual(new[] { orphanId }, unreachable.Select(node => node.Id).ToArray());
        CollectionAssert.AreEqual(new[] { orphanId }, removed.ToArray());
        Assert.IsNotNull(tree.FindById(target.Id));
        Assert.IsNull(tree.FindById(orphanId));
        CollectionAssert.AreEqual(hashBefore, response.CanonicalHash());
        Assert.AreEqual(before, response.SampleResponseFunction(0)[0].Y);
    }

    /// <summary>Verifies a failed cross-function paste restores every public and sampler-observable state.</summary>
    [TestMethod]
    public void Test_FailedPaste_RestoresTopologyPortsIdsHashAndSamplerState()
    {
        // Arrange
        var destinationTree = new EventTree();
        var destinationBranch = new ChanceNode("Destination", new ProbabilitySource(UncertainTable()));
        destinationTree.Add(destinationTree.Root.Id, destinationBranch);
        destinationTree.Add(destinationTree.Root.Id, new RemainderNode("No failure"));
        EventTreeResponse destination = Response(destinationTree, "Destination function");

        var sourceTree = new EventTree();
        var sourceBranch = new ChanceNode("Source", new ProbabilitySource(0.4d)) { IsFailure = false };
        sourceTree.Add(sourceTree.Root.Id, sourceBranch);
        sourceTree.LinkIndependent(sourceBranch.Id, destination, destinationBranch.Id,
            "Back to destination");
        _ = Response(sourceTree, "Source function");
        TreeFragment fragment = sourceTree.Copy(sourceBranch.Id);

        destination.SetupSampler(32, 24680, SamplingScheme.LatinHypercube);
        string xmlBefore = destinationTree.ToXElement().ToString(SaveOptions.DisableFormatting);
        Guid[] idsBefore = destinationTree.Nodes.Select(node => node.Id).ToArray();
        var portsBefore = destination.GetBranches().ToDictionary(branch => branch.Id,
            branch => branch.OutputPort);
        byte[] hashBefore = destination.CanonicalHash();
        double sampleBefore = destination.SampleResponseFunction(0)[0].Y;
        double percentileBefore = destination.SampledPercentile(0, 0);

        // Act
        InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(() =>
            destinationTree.PasteClone(destinationBranch.Id, fragment));

        // Assert
        StringAssert.Contains(exception.Message, "cycle");
        StringAssert.Contains(exception.Message, "PasteClone");
        Assert.AreEqual(xmlBefore,
            destinationTree.ToXElement().ToString(SaveOptions.DisableFormatting));
        CollectionAssert.AreEqual(idsBefore, destinationTree.Nodes.Select(node => node.Id).ToArray());
        CollectionAssert.AreEqual(hashBefore, destination.CanonicalHash());
        foreach (var branch in destination.GetBranches())
            Assert.AreEqual(portsBefore[branch.Id], branch.OutputPort);
        Assert.AreEqual(percentileBefore, destination.SampledPercentile(0, 0));
        Assert.AreEqual(sampleBefore, destination.SampleResponseFunction(0)[0].Y);
    }

    /// <summary>Builds a one-node fragment with a deterministic probability.</summary>
    private static TreeFragment Fragment(double probability)
    {
        var tree = new EventTree();
        var node = new ChanceNode("Replacement", new ProbabilitySource(probability));
        tree.Add(tree.Root.Id, node);
        return tree.Copy(node.Id);
    }

    /// <summary>Builds a labeled response over the common two-knot axis.</summary>
    private static EventTreeResponse Response(EventTree tree, string name = "Event tree")
    {
        return new EventTreeResponse(new[] { 0d, 1d }, tree)
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }

    /// <summary>Builds a valid uncertain probability table for sampler rollback tests.</summary>
    private static UncertainOrderedPairedData UncertainTable()
    {
        return new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Uniform(0.1d, 0.3d)),
                new UncertainOrdinate(1d, new Uniform(0.2d, 0.4d)),
            }, true, SortOrder.Ascending, false, SortOrder.None,
            UnivariateDistributionType.Uniform);
    }
}
