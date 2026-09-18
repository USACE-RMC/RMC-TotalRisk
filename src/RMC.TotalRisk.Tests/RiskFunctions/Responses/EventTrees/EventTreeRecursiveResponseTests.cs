using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using Numerics.Mathematics.Optimization;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.EventTrees;

/// <summary>
/// Tests recursive EventTreeResponse probability sources, occurrence sampling, mixed cycles,
/// two-mode persistence, canonical identity, and transactional sampler setup.
/// </summary>
[TestClass]
public class EventTreeRecursiveResponseTests
{
    /// <summary>Verifies direct and multi-level sources interpolate at the caller's current hazard.</summary>
    [TestMethod]
    public void Test_NestedSources_DirectAndMultiLevelEvaluateAtCallerHazards()
    {
        var bottomTree = new EventTree();
        bottomTree.Add(bottomTree.Root.Id, new ChanceNode("Bottom failure",
            new ProbabilitySource(DeterministicTable(
                new[] { 0d, 2d }, new[] { 0.2d, 0.6d }))));
        bottomTree.Add(bottomTree.Root.Id, new RemainderNode("Bottom survival"));
        EventTreeResponse bottom = Response(new[] { 0d, 2d }, bottomTree, "Bottom");
        EventTreeResponse direct = SingleSourceResponse(
            new[] { 0d, 1d, 2d }, bottom, "Direct");
        EventTreeResponse middle = SingleSourceResponse(
            new[] { 0d, 2d }, bottom, "Middle");
        EventTreeResponse multiLevel = SingleSourceResponse(
            new[] { 0d, 1d, 2d }, middle, "Outer");

        OrderedPairedData directCurve = direct.SampleResponseFunction();
        OrderedPairedData multiCurve = multiLevel.SampleResponseFunction();

        double[] expected =
        {
            0.2d,
            NormalZInterpolate(0.2d, 0.6d, 0.5d),
            0.6d,
        };
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(expected[i], directCurve[i].Y, 1e-14d);
            Assert.AreEqual(expected[i], multiCurve[i].Y, 1e-14d);
        }
    }

    /// <summary>Verifies nested percentile and indexed samples preserve the deepest source draw.</summary>
    [TestMethod]
    public void Test_NestedSources_PercentileAndIndexedSamplesMatchDeepestTable()
    {
        UncertainOrderedPairedData table = UncertainTable(
            new[] { 0d, 2d }, new[] { 0.1d, 0.3d }, new[] { 0.3d, 0.7d });
        var bottomTree = new EventTree();
        bottomTree.Add(bottomTree.Root.Id,
            new ChanceNode("Bottom failure", new ProbabilitySource(table)));
        bottomTree.Add(bottomTree.Root.Id, new RemainderNode("Bottom survival"));
        EventTreeResponse bottom = Response(new[] { 0d, 2d }, bottomTree, "Bottom");
        EventTreeResponse middle = SingleSourceResponse(
            new[] { 0d, 2d }, bottom, "Middle");

        var outerTree = new EventTree();
        var load = new ChanceNode("Load case", new ProbabilitySource(0.6d))
        {
            IsFailure = false,
        };
        outerTree.Add(outerTree.Root.Id, load);
        outerTree.Add(outerTree.Root.Id, new RemainderNode("No load"));
        outerTree.Add(load.Id,
            new ChanceNode("Nested failure", new ProbabilitySource(middle)));
        outerTree.Add(load.Id, new RemainderNode("Loaded survival"));
        EventTreeResponse outer = Response(
            new[] { 0d, 1d, 2d }, outerTree, "Outer");

        const double percentile = 0.25d;
        OrderedPairedData expectedPercentile = table.CurveSample(percentile);
        OrderedPairedData actualPercentile = outer.SampleResponseFunction(percentile);
        for (int h = 0; h < outer.HazardLevels.Count; h++)
        {
            Assert.AreEqual(0.6d * InterpolateResponseCurve(
                    expectedPercentile, outer.HazardLevels[h]),
                actualPercentile[h].Y, 1e-14d);
        }

        outer.SetupSampler(64, 54321, SamplingScheme.LatinHypercube);

        Assert.AreEqual(1, outer.SamplingDimensions);
        for (int realization = 0; realization < 64; realization++)
        {
            double sampledPercentile = outer.SampledPercentile(realization, 0);
            OrderedPairedData deepest = table.CurveSample(sampledPercentile);
            OrderedPairedData actual = outer.SampleResponseFunction(realization);
            for (int h = 0; h < outer.HazardLevels.Count; h++)
            {
                Assert.AreEqual(0.6d * InterpolateResponseCurve(
                        deepest, outer.HazardLevels[h]),
                    actual[h].Y, 1e-14d);
            }
        }
    }

    /// <summary>
    /// Verifies exact recursive dimensions through local tables, ordinary responses, nested
    /// responses, and internal/external independent links.
    /// </summary>
    [TestMethod]
    public void Test_SamplingDimensions_RecursiveLinksAndOrdinaryResponsesAreExact()
    {
        var ordinary = new TabularResponse
        {
            Name = "Ordinary",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = UncertainTable(
                new[] { 0d, 2d }, new[] { 0.05d, 0.1d }, new[] { 0.15d, 0.2d }),
        };
        var deepTree = new EventTree();
        var tableNode = new ChanceNode("Deep table", new ProbabilitySource(
            UncertainTable(new[] { 0d, 2d },
                new[] { 0.02d, 0.04d }, new[] { 0.08d, 0.12d })));
        var ordinaryNode = new ChanceNode("Deep ordinary", new ProbabilitySource(ordinary));
        deepTree.Add(deepTree.Root.Id, tableNode);
        deepTree.LinkIndependent(deepTree.Root.Id, tableNode.Id, "Deep table reuse");
        deepTree.Add(deepTree.Root.Id, ordinaryNode);
        deepTree.Add(deepTree.Root.Id, new RemainderNode("Deep survival"));
        EventTreeResponse deep = Response(new[] { 0d, 2d }, deepTree, "Deep");

        var middleTree = new EventTree();
        var nestedNode = new ChanceNode("Nested deep", new ProbabilitySource(deep));
        middleTree.Add(middleTree.Root.Id, nestedNode);
        middleTree.LinkIndependent(middleTree.Root.Id, deep, ordinaryNode.Id,
            "External ordinary");
        middleTree.Add(middleTree.Root.Id, new RemainderNode("Middle survival"));
        EventTreeResponse middle = Response(new[] { 0d, 2d }, middleTree, "Middle");

        var outerTree = new EventTree();
        var middleNode = new ChanceNode("Nested middle", new ProbabilitySource(middle));
        outerTree.Add(outerTree.Root.Id, middleNode);
        outerTree.LinkIndependent(outerTree.Root.Id, middleNode.Id, "Nested middle reuse");
        outerTree.LinkIndependent(outerTree.Root.Id, deep, tableNode.Id,
            "External deep table");
        outerTree.Add(outerTree.Root.Id,
            new ChanceNode("Outer ordinary", new ProbabilitySource(ordinary)));
        outerTree.Add(outerTree.Root.Id, new RemainderNode("Outer survival"));
        EventTreeResponse outer = Response(new[] { 0d, 2d }, outerTree, "Outer");

        outer.SetupSampler(16, 13579, SamplingScheme.LatinHypercube);
        var branches = outer.SampleBranches(0);

        Assert.AreEqual(3, deep.SamplingDimensions);
        Assert.AreEqual(4, middle.SamplingDimensions);
        Assert.AreEqual(10, outer.SamplingDimensions);
        Assert.AreEqual(1d, branches.Probabilities.Sum(row => row[0]), 1e-12d);
        Assert.AreEqual(0, ordinary.SampleSize,
            "Recursive setup must not mutate the live ordinary response.");
    }

    /// <summary>Verifies repeated references to one live nested tree own distinct replayable streams.</summary>
    [TestMethod]
    public void Test_RepeatedNestedSources_AreIndependentAndPreserveLiveSampler()
    {
        UncertainOrderedPairedData table = UncertainTable(
            new[] { 0d, 1d }, new[] { 0.02d, 0.04d }, new[] { 0.12d, 0.18d });
        var nestedTree = new EventTree();
        nestedTree.Add(nestedTree.Root.Id,
            new ChanceNode("Nested failure", new ProbabilitySource(table)));
        nestedTree.Add(nestedTree.Root.Id, new RemainderNode("Nested survival"));
        EventTreeResponse nested = Response(new[] { 0d, 1d }, nestedTree, "Stored nested");
        nested.SetupSampler(8, 11111, SamplingScheme.LatinHypercube);
        double livePercentile = nested.SampledPercentile(0, 0);
        double liveValue = nested.SampleResponseFunction(0)[0].Y;

        var tree = new EventTree();
        tree.Add(tree.Root.Id,
            new ChanceNode("Occurrence A", new ProbabilitySource(nested)));
        tree.Add(tree.Root.Id,
            new ChanceNode("Occurrence B", new ProbabilitySource(nested)));
        tree.Add(tree.Root.Id, new RemainderNode("Owner survival"));
        EventTreeResponse original = Response(new[] { 0d, 1d }, tree, "Owner");
        var replay = new EventTreeResponse(
            original.ToXElement(RiskSerializationMode.SelfContained));

        original.SetupSampler(64, 22222, SamplingScheme.LatinHypercube);
        replay.SetupSampler(64, 22222, SamplingScheme.LatinHypercube);

        Assert.AreEqual(2, original.SamplingDimensions);
        Assert.AreEqual(8, nested.SampleSize);
        Assert.AreEqual(livePercentile, nested.SampledPercentile(0, 0));
        Assert.AreEqual(liveValue, nested.SampleResponseFunction(0)[0].Y);
        bool distinct = false;
        for (int realization = 0; realization < 64; realization++)
        {
            var actual = original.SampleBranches(realization);
            var repeated = replay.SampleBranches(realization);
            int a = BranchIndex(actual, "Occurrence A");
            int b = BranchIndex(actual, "Occurrence B");
            int replayA = BranchIndex(repeated, "Occurrence A");
            int replayB = BranchIndex(repeated, "Occurrence B");
            distinct |= actual.Probabilities[a][0] != actual.Probabilities[b][0];
            Assert.AreEqual(actual.Probabilities[a][0], repeated.Probabilities[replayA][0]);
            Assert.AreEqual(actual.Probabilities[b][0], repeated.Probabilities[replayB][0]);
        }
        Assert.IsTrue(distinct);
    }

    /// <summary>Verifies direct, indirect, and mixed source/link cycles report complete paths.</summary>
    [TestMethod]
    public void Test_RecursiveCycles_ReportCompleteFunctionNodePathsAndRollBack()
    {
        var directTree = new EventTree();
        EventTreeResponse direct = Response(new[] { 0d, 1d }, directTree, "Direct");
        InvalidOperationException directError = Assert.ThrowsException<InvalidOperationException>(() =>
            directTree.Add(directTree.Root.Id,
                new ChanceNode("Self source", new ProbabilitySource(direct))));

        StringAssert.Contains(directError.Message, "Cross-function event-tree cycle detected");
        StringAssert.Contains(directError.Message, "Direct");
        StringAssert.Contains(directError.Message, "Self source");
        Assert.AreEqual(1, directTree.Nodes.Count);

        var treeA = new EventTree();
        var branchA = new ChanceNode("A scalar", new ProbabilitySource(0.1d));
        treeA.Add(treeA.Root.Id, branchA);
        EventTreeResponse responseA = Response(new[] { 0d, 1d }, treeA, "Function A");
        var treeB = new EventTree();
        var branchB = new ChanceNode("B to A", new ProbabilitySource(responseA));
        treeB.Add(treeB.Root.Id, branchB);
        EventTreeResponse responseB = Response(new[] { 0d, 1d }, treeB, "Function B");
        int countBefore = treeA.Nodes.Count;

        InvalidOperationException indirect = Assert.ThrowsException<InvalidOperationException>(() =>
            treeA.Add(treeA.Root.Id,
                new ChanceNode("A to B", new ProbabilitySource(responseB))));
        StringAssert.Contains(indirect.Message, "Function A");
        StringAssert.Contains(indirect.Message, "A to B");
        StringAssert.Contains(indirect.Message, "Function B");
        StringAssert.Contains(indirect.Message, "B to A");
        Assert.AreEqual(countBefore, treeA.Nodes.Count);

        InvalidOperationException mixed = Assert.ThrowsException<InvalidOperationException>(() =>
            treeA.LinkIndependent(treeA.Root.Id, responseB, branchB.Id, "A link to B"));
        StringAssert.Contains(mixed.Message, "A link to B");
        StringAssert.Contains(mixed.Message, "B to A");
        StringAssert.Contains(mixed.Message, "Function A");
        StringAssert.Contains(mixed.Message, "Function B");
        Assert.AreEqual(countBefore, treeA.Nodes.Count);
    }

    /// <summary>Verifies nested sources round-trip in both modes and repair name fallbacks.</summary>
    [TestMethod]
    public void Test_NestedSource_SerializationModesAndReferenceRepairPreserveIdentity()
    {
        EventTreeResponse nested = NestedInvariantResponse(false, out _);
        nested.Name = "Stored nested";
        EventTreeResponse original = SingleSourceResponse(
            new[] { 0d, 1d }, nested, "Owner");
        XElement selfXml = original.ToXElement(RiskSerializationMode.SelfContained);
        XElement referenceXml = original.ToXElement(RiskSerializationMode.ByReference);
        referenceXml.Descendants("FunctionReference").Single().Attribute("Id")!.Remove();
        IRiskFunctionResolver resolver = new RiskFunctionResolver(
            id => id == nested.Id ? nested : null,
            name => name == nested.Name ? nested : null);

        var unresolved = new EventTreeResponse(referenceXml);
        var selfContained = new EventTreeResponse(selfXml);
        var byReference = new EventTreeResponse(referenceXml, resolver);
        XElement repaired = byReference.ToXElement(RiskSerializationMode.ByReference);

        Assert.IsFalse(unresolved.Validate().IsValid);
        Assert.IsTrue(unresolved.Validate().ValidationMessages.Any(message =>
            message.Contains("which was not found", StringComparison.Ordinal)));
        Assert.AreSame(nested,
            byReference.EventTree.Nodes.OfType<ChanceNode>().Single()
                .ProbabilitySource.ResponseFunction);
        Assert.AreEqual(nested.Id.ToString("D"),
            repaired.Descendants("FunctionReference").Single().Attribute("Id")!.Value);
        CollectionAssert.AreEqual(original.CanonicalHash(), selfContained.CanonicalHash());
        CollectionAssert.AreEqual(original.CanonicalHash(), byReference.CanonicalHash());
        Assert.AreEqual(original.SampleResponseFunction()[0].Y,
            selfContained.SampleResponseFunction()[0].Y);
        Assert.AreEqual(original.SampleResponseFunction()[0].Y,
            byReference.SampleResponseFunction()[0].Y);
    }

    /// <summary>Verifies nested metadata, ids, order, and XML wrappers are hash/seed inert.</summary>
    [TestMethod]
    public void Test_NestedSource_HashAndSeedInvariantToMetadataOrderGuidAndMode()
    {
        EventTreeResponse first = NestedInvariantResponse(false, out EventTreeResponse firstNested);
        EventTreeResponse second = NestedInvariantResponse(true, out EventTreeResponse secondNested);
        second.Name = "Renamed outer";
        second.Description = "Outer metadata";
        second.AssignNewId();
        secondNested.Name = "Renamed nested";
        secondNested.Description = "Nested metadata";
        secondNested.AssignNewId();
        foreach (EventNodeBase node in second.EventTree.Nodes.Concat(secondNested.EventTree.Nodes))
        {
            node.Name = $"Renamed {node.Name}";
            node.Description = "Node metadata";
        }
        IRiskFunctionResolver resolver = new RiskFunctionResolver(
            id => id == firstNested.Id ? firstNested : null,
            name => name == firstNested.Name ? firstNested : null);
        var selfContained = new EventTreeResponse(
            first.ToXElement(RiskSerializationMode.SelfContained));
        var byReference = new EventTreeResponse(
            first.ToXElement(RiskSerializationMode.ByReference), resolver);

        CollectionAssert.AreEqual(first.CanonicalHash(), second.CanonicalHash());
        CollectionAssert.AreEqual(first.CanonicalHash(), selfContained.CanonicalHash());
        CollectionAssert.AreEqual(first.CanonicalHash(), byReference.CanonicalHash());
        first.SetupSampler(32, 97531, SamplingScheme.LatinHypercube);
        second.SetupSampler(32, 97531, SamplingScheme.LatinHypercube);
        selfContained.SetupSampler(32, 97531, SamplingScheme.LatinHypercube);
        byReference.SetupSampler(32, 97531, SamplingScheme.LatinHypercube);
        for (int realization = 0; realization < 32; realization++)
        {
            Assert.AreEqual(first.SampleResponseFunction(realization)[0].Y,
                second.SampleResponseFunction(realization)[0].Y);
            Assert.AreEqual(first.SampleResponseFunction(realization)[0].Y,
                selfContained.SampleResponseFunction(realization)[0].Y);
            Assert.AreEqual(first.SampleResponseFunction(realization)[0].Y,
                byReference.SampleResponseFunction(realization)[0].Y);
        }

        byte[] baseline = first.CanonicalHash();
        firstNested.EventTree.Nodes.OfType<ChanceNode>().First()
            .ProbabilitySource = new ProbabilitySource(0.01d);
        CollectionAssert.AreNotEqual(baseline, first.CanonicalHash());
    }

    /// <summary>Verifies failed capacity setup and recursive compilation preserve prior sampling.</summary>
    [TestMethod]
    public void Test_FailedRecursiveSetupAndCompilation_PreserveExactSamplerState()
    {
        var shallow = new ParametricResponse
        {
            Name = "Shallow posterior",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            ParentDistribution = new Normal(0.5d, 0.2d),
        };
        var sets = new List<ParameterSet>();
        for (int i = 0; i < 4; i++)
            sets.Add(new ParameterSet(new[] { 0.45d + 0.03d * i, 0.2d }, 0d));
        shallow.Estimate(sets);
        var composite = new CompositeResponse(new[]
        {
            new WeightedResponseFunction(shallow, 1d),
        })
        {
            Name = "Capacity-limited composite",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            CompositeCombinationType = CompositeCombinationType.Mixture,
        };
        EventTreeResponse nested = SingleSourceResponse(
            new[] { 0d, 1d }, composite, "Nested capacity");
        var outerTree = new EventTree();
        var local = new ChanceNode("Local uncertainty", new ProbabilitySource(
            UncertainTable(new[] { 0d, 1d },
                new[] { 0.02d, 0.04d }, new[] { 0.08d, 0.12d })));
        outerTree.Add(outerTree.Root.Id, local);
        outerTree.Add(outerTree.Root.Id,
            new ChanceNode("Nested capacity source", new ProbabilitySource(nested)));
        outerTree.Add(outerTree.Root.Id, new RemainderNode("Outer survival"));
        EventTreeResponse outer = Response(new[] { 0d, 1d }, outerTree, "Outer rollback");
        outer.SetupSampler(4, 86420, SamplingScheme.LatinHypercube);
        double percentileBefore = outer.SampledPercentile(0, 0);
        double valueBefore = outer.SampleResponseFunction(0)[0].Y;
        object planBefore = outer.CompiledPlanIdentity;
        long planBuildsBefore = outer.CompiledPlanBuildCount;

        InvalidOperationException capacity = Assert.ThrowsException<InvalidOperationException>(() =>
            outer.SetupSampler(5, 86420, SamplingScheme.LatinHypercube));

        StringAssert.Contains(capacity.Message, "Shallow posterior");
        StringAssert.Contains(capacity.Message, "posterior of only 4");
        StringAssert.Contains(capacity.Message, "canonical path");
        Assert.AreEqual(4, outer.SampleSize);
        Assert.AreEqual(percentileBefore, outer.SampledPercentile(0, 0));
        Assert.AreEqual(valueBefore, outer.SampleResponseFunction(0)[0].Y);
        Assert.AreSame(planBefore, outer.CompiledPlanIdentity);
        Assert.AreEqual(planBuildsBefore, outer.CompiledPlanBuildCount);

        ProbabilitySource originalSource = local.ProbabilitySource;
        local.ProbabilitySource = new ProbabilitySource(outer);
        InvalidOperationException cycle = Assert.ThrowsException<InvalidOperationException>(() =>
            outer.SetupSampler(4, 86420, SamplingScheme.LatinHypercube));
        StringAssert.Contains(cycle.Message, "Cross-function event-tree cycle detected");
        local.ProbabilitySource = originalSource;

        Assert.AreEqual(4, outer.SampleSize);
        Assert.AreEqual(percentileBefore, outer.SampledPercentile(0, 0));
        Assert.AreEqual(valueBefore, outer.SampleResponseFunction(0)[0].Y);
    }

    /// <summary>Returns the branch index with the requested display name.</summary>
    private static int BranchIndex(
        RMC.TotalRisk.RiskFunctions.Responses.Trees.ResponseBranchSample sample,
        string name)
    {
        return Enumerable.Range(0, sample.Branches.Count)
            .Single(i => sample.Branches[i].Name == name);
    }

    /// <summary>Builds a single nested response source plus an explicit residual branch.</summary>
    private static EventTreeResponse SingleSourceResponse(
        double[] hazards, IResponseFunction source, string name)
    {
        var tree = new EventTree();
        tree.Add(tree.Root.Id,
            new ChanceNode("Nested source", new ProbabilitySource(source)));
        tree.Add(tree.Root.Id, new RemainderNode("Survival"));
        return Response(hazards, tree, name);
    }

    /// <summary>Builds an uncertain nested response with reversible sibling presentation order.</summary>
    private static EventTreeResponse NestedInvariantResponse(
        bool reverse, out EventTreeResponse nested)
    {
        var nestedTree = new EventTree();
        var a = new ChanceNode("A", new ProbabilitySource(
            UncertainTable(new[] { 0d, 1d },
                new[] { 0.01d, 0.03d }, new[] { 0.07d, 0.11d })));
        var b = new ChanceNode("B", new ProbabilitySource(
            UncertainTable(new[] { 0d, 1d },
                new[] { 0.02d, 0.04d }, new[] { 0.08d, 0.12d })));
        if (reverse)
        {
            nestedTree.Add(nestedTree.Root.Id, b);
            nestedTree.Add(nestedTree.Root.Id, a);
        }
        else
        {
            nestedTree.Add(nestedTree.Root.Id, a);
            nestedTree.Add(nestedTree.Root.Id, b);
        }
        nestedTree.Add(nestedTree.Root.Id, new RemainderNode("Nested survival"));
        nested = Response(new[] { 0d, 1d }, nestedTree, "Nested invariant");
        return SingleSourceResponse(new[] { 0d, 1d }, nested, "Outer invariant");
    }

    /// <summary>Builds a labeled event-tree response.</summary>
    private static EventTreeResponse Response(
        double[] hazards, EventTree tree, string name)
    {
        return new EventTreeResponse(hazards, tree)
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }

    /// <summary>Evaluates a two-knot response curve with the established Normal-Z interpolation.</summary>
    private static double InterpolateResponseCurve(OrderedPairedData curve, double hazard)
    {
        if (hazard <= curve[0].X) return curve[0].Y;
        if (hazard >= curve[1].X) return curve[1].Y;
        double fraction = (hazard - curve[0].X) / (curve[1].X - curve[0].X);
        return NormalZInterpolate(curve[0].Y, curve[1].Y, fraction);
    }

    /// <summary>Interpolates one response probability on the standard-normal quantile axis.</summary>
    private static double NormalZInterpolate(
        double lowerProbability, double upperProbability, double fraction)
    {
        var standardNormal = new Normal(0d, 1d);
        double lowerZ = standardNormal.InverseCDF(lowerProbability);
        double upperZ = standardNormal.InverseCDF(upperProbability);
        return standardNormal.CDF(lowerZ + fraction * (upperZ - lowerZ));
    }

    /// <summary>Builds an aligned deterministic table.</summary>
    private static UncertainOrderedPairedData DeterministicTable(
        double[] hazards, double[] probabilities)
    {
        return new UncertainOrderedPairedData(
            hazards.Select((hazard, index) =>
                new UncertainOrdinate(hazard,
                    new Deterministic(probabilities[index]))).ToArray(),
            true, SortOrder.Ascending, false, SortOrder.None,
            UnivariateDistributionType.Deterministic);
    }

    /// <summary>Builds an aligned co-monotonic uniform table.</summary>
    private static UncertainOrderedPairedData UncertainTable(
        double[] hazards, double[] minimums, double[] maximums)
    {
        return new UncertainOrderedPairedData(
            hazards.Select((hazard, index) =>
                new UncertainOrdinate(hazard,
                    new Uniform(minimums[index], maximums[index]))).ToArray(),
            true, SortOrder.Ascending, false, SortOrder.None,
            UnivariateDistributionType.Uniform);
    }
}
