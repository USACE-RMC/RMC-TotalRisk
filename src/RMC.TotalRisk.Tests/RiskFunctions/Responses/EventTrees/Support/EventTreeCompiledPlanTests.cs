using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.EventTrees.Support;

/// <summary>
/// Tests immutable event-tree compiled-plan reuse, dependency invalidation, rollback, numerical
/// parity, and large-tree concurrent read behavior through the internal diagnostic seam.
/// </summary>
[TestClass]
public class EventTreeCompiledPlanTests
{
    /// <summary>Verifies repeated reads reuse one plan while metadata remains compute-inert.</summary>
    [TestMethod]
    public void Test_RepeatedReadsAndMetadataEdits_ReuseOneImmutablePlan()
    {
        var tree = new EventTree();
        var sequence = new ChanceNode("Sequence", new ProbabilitySource(0.7d))
        {
            IsFailure = false,
        };
        var terminal = new ChanceNode("Failure", new ProbabilitySource(0.25d));
        tree.Add(tree.Root.Id, sequence);
        tree.Add(sequence.Id, terminal);
        tree.Add(sequence.Id, new RemainderNode("Sequence survival"));
        tree.Add(tree.Root.Id, new RemainderNode("No sequence"));
        EventTreeResponse response = Response(tree, "Owner");

        object plan = response.CompiledPlanIdentity;
        byte[] hash = response.CanonicalHash();
        _ = response.IsDeterministic;
        _ = response.SamplingDimensions;
        _ = response.SampleResponseFunction();
        _ = response.GetBranches();
        _ = response.ToXElement(RiskSerializationMode.SelfContained);
        _ = tree.GetTopologicalOrder();
        _ = tree.SubtreeCanonicalHash(tree.Root.Id);

        Assert.AreSame(plan, response.CompiledPlanIdentity);
        Assert.AreEqual(1L, response.CompiledPlanBuildCount);
        response.Name = "Renamed owner";
        response.Description = "Owner metadata";
        response.SpecifiedHazard = "Renamed hazard";
        response.HazardUnit = "renamed unit";
        response.AssignNewId();
        tree.Root.Name = "Renamed root";
        sequence.Name = "Renamed sequence";
        sequence.Description = "Sequence metadata";
        sequence.IsFailure = true;
        terminal.Name = "Renamed terminal";
        terminal.Description = "Terminal metadata";

        Assert.AreSame(plan, response.CompiledPlanIdentity);
        Assert.AreEqual(1L, response.CompiledPlanBuildCount);
        CollectionAssert.AreEqual(hash, response.CanonicalHash());
        Assert.IsTrue(response.GetBranches().Any(branch => branch.Name == "Renamed terminal"));

        terminal.IsFailure = false;

        Assert.AreEqual(1L, response.CompiledPlanBuildCount);
        Assert.AreNotSame(plan, response.CompiledPlanIdentity);
        Assert.AreEqual(2L, response.CompiledPlanBuildCount);
    }

    /// <summary>Verifies every controlled structural mutation invalidates after commit.</summary>
    [TestMethod]
    public void Test_AllStructuralMutationCategories_InvalidateExactlyOnceOnNextRead()
    {
        EventTreeResponse add = ScalarResponse();
        AssertMutationInvalidates(add, () => add.EventTree.Add(add.EventTree.Root.Id,
            new ChanceNode("Added", new ProbabilitySource(0.1d))));

        EventTreeResponse insert = ScalarResponse();
        RemainderNode insertRemainder = insert.EventTree.Nodes.OfType<RemainderNode>().Single();
        AssertMutationInvalidates(insert, () => insert.EventTree.Insert(insertRemainder.Id,
            new ChanceNode("Inserted", new ProbabilitySource(0.1d))));

        var moveTree = new EventTree();
        var moveParent = new ChanceNode("Parent", new ProbabilitySource(0.4d)) { IsFailure = false };
        var moveChild = new ChanceNode("Child", new ProbabilitySource(0.2d));
        moveTree.Add(moveTree.Root.Id, moveParent);
        moveTree.Add(moveTree.Root.Id, moveChild);
        moveTree.Add(moveTree.Root.Id, new RemainderNode("Other"));
        EventTreeResponse move = Response(moveTree);
        AssertMutationInvalidates(move, () => moveTree.Move(moveChild.Id, moveParent.Id));

        EventTreeResponse replace = ScalarResponse();
        ChanceNode replaceNode = replace.EventTree.Nodes.OfType<ChanceNode>().Single();
        TreeFragment replacement = Fragment(0.55d);
        AssertMutationInvalidates(replace, () =>
            replace.EventTree.ReplaceSubtree(replaceNode.Id, replacement));

        EventTreeResponse delete = ScalarResponse();
        ChanceNode deleteNode = delete.EventTree.Nodes.OfType<ChanceNode>().Single();
        AssertMutationInvalidates(delete, () => delete.EventTree.Delete(deleteNode.Id));

        var materializeTree = new EventTree();
        var materializeTarget = new ChanceNode("Target", new ProbabilitySource(0.2d));
        materializeTree.Add(materializeTree.Root.Id, materializeTarget);
        Guid linkId = materializeTree.LinkIndependent(materializeTree.Root.Id,
            materializeTarget.Id, "Occurrence");
        materializeTree.Add(materializeTree.Root.Id, new RemainderNode("Other"));
        EventTreeResponse materialize = Response(materializeTree);
        AssertMutationInvalidates(materialize, () => materializeTree.MaterializeLink(linkId));

        EventTreeResponse paste = ScalarResponse();
        ChanceNode pasteNode = paste.EventTree.Nodes.OfType<ChanceNode>().Single();
        TreeFragment pasted = paste.EventTree.Copy(pasteNode.Id);
        AssertMutationInvalidates(paste, () =>
            paste.EventTree.PasteClone(paste.EventTree.Root.Id, pasted));

        EventTreeResponse prune = DisconnectedResponse();
        Assert.AreEqual(1, prune.EventTree.GetUnreachableNodes().Count);
        AssertMutationInvalidates(prune, () => prune.EventTree.PruneUnreachable());
    }

    /// <summary>Verifies source replacement, collection events, and silent table edits cannot go stale.</summary>
    [TestMethod]
    public void Test_ProbabilitySourceAndMutableTableEdits_InvalidateOrFailFingerprintReuse()
    {
        UncertainOrderedPairedData table = DeterministicTable(0.2d, 0.4d);
        var tree = new EventTree();
        var chance = new ChanceNode("Table", new ProbabilitySource(table));
        tree.Add(tree.Root.Id, chance);
        tree.Add(tree.Root.Id, new RemainderNode("Other"));
        EventTreeResponse response = Response(tree);

        object initial = response.CompiledPlanIdentity;
        long builds = response.CompiledPlanBuildCount;
        ((Deterministic)table[0].Y!).Value = 0.3d;
        Assert.AreEqual(builds, response.CompiledPlanBuildCount,
            "A silent in-place distribution edit is found by the defensive fingerprint on read.");
        object afterSilentEdit = response.CompiledPlanIdentity;
        Assert.AreNotSame(initial, afterSilentEdit);
        Assert.AreEqual(builds + 1L, response.CompiledPlanBuildCount);
        Assert.AreEqual(0.3d, response.SampleResponseFunction()[0].Y);

        builds = response.CompiledPlanBuildCount;
        table[0] = new UncertainOrdinate(0d, new Deterministic(0.35d));
        Assert.AreEqual(builds, response.CompiledPlanBuildCount);
        object afterCollectionEdit = response.CompiledPlanIdentity;
        Assert.AreNotSame(afterSilentEdit, afterCollectionEdit);
        Assert.AreEqual(builds + 1L, response.CompiledPlanBuildCount);

        builds = response.CompiledPlanBuildCount;
        chance.ProbabilitySource = new ProbabilitySource(0.45d);
        Assert.AreEqual(builds, response.CompiledPlanBuildCount);
        Assert.AreNotSame(afterCollectionEdit, response.CompiledPlanIdentity);
        Assert.AreEqual(builds + 1L, response.CompiledPlanBuildCount);
        Assert.AreEqual(0.45d, response.SampleResponseFunction()[0].Y);
    }

    /// <summary>Verifies nested, internal-link, and external-link dependencies are instance-scoped.</summary>
    [TestMethod]
    public void Test_NestedAndLinkDependencies_InvalidateAffectedOwnersOnly()
    {
        EventTreeResponse target = ScalarResponse(0.2d, "Target");
        ChanceNode targetNode = target.EventTree.Nodes.OfType<ChanceNode>().Single();
        var ownerTree = new EventTree();
        var nested = new ChanceNode("Nested", new ProbabilitySource(target));
        var local = new ChanceNode("Local", new ProbabilitySource(0.1d));
        ownerTree.Add(ownerTree.Root.Id, nested);
        ownerTree.LinkIndependent(ownerTree.Root.Id, target, targetNode.Id, "External target");
        ownerTree.Add(ownerTree.Root.Id, local);
        ownerTree.LinkIndependent(ownerTree.Root.Id, local.Id, "Internal local");
        ownerTree.Add(ownerTree.Root.Id, new RemainderNode("Other"));
        EventTreeResponse owner = Response(ownerTree, "Owner");
        EventTreeResponse unrelated = ScalarResponse(0.3d, "Unrelated");

        object ownerPlan = owner.CompiledPlanIdentity;
        object unrelatedPlan = unrelated.CompiledPlanIdentity;
        long ownerBuilds = owner.CompiledPlanBuildCount;
        targetNode.ProbabilitySource = new ProbabilitySource(0.25d);

        Assert.AreEqual(ownerBuilds, owner.CompiledPlanBuildCount);
        Assert.AreNotSame(ownerPlan, owner.CompiledPlanIdentity);
        Assert.AreEqual(ownerBuilds + 1L, owner.CompiledPlanBuildCount,
            "One target function used by both a nested source and external link invalidates once.");
        Assert.AreSame(unrelatedPlan, unrelated.CompiledPlanIdentity);
        Assert.AreEqual(1L, unrelated.CompiledPlanBuildCount);

        ownerPlan = owner.CompiledPlanIdentity;
        ownerBuilds = owner.CompiledPlanBuildCount;
        local.ProbabilitySource = new ProbabilitySource(0.15d);
        Assert.AreEqual(ownerBuilds, owner.CompiledPlanBuildCount);
        Assert.AreNotSame(ownerPlan, owner.CompiledPlanIdentity);
        Assert.AreEqual(ownerBuilds + 1L, owner.CompiledPlanBuildCount);
    }

    /// <summary>Verifies both XML modes follow their correct live dependency ownership.</summary>
    [TestMethod]
    public void Test_XmlModesAndResolverBackedLiveReferences_CannotRetainStalePlans()
    {
        EventTreeResponse stored = ScalarResponse(0.2d, "Stored nested");
        var ownerTree = new EventTree();
        ownerTree.Add(ownerTree.Root.Id,
            new ChanceNode("Stored source", new ProbabilitySource(stored)));
        ownerTree.Add(ownerTree.Root.Id, new RemainderNode("Other"));
        EventTreeResponse owner = Response(ownerTree, "Owner");
        XElement selfXml = owner.ToXElement(RiskSerializationMode.SelfContained);
        XElement referenceXml = owner.ToXElement(RiskSerializationMode.ByReference);
        IRiskFunctionResolver resolver = new RiskFunctionResolver(
            id => id == stored.Id ? stored : null,
            name => name == stored.Name ? stored : null);
        var selfContained = new EventTreeResponse(selfXml);
        var byReference = new EventTreeResponse(referenceXml, resolver);
        EventTreeResponse selfNested = (EventTreeResponse)selfContained.EventTree.Nodes
            .OfType<ChanceNode>().Single().ProbabilitySource.ResponseFunction!;

        object selfPlan = selfContained.CompiledPlanIdentity;
        object referencePlan = byReference.CompiledPlanIdentity;
        Assert.AreSame(stored, byReference.EventTree.Nodes.OfType<ChanceNode>().Single()
            .ProbabilitySource.ResponseFunction);
        ChanceNode storedChance = stored.EventTree.Nodes.OfType<ChanceNode>().Single();
        storedChance.ProbabilitySource = new ProbabilitySource(0.4d);

        Assert.AreSame(selfPlan, selfContained.CompiledPlanIdentity);
        Assert.AreNotSame(referencePlan, byReference.CompiledPlanIdentity);
        Assert.AreEqual(0.2d, selfContained.SampleResponseFunction()[0].Y);
        Assert.AreEqual(0.4d, byReference.SampleResponseFunction()[0].Y);

        selfPlan = selfContained.CompiledPlanIdentity;
        selfNested.EventTree.Nodes.OfType<ChanceNode>().Single().ProbabilitySource =
            new ProbabilitySource(0.5d);
        Assert.AreNotSame(selfPlan, selfContained.CompiledPlanIdentity);
        Assert.AreEqual(0.5d, selfContained.SampleResponseFunction()[0].Y);

        var ordinary = new TabularResponse
        {
            Name = "Stored ordinary",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = DeterministicTable(0.15d, 0.35d),
        };
        var ordinaryTree = new EventTree();
        ordinaryTree.Add(ordinaryTree.Root.Id,
            new ChanceNode("Ordinary source", new ProbabilitySource(ordinary)));
        ordinaryTree.Add(ordinaryTree.Root.Id, new RemainderNode("Other"));
        EventTreeResponse ordinaryOwner = Response(ordinaryTree, "Ordinary owner");
        IRiskFunctionResolver ordinaryResolver = new RiskFunctionResolver(
            id => id == ordinary.Id ? ordinary : null,
            name => name == ordinary.Name ? ordinary : null);
        var ordinaryReference = new EventTreeResponse(
            ordinaryOwner.ToXElement(RiskSerializationMode.ByReference), ordinaryResolver);
        object ordinaryPlan = ordinaryReference.CompiledPlanIdentity;

        ordinary.UncertainOrderedPairedData = DeterministicTable(0.3d, 0.5d);

        Assert.AreNotSame(ordinaryPlan, ordinaryReference.CompiledPlanIdentity);
        Assert.AreEqual(0.3d, ordinaryReference.SampleResponseFunction()[0].Y);
    }

    /// <summary>Verifies failed topology compilation restores cache, ports, hash, and sampler state.</summary>
    [TestMethod]
    public void Test_FailedMutation_RestoresExactCachePortsHashAndSamplerState()
    {
        UncertainOrderedPairedData table = UncertainTable();
        var tree = new EventTree();
        tree.Add(tree.Root.Id, new ChanceNode("Failure", new ProbabilitySource(table)));
        tree.Add(tree.Root.Id, new RemainderNode("Other"));
        EventTreeResponse response = Response(tree, "Rollback");
        _ = response.GetBranches();
        response.SetupSampler(16, 24680, SamplingScheme.LatinHypercube);
        object plan = response.CompiledPlanIdentity;
        long builds = response.CompiledPlanBuildCount;
        string xml = response.ToXElement().ToString(SaveOptions.DisableFormatting);
        byte[] hash = response.CanonicalHash();
        var ports = response.GetBranches().ToDictionary(branch => branch.Id,
            branch => branch.OutputPort);
        double percentile = response.SampledPercentile(0, 0);
        double value = response.SampleResponseFunction(0)[0].Y;

        Assert.ThrowsException<InvalidOperationException>(() => tree.Add(tree.Root.Id,
            new ChanceNode("Cycle", new ProbabilitySource(response))));

        Assert.AreSame(plan, response.CompiledPlanIdentity);
        Assert.AreEqual(builds, response.CompiledPlanBuildCount);
        Assert.AreEqual(xml, response.ToXElement().ToString(SaveOptions.DisableFormatting));
        CollectionAssert.AreEqual(hash, response.CanonicalHash());
        foreach (ResponseBranchDescriptor branch in response.GetBranches())
            Assert.AreEqual(ports[branch.Id], branch.OutputPort);
        Assert.AreEqual(16, response.SampleSize);
        Assert.AreEqual(percentile, response.SampledPercentile(0, 0));
        Assert.AreEqual(value, response.SampleResponseFunction(0)[0].Y);
    }

    /// <summary>Verifies cache reuse preserves hashes, seeds, aggregate/per-leaf samples, and ports.</summary>
    [TestMethod]
    public void Test_CachedAndFreshEquivalentResponses_AreBitIdentical()
    {
        var tree = new EventTree();
        tree.Add(tree.Root.Id,
            new ChanceNode("Failure", new ProbabilitySource(UncertainTable())));
        tree.Add(tree.Root.Id, new RemainderNode("Other"));
        EventTreeResponse cached = Response(tree, "Cached");
        var fresh = new EventTreeResponse(
            cached.ToXElement(RiskSerializationMode.SelfContained));
        byte[] hash = cached.CanonicalHash();
        object plan = cached.CompiledPlanIdentity;
        ResponseBranchSample mean = cached.SampleBranches();
        int[] ports = cached.GetBranches().Select(branch => branch.OutputPort).ToArray();

        for (int read = 0; read < 8; read++)
        {
            Assert.AreSame(plan, cached.CompiledPlanIdentity);
            CollectionAssert.AreEqual(hash, cached.CanonicalHash());
            CollectionAssert.AreEqual(Flatten(mean), Flatten(cached.SampleBranches()));
            CollectionAssert.AreEqual(ports,
                cached.GetBranches().Select(branch => branch.OutputPort).ToArray());
        }

        CollectionAssert.AreEqual(hash, fresh.CanonicalHash());
        CollectionAssert.AreEqual(ports,
            fresh.GetBranches().Select(branch => branch.OutputPort).ToArray());
        cached.SetupSampler(32, 97531, SamplingScheme.LatinHypercube);
        fresh.SetupSampler(32, 97531, SamplingScheme.LatinHypercube);
        for (int realization = 0; realization < 32; realization++)
        {
            CollectionAssert.AreEqual(
                Flatten(cached.SampleBranches(realization)),
                Flatten(fresh.SampleBranches(realization)));
            CollectionAssert.AreEqual(
                cached.SampleResponseFunction(realization).Select(item => item.Y).ToArray(),
                fresh.SampleResponseFunction(realization).Select(item => item.Y).ToArray());
        }
    }

    /// <summary>Verifies a large deep/wide tree publishes once under concurrent read-only use.</summary>
    [TestMethod]
    public void Test_LargeDeepWideTree_CompilesOnceAndReadsConcurrently()
    {
        EventTreeResponse response = LargeResponse(128, 64, 33);
        string[] hashes = new string[16];
        string[] branchSamples = new string[16];
        string[] aggregateSamples = new string[16];

        Parallel.For(0, hashes.Length, index =>
        {
            hashes[index] = Convert.ToHexString(response.CanonicalHash());
            branchSamples[index] = Token(Flatten(response.SampleBranches()));
            aggregateSamples[index] = Token(response.SampleResponseFunction()
                .Select(item => item.Y).ToArray());
        });

        Assert.AreEqual(1L, response.CompiledPlanBuildCount);
        Assert.AreEqual(259, response.CompiledInstructionCount);
        Assert.AreEqual(258, response.CompiledEdgeCount);
        Assert.IsTrue(hashes.All(value => value == hashes[0]));
        Assert.IsTrue(branchSamples.All(value => value == branchSamples[0]));
        Assert.IsTrue(aggregateSamples.All(value => value == aggregateSamples[0]));
        Assert.AreSame(response.CompiledPlanIdentity, response.CompiledPlanIdentity);

        UncertainOrderedPairedData table = DeterministicTable(0.2d, 0.4d);
        var staleTree = new EventTree();
        staleTree.Add(staleTree.Root.Id,
            new ChanceNode("Silent table", new ProbabilitySource(table)));
        staleTree.Add(staleTree.Root.Id, new RemainderNode("Other"));
        EventTreeResponse stale = Response(staleTree, "Stale publication race");
        _ = stale.CompiledPlanIdentity;
        ((Deterministic)table[0].Y!).Value = 0.25d;
        var plans = new object[16];

        Parallel.For(0, plans.Length, index =>
            plans[index] = stale.CompiledPlanIdentity);

        Assert.AreEqual(2L, stale.CompiledPlanBuildCount);
        Assert.IsTrue(plans.All(item => ReferenceEquals(item, plans[0])));
        Assert.AreEqual(0.25d, stale.SampleResponseFunction()[0].Y);
    }

    /// <summary>Asserts one committed mutation invalidates lazily and publishes exactly one replacement.</summary>
    private static void AssertMutationInvalidates(EventTreeResponse response, Action mutation)
    {
        object before = response.CompiledPlanIdentity;
        long builds = response.CompiledPlanBuildCount;
        mutation();
        Assert.AreEqual(builds, response.CompiledPlanBuildCount);
        object after = response.CompiledPlanIdentity;
        Assert.AreNotSame(before, after);
        Assert.AreEqual(builds + 1L, response.CompiledPlanBuildCount);
        Assert.AreSame(after, response.CompiledPlanIdentity);
    }

    /// <summary>Builds a deterministic scalar response with an explicit remainder.</summary>
    private static EventTreeResponse ScalarResponse(double probability = 0.2d,
        string name = "Event tree")
    {
        var tree = new EventTree();
        tree.Add(tree.Root.Id,
            new ChanceNode("Failure", new ProbabilitySource(probability)));
        tree.Add(tree.Root.Id, new RemainderNode("Other"));
        return Response(tree, name);
    }

    /// <summary>Builds an unattached one-node replacement fragment.</summary>
    private static TreeFragment Fragment(double probability)
    {
        var tree = new EventTree();
        var branch = new ChanceNode("Replacement", new ProbabilitySource(probability));
        tree.Add(tree.Root.Id, branch);
        return tree.Copy(branch.Id);
    }

    /// <summary>Builds a response containing one serialized unreachable node.</summary>
    private static EventTreeResponse DisconnectedResponse()
    {
        var authored = new EventTree();
        var branch = new ChanceNode("Reachable", new ProbabilitySource(0.2d));
        authored.Add(authored.Root.Id, branch);
        authored.Add(authored.Root.Id, new RemainderNode("Other"));
        XElement xml = authored.ToXElement();
        XElement orphan = new XElement(xml.Element("Nodes")!.Elements(nameof(ChanceNode)).Single());
        orphan.SetAttributeValue("Id", Guid.NewGuid().ToString("D"));
        orphan.SetAttributeValue("Name", "Orphan");
        orphan.SetAttributeValue("OutputPort", "99");
        xml.Element("Nodes")!.Add(orphan);
        return Response(new EventTree(xml));
    }

    /// <summary>Builds a large tree combining one deep chain and many wide root branches.</summary>
    private static EventTreeResponse LargeResponse(int width, int depth, int hazardCount)
    {
        var tree = new EventTree();
        var chain = new ChanceNode("Chain 0", new ProbabilitySource(0.8d))
        {
            IsFailure = false,
        };
        tree.Add(tree.Root.Id, chain);
        EventNodeBase parent = chain;
        for (int level = 1; level <= depth; level++)
        {
            var child = new ChanceNode($"Chain {level}", new ProbabilitySource(0.8d))
            {
                IsFailure = level == depth,
            };
            tree.Add(parent.Id, child);
            tree.Add(parent.Id, new RemainderNode($"Chain remainder {level}"));
            parent = child;
        }
        for (int branch = 0; branch < width; branch++)
        {
            tree.Add(tree.Root.Id, new ChanceNode($"Wide {branch}",
                new ProbabilitySource(0.01d)));
        }
        tree.Add(tree.Root.Id, new RemainderNode("Root remainder"));
        double[] hazards = Enumerable.Range(0, hazardCount)
            .Select(index => index / (double)(hazardCount - 1)).ToArray();
        return new EventTreeResponse(hazards, tree)
        {
            Name = "Large event tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }

    /// <summary>Builds a labeled response over the common two-knot hazard axis.</summary>
    private static EventTreeResponse Response(EventTree tree, string name = "Event tree")
    {
        return new EventTreeResponse(new[] { 0d, 1d }, tree)
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }

    /// <summary>Builds an aligned deterministic table.</summary>
    private static UncertainOrderedPairedData DeterministicTable(double first, double second)
    {
        return new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Deterministic(first)),
                new UncertainOrdinate(1d, new Deterministic(second)),
            }, true, SortOrder.Ascending, false, SortOrder.None,
            UnivariateDistributionType.Deterministic);
    }

    /// <summary>Builds an aligned uncertain table.</summary>
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

    /// <summary>Flattens one immutable branch sample in public branch/hazard order.</summary>
    private static double[] Flatten(ResponseBranchSample sample)
    {
        return sample.Probabilities.SelectMany(row => row).ToArray();
    }

    /// <summary>Builds a culture-invariant exact-bit comparison token.</summary>
    private static string Token(IEnumerable<double> values)
    {
        return string.Join("|", values.Select(value =>
            value.ToString("R", CultureInfo.InvariantCulture)));
    }
}
