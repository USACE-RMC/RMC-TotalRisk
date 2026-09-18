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
/// Tests shared-logical event-tree links: class-unified sampling, identity stamping,
/// mean/percentile invisibility, authoring, serialization unification, and node importance.
/// </summary>
[TestClass]
public class EventTreeSharedLimbTests
{
    /// <summary>Verifies a shared limb draws once per realization at every occurrence.</summary>
    [TestMethod]
    public void Test_SharedLink_SamplesLimbOncePerRealization()
    {
        EventTreeResponse response = SharedPairResponse(TreeLinkMode.SharedLogicalEvent, out _);

        Assert.AreEqual(1, response.SamplingDimensions);
        response.SetupSampler(32, 12345, SamplingScheme.LatinHypercube);
        for (int realization = 0; realization < 32; realization++)
        {
            var sample = response.SampleBranches(realization);
            IReadOnlyList<double> authored = BranchRow(sample, "Limb");
            IReadOnlyList<double> linked = BranchRow(sample, "Limb occurrence");
            for (int h = 0; h < sample.Hazards.Count; h++)
            {
                // Path products 0.5 * p and 0.25 * p over one shared draw p: the halving is an
                // exact power-of-two scaling, so the identity must hold bitwise.
                Assert.AreEqual(authored[h] * 0.5d, linked[h]);
            }
        }
    }

    /// <summary>Verifies an independent link keeps distinct draws for equal limb content.</summary>
    [TestMethod]
    public void Test_IndependentLink_KeepsDistinctDraws()
    {
        EventTreeResponse response = SharedPairResponse(TreeLinkMode.IndependentClone, out _);

        Assert.AreEqual(2, response.SamplingDimensions);
        response.SetupSampler(32, 12345, SamplingScheme.LatinHypercube);
        bool anyDistinct = false;
        for (int realization = 0; realization < 32; realization++)
        {
            var sample = response.SampleBranches(realization);
            IReadOnlyList<double> authored = BranchRow(sample, "Limb");
            IReadOnlyList<double> linked = BranchRow(sample, "Limb occurrence");
            for (int h = 0; h < sample.Hazards.Count; h++)
                anyDistinct |= linked[h] != authored[h] * 0.5d;
        }
        Assert.IsTrue(anyDistinct, "Independent occurrences must fork distinct sampling streams.");
    }

    /// <summary>
    /// Verifies sharing is invisible on the mean and percentile evaluation paths. Bit equality is
    /// the correct expectation for this fixture because every sibling group carries at most two
    /// explicit branches, and a two-term compensated sum is insensitive to the canonical-order
    /// movement the mode attribute causes in wider groups.
    /// </summary>
    [TestMethod]
    public void Test_SharedVsIndependent_MeanAndPercentileCurves_BitEqual()
    {
        EventTreeResponse shared = SharedPairResponse(TreeLinkMode.SharedLogicalEvent, out _);
        EventTreeResponse independent = SharedPairResponse(TreeLinkMode.IndependentClone, out _);

        OrderedPairedData sharedMean = shared.SampleResponseFunction();
        OrderedPairedData independentMean = independent.SampleResponseFunction();
        OrderedPairedData sharedPercentile = shared.SampleResponseFunction(0.31d);
        OrderedPairedData independentPercentile = independent.SampleResponseFunction(0.31d);

        for (int h = 0; h < sharedMean.Count; h++)
        {
            Assert.AreEqual(independentMean[h].Y, sharedMean[h].Y);
            Assert.AreEqual(independentPercentile[h].Y, sharedPercentile[h].Y);
        }
    }

    /// <summary>Verifies selecting the shared mode is a deliberate hash event that restores exactly.</summary>
    [TestMethod]
    public void Test_SharedLinkSelection_MovesCanonicalHash_AndRebuildsExactly()
    {
        string independentHash = Convert.ToHexString(
            SharedPairResponse(TreeLinkMode.IndependentClone, out _).CanonicalHash());
        string sharedHash = Convert.ToHexString(
            SharedPairResponse(TreeLinkMode.SharedLogicalEvent, out _).CanonicalHash());
        string independentAgain = Convert.ToHexString(
            SharedPairResponse(TreeLinkMode.IndependentClone, out _).CanonicalHash());
        string sharedAgain = Convert.ToHexString(
            SharedPairResponse(TreeLinkMode.SharedLogicalEvent, out _).CanonicalHash());

        Assert.AreNotEqual(independentHash, sharedHash);
        Assert.AreEqual(independentHash, independentAgain);
        Assert.AreEqual(sharedHash, sharedAgain);
    }

    /// <summary>Verifies metadata and presentation edits stay hash-inert on a shared tree.</summary>
    [TestMethod]
    public void Test_SharedTree_MetadataAndIds_HashInert()
    {
        EventTreeResponse first = SharedPairResponse(TreeLinkMode.SharedLogicalEvent, out Guid linkId);
        EventTreeResponse second = SharedPairResponse(TreeLinkMode.SharedLogicalEvent, out _);
        second.Name = "Renamed shared tree";
        ((EventTreeLinkNode)second.EventTree.Nodes.OfType<EventTreeLinkNode>().Single()).Name = "Renamed occurrence";
        second.EventTree.Nodes.First(node => node.Name == "Limb").Name = "Renamed limb";

        Assert.AreEqual(Convert.ToHexString(first.CanonicalHash()),
            Convert.ToHexString(second.CanonicalHash()));
        Assert.AreNotEqual(Guid.Empty, linkId);
    }

    /// <summary>Verifies the identity distinguishes sharing groups over equal-content limbs.</summary>
    [TestMethod]
    public void Test_SharedIdentity_DistinguishesSharingGroups()
    {
        string straight = Convert.ToHexString(GroupedSharedResponse(false).CanonicalHash());
        string crossed = Convert.ToHexString(GroupedSharedResponse(true).CanonicalHash());
        string straightAgain = Convert.ToHexString(GroupedSharedResponse(false).CanonicalHash());

        Assert.AreEqual(straight, straightAgain);
        Assert.AreNotEqual(straight, crossed,
            "Equal-content limbs shared in different groups must not collide in identity.");
    }

    /// <summary>Verifies both serialization modes preserve shared identity and sampled values.</summary>
    [TestMethod]
    public void Test_SharedLink_TwoModeRoundTrip_PreservesHashAndSamples()
    {
        EventTreeResponse original = SharedPairResponse(TreeLinkMode.SharedLogicalEvent, out _);
        var selfContained = new EventTreeResponse(original.ToXElement(RiskSerializationMode.SelfContained));
        var byReference = new EventTreeResponse(original.ToXElement(RiskSerializationMode.ByReference));

        Assert.AreEqual(Convert.ToHexString(original.CanonicalHash()),
            Convert.ToHexString(selfContained.CanonicalHash()));
        Assert.AreEqual(Convert.ToHexString(original.CanonicalHash()),
            Convert.ToHexString(byReference.CanonicalHash()));

        original.SetupSampler(16, 45678, SamplingScheme.LatinHypercube);
        selfContained.SetupSampler(16, 45678, SamplingScheme.LatinHypercube);
        for (int realization = 0; realization < 16; realization++)
        {
            OrderedPairedData expected = original.SampleResponseFunction(realization);
            OrderedPairedData actual = selfContained.SampleResponseFunction(realization);
            for (int h = 0; h < expected.Count; h++) Assert.AreEqual(expected[h].Y, actual[h].Y);
        }
    }

    /// <summary>Verifies repeated self-contained embeds of one external target unify on read.</summary>
    [TestMethod]
    public void Test_ExternalSharedLinks_UnifyAfterSelfContainedRoundTrip()
    {
        EventTreeResponse original = ExternalSharedPairResponse();
        Assert.AreEqual(1, original.SamplingDimensions);

        var restored = new EventTreeResponse(original.ToXElement(RiskSerializationMode.SelfContained));
        EventTreeLinkNode[] links = restored.EventTree.Nodes.OfType<EventTreeLinkNode>().ToArray();

        Assert.AreEqual(2, links.Length);
        Assert.IsNotNull(links[0].TargetFunction);
        Assert.AreSame(links[0].TargetFunction, links[1].TargetFunction,
            "Repeated embeds of one external function must materialize as one live instance.");
        Assert.AreEqual(1, restored.SamplingDimensions);
        Assert.AreEqual(Convert.ToHexString(original.CanonicalHash()),
            Convert.ToHexString(restored.CanonicalHash()));

        original.SetupSampler(16, 12345, SamplingScheme.LatinHypercube);
        restored.SetupSampler(16, 12345, SamplingScheme.LatinHypercube);
        for (int realization = 0; realization < 16; realization++)
        {
            OrderedPairedData expected = original.SampleResponseFunction(realization);
            OrderedPairedData actual = restored.SampleResponseFunction(realization);
            for (int h = 0; h < expected.Count; h++) Assert.AreEqual(expected[h].Y, actual[h].Y);
        }
    }

    /// <summary>Verifies divergent repeated embeds of one function id are rejected loudly.</summary>
    [TestMethod]
    public void Test_ExternalSharedLinks_DivergentEmbeds_Throw()
    {
        XElement serialized = ExternalSharedPairResponse().ToXElement(RiskSerializationMode.SelfContained);
        XElement[] embeds = serialized.Descendants("Function")
            .Select(function => function.Elements().First())
            .ToArray();
        Assert.AreEqual(2, embeds.Length);
        embeds[1].SetAttributeValue("Name", "Divergent snapshot");

        var exception = Assert.ThrowsException<InvalidOperationException>(
            () => new EventTreeResponse(serialized));
        StringAssert.Contains(exception.Message, "divergent content");
    }

    /// <summary>Verifies a shared limb containing an independent link evaluates as one deep object.</summary>
    [TestMethod]
    public void Test_NestedIndependentLink_InsideSharedLimb_IsDeepIdentical()
    {
        EventTreeResponse response = NestedForkResponse();

        // (M, root context) and (M, memoized fork context) are the only sampling classes: the
        // interior independent fork is created once and reused by every shared occurrence.
        Assert.AreEqual(2, response.SamplingDimensions);
        response.SetupSampler(24, 12345, SamplingScheme.LatinHypercube);
        for (int realization = 0; realization < 24; realization++)
        {
            var sample = response.SampleBranches(realization);
            AssertHalvingChain(sample, "M");
            AssertHalvingChain(sample, "M clone");

            // The interior fork is deliberately independent of the authored occurrence.
            double authored = BranchRows(sample, "M").Max(row => row[0]);
            double forked = BranchRows(sample, "M clone").Max(row => row[0]);
            if (realization == 0)
                Assert.AreNotEqual(authored, forked, "The interior independent fork must draw its own stream.");
        }
    }

    /// <summary>Verifies the internal and external shared-link authoring overloads.</summary>
    [TestMethod]
    public void Test_LinkShared_AuthoringOverloads_AddSharedLinks()
    {
        var tree = new EventTree();
        var carrier = new ChanceNode("Carrier", new ProbabilitySource(0.5d)) { IsFailure = false };
        tree.Add(tree.Root.Id, carrier);
        var limb = new ChanceNode("Limb", new ProbabilitySource(0.2d));
        tree.Add(carrier.Id, limb);
        var gate = new ChanceNode("Gate", new ProbabilitySource(0.25d)) { IsFailure = false };
        tree.Add(tree.Root.Id, gate);

        Guid internalLinkId = tree.LinkShared(gate.Id, limb.Id, "Shared occurrence");
        var internalLink = (EventTreeLinkNode)tree.FindById(internalLinkId)!;
        Assert.AreEqual(TreeLinkMode.SharedLogicalEvent, internalLink.LinkMode);
        Assert.AreEqual(limb.Id, internalLink.Target.NodeId);

        EventTreeResponse external = SharedPairResponse(TreeLinkMode.SharedLogicalEvent, out _);
        Guid targetNodeId = external.EventTree.Nodes.First(node => node.Name == "Limb").Id;
        Guid externalLinkId = tree.LinkShared(gate.Id, external, targetNodeId, "External shared");
        var externalLink = (EventTreeLinkNode)tree.FindById(externalLinkId)!;
        Assert.AreEqual(TreeLinkMode.SharedLogicalEvent, externalLink.LinkMode);
        Assert.AreSame(external, externalLink.TargetFunction);

        Assert.ThrowsException<InvalidOperationException>(
            () => tree.LinkShared(gate.Id, Guid.NewGuid(), "Missing"));
    }

    /// <summary>Verifies materializing a shared link converts it to independent draws.</summary>
    [TestMethod]
    public void Test_MaterializeSharedLink_ConvertsToIndependentDraws()
    {
        EventTreeResponse response = SharedPairResponse(TreeLinkMode.SharedLogicalEvent, out Guid linkId);
        string sharedHash = Convert.ToHexString(response.CanonicalHash());

        response.EventTree.MaterializeLink(linkId);

        Assert.AreEqual(2, response.SamplingDimensions);
        Assert.AreNotEqual(sharedHash, Convert.ToHexString(response.CanonicalHash()));
        response.SetupSampler(32, 12345, SamplingScheme.LatinHypercube);
        bool anyDistinct = false;
        for (int realization = 0; realization < 32; realization++)
        {
            var sample = response.SampleBranches(realization);
            IReadOnlyList<double> authored = BranchRow(sample, "Limb");
            IReadOnlyList<double> materialized = BranchRow(sample, "Limb occurrence");
            for (int h = 0; h < sample.Hazards.Count; h++)
                anyDistinct |= materialized[h] != authored[h] * 0.5d;
        }
        Assert.IsTrue(anyDistinct, "A materialized limb copy must sample independently.");
    }

    /// <summary>Verifies a pasted shared link keeps its mode and joins the source sharing class.</summary>
    [TestMethod]
    public void Test_PasteClone_SharedLink_PreservesModeAndSharing()
    {
        EventTreeResponse response = SharedPairResponse(TreeLinkMode.SharedLogicalEvent, out _);
        EventTree tree = response.EventTree;
        EventNodeBase gateTwo = tree.Nodes.First(node => node.Name == "Gate two");

        TreeFragment fragment = tree.Copy(gateTwo.Id);
        Guid pastedId = tree.PasteClone(tree.Root.Id, fragment);

        EventTreeLinkNode pastedLink = tree.GetDescendants(pastedId)
            .OfType<EventTreeLinkNode>().Single();
        Assert.AreEqual(TreeLinkMode.SharedLogicalEvent, pastedLink.LinkMode);
        Assert.AreEqual(1, response.SamplingDimensions,
            "A pasted shared occurrence must join the existing sampling class.");
    }

    /// <summary>Verifies a failed shared-link mutation rolls the tree back exactly.</summary>
    [TestMethod]
    public void Test_SharedLink_CyclicTarget_RollsBackExactly()
    {
        EventTreeResponse response = SharedPairResponse(TreeLinkMode.SharedLogicalEvent, out _);
        EventTree tree = response.EventTree;
        string hashBefore = Convert.ToHexString(response.CanonicalHash());
        int nodesBefore = tree.Nodes.Count;
        EventNodeBase gateOne = tree.Nodes.First(node => node.Name == "Gate one");
        EventNodeBase limb = tree.Nodes.First(node => node.Name == "Limb");

        Assert.ThrowsException<InvalidOperationException>(
            () => tree.LinkShared(limb.Id, gateOne.Id, "Cycle"));

        Assert.AreEqual(nodesBefore, tree.Nodes.Count);
        Assert.AreEqual(hashBefore, Convert.ToHexString(response.CanonicalHash()));
    }

    /// <summary>Verifies node importance draws a shared class once and varies it as one quantity.</summary>
    [TestMethod]
    public void Test_TreeNodeImportance_SharedClass_DrawsOnce()
    {
        EventTreeResponse shared = SharedPairResponse(TreeLinkMode.SharedLogicalEvent, out _);
        var options = new TreeNodeImportanceOptions(0d) { Iterations = 200, Seed = 8675309 };

        TreeNodeImportanceResult result = TreeNodeImportance.Compute(shared, options);
        TreeNodeImportanceEntry authored = result.Entries.Single(entry => entry.Name == "Limb");
        TreeNodeImportanceEntry linked = result.Entries.Single(entry => entry.Name == "Limb occurrence");

        Assert.IsTrue(authored.HasUncertainty);
        Assert.IsTrue(linked.HasUncertainty);
        for (int i = 0; i < authored.ProbabilitySummary.Count; i++)
        {
            // One draw per class: path probabilities 0.5 * p and 0.25 * p halve exactly, so
            // every order statistic halves exactly too.
            Assert.AreEqual(authored.ProbabilitySummary[i] * 0.5d, linked.ProbabilitySummary[i]);
        }
        Assert.AreEqual(authored.FirstOrderIndex, linked.FirstOrderIndex,
            "The one-at-a-time pass must vary a shared class as one knowledge quantity.");
        Assert.IsTrue(authored.FirstOrderIndex > 0d);

        EventTreeResponse independent = SharedPairResponse(TreeLinkMode.IndependentClone, out _);
        TreeNodeImportanceResult independentResult = TreeNodeImportance.Compute(independent, options);
        TreeNodeImportanceEntry independentAuthored = independentResult.Entries.Single(entry => entry.Name == "Limb");
        TreeNodeImportanceEntry independentLinked = independentResult.Entries.Single(entry => entry.Name == "Limb occurrence");
        bool anyDistinct = false;
        for (int i = 0; i < independentAuthored.ProbabilitySummary.Count; i++)
            anyDistinct |= independentLinked.ProbabilitySummary[i] != independentAuthored.ProbabilitySummary[i] * 0.5d;
        Assert.IsTrue(anyDistinct, "Independent occurrences must keep distinct importance draws.");
    }

    /// <summary>Asserts every same-named leaf forms an exact power-of-two halving chain.</summary>
    /// <param name="sample">The exhaustive branch sample.</param>
    /// <param name="name">The repeated leaf display name.</param>
    private static void AssertHalvingChain(ResponseBranchSample sample, string name)
    {
        foreach (int h in Enumerable.Range(0, sample.Hazards.Count))
        {
            double[] values = BranchRows(sample, name)
                .Select(row => row[h])
                .OrderByDescending(value => value)
                .ToArray();
            Assert.AreEqual(3, values.Length);
            Assert.AreEqual(values[0] * 0.5d, values[1]);
            Assert.AreEqual(values[1] * 0.5d, values[2]);
        }
    }

    /// <summary>Gets the single branch probability row with one display name.</summary>
    /// <param name="sample">The exhaustive branch sample.</param>
    /// <param name="name">The branch display name.</param>
    /// <returns>The probability row.</returns>
    private static IReadOnlyList<double> BranchRow(ResponseBranchSample sample, string name)
    {
        return BranchRows(sample, name).Single();
    }

    /// <summary>Gets every branch probability row with one display name.</summary>
    /// <param name="sample">The exhaustive branch sample.</param>
    /// <param name="name">The branch display name.</param>
    /// <returns>The probability rows.</returns>
    private static IReadOnlyList<double>[] BranchRows(ResponseBranchSample sample, string name)
    {
        return Enumerable.Range(0, sample.Branches.Count)
            .Where(i => sample.Branches[i].Name == name)
            .Select(i => sample.Probabilities[i])
            .ToArray();
    }

    /// <summary>Builds the canonical shared-pair fixture: an uncertain limb reused via one link.</summary>
    /// <param name="mode">The link mode under test.</param>
    /// <param name="linkId">The added link node id.</param>
    /// <returns>The configured response.</returns>
    private static EventTreeResponse SharedPairResponse(TreeLinkMode mode, out Guid linkId)
    {
        var tree = new EventTree();
        var gateOne = new ChanceNode("Gate one", new ProbabilitySource(0.5d)) { IsFailure = false };
        var gateTwo = new ChanceNode("Gate two", new ProbabilitySource(0.25d)) { IsFailure = false };
        tree.Add(tree.Root.Id, gateOne);
        tree.Add(tree.Root.Id, gateTwo);
        tree.Add(tree.Root.Id, new RemainderNode("Root remainder"));
        var limb = new ChanceNode("Limb", new ProbabilitySource(UncertainTable()));
        tree.Add(gateOne.Id, limb);
        tree.Add(gateOne.Id, new RemainderNode("Gate one remainder"));
        linkId = mode == TreeLinkMode.SharedLogicalEvent
            ? tree.LinkShared(gateTwo.Id, limb.Id, "Limb occurrence")
            : tree.LinkIndependent(gateTwo.Id, limb.Id, "Limb occurrence");
        tree.Add(gateTwo.Id, new RemainderNode("Gate two remainder"));
        return ValidResponse(tree, "Shared limb tree");
    }

    /// <summary>Builds two equal-content limbs each reused by two shared links.</summary>
    /// <param name="crossed">Whether the second and third links swap their targets.</param>
    /// <returns>The configured response.</returns>
    private static EventTreeResponse GroupedSharedResponse(bool crossed)
    {
        var tree = new EventTree();
        var carrierX = new ChanceNode("Carrier X", new ProbabilitySource(0.05d)) { IsFailure = false };
        var carrierY = new ChanceNode("Carrier Y", new ProbabilitySource(0.05d)) { IsFailure = false };
        tree.Add(tree.Root.Id, carrierX);
        tree.Add(tree.Root.Id, carrierY);
        var limbX = new ChanceNode("Limb X", new ProbabilitySource(UncertainTable()));
        var limbY = new ChanceNode("Limb Y", new ProbabilitySource(UncertainTable()));
        tree.Add(carrierX.Id, limbX);
        tree.Add(carrierY.Id, limbY);

        double[] gateProbabilities = { 0.1d, 0.15d, 0.2d, 0.25d };
        Guid[] straightTargets = { limbX.Id, limbX.Id, limbY.Id, limbY.Id };
        Guid[] crossedTargets = { limbX.Id, limbY.Id, limbX.Id, limbY.Id };
        Guid[] targets = crossed ? crossedTargets : straightTargets;
        for (int i = 0; i < targets.Length; i++)
        {
            var gate = new ChanceNode($"Gate {i}", new ProbabilitySource(gateProbabilities[i])) { IsFailure = false };
            tree.Add(tree.Root.Id, gate);
            tree.LinkShared(gate.Id, targets[i], "Group occurrence");
        }
        tree.Add(tree.Root.Id, new RemainderNode("Root remainder"));
        return ValidResponse(tree, "Grouped shared tree");
    }

    /// <summary>Builds an owner whose two shared links target one external function's limb.</summary>
    /// <returns>The configured owner response.</returns>
    private static EventTreeResponse ExternalSharedPairResponse()
    {
        var targetTree = new EventTree();
        var carrier = new ChanceNode("Carrier", new ProbabilitySource(0.5d)) { IsFailure = false };
        targetTree.Add(targetTree.Root.Id, carrier);
        var limb = new ChanceNode("Stored limb", new ProbabilitySource(UncertainTable()));
        targetTree.Add(carrier.Id, limb);
        targetTree.Add(targetTree.Root.Id, new RemainderNode("Target remainder"));
        EventTreeResponse target = ValidResponse(targetTree, "Stored tree");

        var ownerTree = new EventTree();
        var gateOne = new ChanceNode("Gate one", new ProbabilitySource(0.5d)) { IsFailure = false };
        var gateTwo = new ChanceNode("Gate two", new ProbabilitySource(0.25d)) { IsFailure = false };
        ownerTree.Add(ownerTree.Root.Id, gateOne);
        ownerTree.Add(ownerTree.Root.Id, gateTwo);
        ownerTree.LinkShared(gateOne.Id, target, limb.Id, "First occurrence");
        ownerTree.LinkShared(gateTwo.Id, target, limb.Id, "Second occurrence");
        ownerTree.Add(ownerTree.Root.Id, new RemainderNode("Owner remainder"));
        return ValidResponse(ownerTree, "Owner tree");
    }

    /// <summary>Builds a shared limb whose interior contains a deliberate independent fork.</summary>
    /// <returns>The configured response.</returns>
    private static EventTreeResponse NestedForkResponse()
    {
        var tree = new EventTree();
        var carrier = new ChanceNode("Carrier", new ProbabilitySource(0.5d)) { IsFailure = false };
        tree.Add(tree.Root.Id, carrier);
        var limbRoot = new ChanceNode("A", new ProbabilitySource(0.75d)) { IsFailure = false };
        tree.Add(carrier.Id, limbRoot);
        var interior = new ChanceNode("M", new ProbabilitySource(NarrowUncertainTable()));
        tree.Add(limbRoot.Id, interior);
        tree.LinkIndependent(limbRoot.Id, interior.Id, "M clone");
        tree.Add(limbRoot.Id, new RemainderNode("Limb remainder"));

        var gateOne = new ChanceNode("Gate one", new ProbabilitySource(0.25d)) { IsFailure = false };
        var gateTwo = new ChanceNode("Gate two", new ProbabilitySource(0.125d)) { IsFailure = false };
        tree.Add(tree.Root.Id, gateOne);
        tree.Add(tree.Root.Id, gateTwo);
        tree.LinkShared(gateOne.Id, limbRoot.Id, "A occurrence one");
        tree.LinkShared(gateTwo.Id, limbRoot.Id, "A occurrence two");
        tree.Add(tree.Root.Id, new RemainderNode("Root remainder"));
        return ValidResponse(tree, "Nested fork tree");
    }

    /// <summary>Builds the standard two-level uncertain limb table.</summary>
    /// <returns>The table.</returns>
    private static UncertainOrderedPairedData UncertainTable()
    {
        return new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Uniform(0.1d, 0.3d)),
                new UncertainOrdinate(1d, new Uniform(0.2d, 0.6d)),
            }, true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Uniform);
    }

    /// <summary>Builds a narrow uncertain table whose sibling sums never reach one.</summary>
    /// <returns>The table.</returns>
    private static UncertainOrderedPairedData NarrowUncertainTable()
    {
        return new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Uniform(0.05d, 0.15d)),
                new UncertainOrdinate(1d, new Uniform(0.1d, 0.3d)),
            }, true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Uniform);
    }

    /// <summary>Builds a labeled valid response over hazards zero and one.</summary>
    /// <param name="tree">The authored tree.</param>
    /// <param name="name">The response name.</param>
    /// <returns>The response.</returns>
    private static EventTreeResponse ValidResponse(EventTree tree, string name)
    {
        return new EventTreeResponse(new[] { 0d, 1d }, tree)
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }
}
