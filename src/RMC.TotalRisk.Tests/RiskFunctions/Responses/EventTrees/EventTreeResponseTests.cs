using System;
using System.Linq;
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

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.EventTrees;

/// <summary>
/// Tests EventTreeResponse probability algebra, branching outputs, uncertainty sampling,
/// serialization modes, canonical identity, and factory integration.
/// </summary>
[TestClass]
public class EventTreeResponseTests
{
    /// <summary>Verifies nested conditional path products and failure-leaf summation.</summary>
    [TestMethod]
    public void Test_SampleResponseFunction_NestedPathProduct()
    {
        var tree = new EventTree();
        var first = new ChanceNode("First", new ProbabilitySource(0.4d)) { IsFailure = false };
        tree.Add(tree.Root.Id, first);
        tree.Add(tree.Root.Id, new RemainderNode("Root remainder"));
        tree.Add(first.Id, new ChanceNode("Failure", new ProbabilitySource(0.25d)));
        tree.Add(first.Id, new RemainderNode("Nested remainder"));
        var response = ValidResponse(tree);

        OrderedPairedData curve = response.SampleResponseFunction();

        Assert.AreEqual(0.1d, curve[0].Y, 1e-14d);
        Assert.AreEqual(0.1d, curve[1].Y, 1e-14d);
    }

    /// <summary>Verifies explicit siblings above one normalize proportionally before path products.</summary>
    [TestMethod]
    public void Test_SampleBranches_ExplicitSumAboveOne_NormalizesProportionally()
    {
        var tree = new EventTree();
        tree.Add(tree.Root.Id, new ChanceNode("Failure", new ProbabilitySource(0.8d)));
        tree.Add(tree.Root.Id, new ChanceNode("Nonfailure", new ProbabilitySource(0.7d)) { IsFailure = false });
        tree.Add(tree.Root.Id, new RemainderNode());
        var response = ValidResponse(tree);

        var sample = response.SampleBranches();
        OrderedPairedData curve = response.SampleResponseFunction();

        Assert.AreEqual(0.8d / 1.5d, curve[0].Y, 1e-14d);
        int remainder = Enumerable.Range(0, sample.Branches.Count).Single(i => sample.Branches[i].Name == "Remainder");
        Assert.AreEqual(0d, sample.Probabilities[remainder][0], 1e-14d);
    }

    /// <summary>Verifies an omitted remainder becomes an explicit aggregate unmodeled branch.</summary>
    [TestMethod]
    public void Test_SampleBranches_NoRemainder_EmitsImplicitNonfailure()
    {
        var tree = new EventTree();
        tree.Add(tree.Root.Id, new ChanceNode("Failure", new ProbabilitySource(0.2d)));
        var response = ValidResponse(tree);

        var sample = response.SampleBranches();
        int implicitIndex = Enumerable.Range(0, sample.Branches.Count).Single(i => sample.Branches[i].Name == "Unmodeled");

        Assert.AreEqual("Unmodeled", sample.Branches[implicitIndex].Name);
        Assert.IsFalse(sample.Branches[implicitIndex].IsFailure);
        Assert.AreEqual(0.8d, sample.Probabilities[implicitIndex][0], 1e-14d);
        Assert.AreEqual(0.2d, response.SampleResponseFunction()[0].Y, 1e-14d);
    }

    /// <summary>Verifies every branch sample is finite, bounded, and exhaustive at each hazard.</summary>
    [TestMethod]
    public void Test_SampleBranches_FormsExhaustivePartition()
    {
        var response = TabularFailureResponse();
        ResponseBranchSample sample = response.SampleBranches(0.25d);

        for (int h = 0; h < sample.Hazards.Count; h++)
            Assert.AreEqual(1d, sample.Probabilities.Sum(row => row[h]), 1e-12d);
        Assert.AreEqual(0.15d, response.SampleResponseFunction(0.25d)[0].Y, 1e-12d);
        Assert.AreEqual(0.3d, response.SampleResponseFunction(0.25d)[1].Y, 1e-12d);
    }

    /// <summary>Verifies LHS realization streams reproduce across equal-content instances.</summary>
    [TestMethod]
    public void Test_SetupSampler_EqualContent_ReproducesBitExactly()
    {
        var first = TabularFailureResponse();
        var second = TabularFailureResponse();
        first.SetupSampler(32, 12345, SamplingScheme.LatinHypercube);
        second.SetupSampler(32, 12345, SamplingScheme.LatinHypercube);

        for (int realization = 0; realization < 32; realization++)
        {
            var a = first.SampleResponseFunction(realization);
            var b = second.SampleResponseFunction(realization);
            Assert.AreEqual(a[0].Y, b[0].Y);
            Assert.AreEqual(a[1].Y, b[1].Y);
        }
    }

    /// <summary>Verifies referenced-response dimensions expose the actual recursively seeded child draws.</summary>
    [TestMethod]
    public void Test_SetupSampler_ReferencedResponseDimensions_AreFlattened()
    {
        var child = new TabularResponse
        {
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = TabularFailureResponse().EventTree.Nodes.OfType<ChanceNode>().Single().ProbabilitySource.Table!,
        };
        var tree = new EventTree();
        tree.Add(tree.Root.Id, new ChanceNode("Failure", new ProbabilitySource(child)));
        tree.Add(tree.Root.Id, new RemainderNode());
        var response = ValidResponse(tree);

        response.SetupSampler(16, 45678, SamplingScheme.LatinHypercube);

        Assert.AreEqual(1, response.SamplingDimensions);
        Assert.AreEqual(0, child.SampleSize,
            "Recursive setup must not overwrite the live stored child sampler.");
        for (int realization = 0; realization < 16; realization++)
        {
            double percentile = response.SampledPercentile(realization, 0);
            OrderedPairedData expected = child.UncertainOrderedPairedData.CurveSample(percentile);
            OrderedPairedData actual = response.SampleResponseFunction(realization);
            Assert.AreEqual(expected[0].Y, actual[0].Y);
            Assert.AreEqual(expected[1].Y, actual[1].Y);
        }
    }
    /// <summary>Verifies display metadata, persistent ids, and sibling presentation order are hash-inert.</summary>
    [TestMethod]
    public void Test_CanonicalHash_MetadataAndPresentationOrder_Inert()
    {
        var first = TwoBranchResponse(false);
        var second = TwoBranchResponse(true);
        second.Name = "Renamed function";
        second.Description = "Changed";
        second.EventTree.Root.Name = "Different root";
        foreach (var node in second.EventTree.Nodes) node.Description = "metadata";

        CollectionAssert.AreEqual(first.CanonicalHash(), second.CanonicalHash());
        OrderedPairedData firstCurve = first.SampleResponseFunction();
        OrderedPairedData secondCurve = second.SampleResponseFunction();
        Assert.AreEqual(firstCurve.Count, secondCurve.Count);
        for (int i = 0; i < firstCurve.Count; i++)
            Assert.AreEqual(firstCurve[i].Y, secondCurve[i].Y);
    }

    /// <summary>Verifies authored branch ports survive rename, reorder, compute edits, and XML round trips.</summary>
    [TestMethod]
    public void Test_BranchOutputPorts_ArePersistentAndStable()
    {
        var response = TwoBranchResponse(false);
        var baseline = response.GetBranches().Where(branch => branch.OutputPort >= 3)
            .ToDictionary(branch => branch.Id, branch => branch.OutputPort);
        var chances = response.EventTree.Nodes.OfType<ChanceNode>().ToArray();

        response.EventTree.Move(chances[1].Id, response.EventTree.Root.Id, chances[0].Id);
        chances[0].Name = "Renamed";
        chances[0].ProbabilitySource = new ProbabilitySource(0.25d);

        foreach (var branch in response.GetBranches().Where(branch => branch.OutputPort >= 3))
            Assert.AreEqual(baseline[branch.Id], branch.OutputPort);

        var restored = new EventTreeResponse(response.ToXElement());
        foreach (var branch in restored.GetBranches().Where(branch => branch.OutputPort >= 3))
            Assert.AreEqual(baseline[branch.Id], branch.OutputPort);
        Assert.AreEqual(2, response.GetBranches().Single(branch => branch.Name == "Unmodeled").OutputPort);
    }

    /// <summary>Verifies compute-relevant probability and terminal classification edits move the hash.</summary>
    [TestMethod]
    public void Test_CanonicalHash_ComputeEdits_Move()
    {
        var response = TwoBranchResponse(false);
        byte[] baseline = response.CanonicalHash();
        var chance = response.EventTree.Nodes.OfType<ChanceNode>().First();

        chance.ProbabilitySource = new ProbabilitySource(0.25d);
        CollectionAssert.AreNotEqual(baseline, response.CanonicalHash());
        baseline = response.CanonicalHash();
        chance.IsFailure = !chance.IsFailure;
        CollectionAssert.AreNotEqual(baseline, response.CanonicalHash());
    }

    /// <summary>Verifies self-contained and by-reference response sources restore one canonical identity.</summary>
    [TestMethod]
    public void Test_SerializationModes_ResponseSource_RoundTripSameHash()
    {
        var child = new TabularResponse
        {
            Name = "Stored fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = DeterministicTable(new[] { 0d, 1d }, new[] { 0.2d, 0.6d }),
        };
        var tree = new EventTree();
        tree.Add(tree.Root.Id, new ChanceNode("Failure", new ProbabilitySource(child)));
        tree.Add(tree.Root.Id, new RemainderNode());
        var original = ValidResponse(tree);
        IRiskFunctionResolver resolver = new RiskFunctionResolver(
            id => id == child.Id ? child : null,
            name => name == child.Name ? child : null);

        var selfContained = new EventTreeResponse(original.ToXElement(RiskSerializationMode.SelfContained));
        var byReference = new EventTreeResponse(original.ToXElement(RiskSerializationMode.ByReference), resolver);

        CollectionAssert.AreEqual(original.CanonicalHash(), selfContained.CanonicalHash());
        CollectionAssert.AreEqual(original.CanonicalHash(), byReference.CanonicalHash());
        Assert.AreSame(child, byReference.EventTree.Nodes.OfType<ChanceNode>().Single().ProbabilitySource.ResponseFunction);
    }

    /// <summary>Verifies validation catches malformed hazards and probabilities and warns on normalization.</summary>
    [TestMethod]
    public void Test_Validate_ReportsErrorsAndNormalizationWarning()
    {
        var tree = new EventTree();
        tree.Add(tree.Root.Id, new ChanceNode("A", new ProbabilitySource(0.8d)));
        tree.Add(tree.Root.Id, new ChanceNode("B", new ProbabilitySource(0.7d)));
        var response = ValidResponse(tree);

        var valid = response.Validate();
        Assert.IsTrue(valid.IsValid);
        Assert.IsTrue(valid.ValidationMessages.Any(message => message.StartsWith("Warning:", StringComparison.Ordinal)));

        response.SetHazardLevels(new[] { 1d, 0d });
        Assert.IsFalse(response.Validate().IsValid);
        tree.Nodes.OfType<ChanceNode>().First().ProbabilitySource = new ProbabilitySource(2d);
        Assert.IsFalse(response.Validate().IsValid);
    }

    /// <summary>Verifies runtime discrimination and factory reconstruction.</summary>
    [TestMethod]
    public void Test_DiscriminatorAndFactory_AreIntegrated()
    {
        var response = TwoBranchResponse(false);
        var restored = RiskFunctionFactory.CreateResponseFunction(response.ToXElement());

        Assert.AreEqual(ResponseFunctionType.EventTree, response.FunctionType);
        Assert.IsInstanceOfType<EventTreeResponse>(restored);
        CollectionAssert.AreEqual(response.CanonicalHash(), restored!.CanonicalHash());
    }

    /// <summary>Verifies an internal linked subtree is numerically identical to an explicit clone.</summary>
    [TestMethod]
    public void Test_InternalLink_EqualsExplicitClone()
    {
        var linkedTree = new EventTree();
        var reusable = new ChanceNode("Reusable sequence", new ProbabilitySource(0.4d)) { IsFailure = false };
        linkedTree.Add(linkedTree.Root.Id, reusable);
        linkedTree.Add(reusable.Id, new ChanceNode("Failure", new ProbabilitySource(0.25d)));
        linkedTree.Add(reusable.Id, new RemainderNode("Sequence survival"));
        linkedTree.LinkIndependent(linkedTree.Root.Id, reusable.Id, "Reused sequence");
        linkedTree.Add(linkedTree.Root.Id, new RemainderNode("Root survival"));
        var linked = ValidResponse(linkedTree);

        var explicitTree = new EventTree();
        for (int occurrence = 0; occurrence < 2; occurrence++)
        {
            var sequence = new ChanceNode($"Sequence {occurrence}", new ProbabilitySource(0.4d)) { IsFailure = false };
            explicitTree.Add(explicitTree.Root.Id, sequence);
            explicitTree.Add(sequence.Id, new ChanceNode($"Failure {occurrence}", new ProbabilitySource(0.25d)));
            explicitTree.Add(sequence.Id, new RemainderNode($"Survival {occurrence}"));
        }
        explicitTree.Add(explicitTree.Root.Id, new RemainderNode("Root survival"));
        var explicitClone = ValidResponse(explicitTree);

        Assert.AreEqual(0.2d, linked.SampleResponseFunction()[0].Y, 1e-14d);
        Assert.AreEqual(explicitClone.SampleResponseFunction()[0].Y,
            linked.SampleResponseFunction()[0].Y, 1e-14d);
        Assert.AreEqual(1d, linked.SampleBranches().Probabilities.Sum(row => row[0]), 1e-14d);
    }

    /// <summary>Verifies repeated internal link occurrences receive distinct, reproducible LHS bindings.</summary>
    [TestMethod]
    public void Test_InternalLink_UncertainOccurrenceBindings_AreIndependentAndReproducible()
    {
        var table = new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Uniform(0.1d, 0.3d)),
                new UncertainOrdinate(1d, new Uniform(0.2d, 0.4d)),
            }, true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Uniform);
        var tree = new EventTree();
        var target = new ChanceNode("Direct", new ProbabilitySource(table));
        tree.Add(tree.Root.Id, target);
        tree.LinkIndependent(tree.Root.Id, target.Id, "Linked");
        tree.Add(tree.Root.Id, new RemainderNode("No failure"));
        var response = ValidResponse(tree);
        var restored = new EventTreeResponse(response.ToXElement());

        response.SetupSampler(64, 24680, SamplingScheme.LatinHypercube);
        restored.SetupSampler(64, 24680, SamplingScheme.LatinHypercube);

        Assert.AreEqual(2, response.SamplingDimensions);
        bool observedDistinctOccurrence = false;
        for (int realization = 0; realization < 64; realization++)
        {
            ResponseBranchSample actual = response.SampleBranches(realization);
            ResponseBranchSample replay = restored.SampleBranches(realization);
            int direct = Enumerable.Range(0, actual.Branches.Count).Single(i => actual.Branches[i].Name == "Direct");
            int linked = Enumerable.Range(0, actual.Branches.Count).Single(i => actual.Branches[i].Name == "Linked");
            int replayDirect = Enumerable.Range(0, replay.Branches.Count).Single(i => replay.Branches[i].Name == "Direct");
            int replayLinked = Enumerable.Range(0, replay.Branches.Count).Single(i => replay.Branches[i].Name == "Linked");
            observedDistinctOccurrence |= actual.Probabilities[direct][0] != actual.Probabilities[linked][0];
            Assert.AreEqual(actual.Probabilities[direct][0], replay.Probabilities[replayDirect][0]);
            Assert.AreEqual(actual.Probabilities[linked][0], replay.Probabilities[replayLinked][0]);
            Assert.AreEqual(response.SampleResponseFunction(realization)[0].Y,
                restored.SampleResponseFunction(realization)[0].Y);
        }
        Assert.IsTrue(observedDistinctOccurrence);
    }

    /// <summary>Verifies external links round-trip in both modes without moving identity or values.</summary>
    [TestMethod]
    public void Test_ExternalLink_SerializationModes_RoundTripSameHashAndValues()
    {
        var targetTree = new EventTree();
        var sequence = new ChanceNode("Sequence", new ProbabilitySource(0.5d)) { IsFailure = false };
        targetTree.Add(targetTree.Root.Id, sequence);
        targetTree.Add(targetTree.Root.Id, new RemainderNode("Unused root mass"));
        targetTree.Add(sequence.Id, new ChanceNode("Failure", new ProbabilitySource(0.2d)));
        targetTree.Add(sequence.Id, new RemainderNode("Sequence survival"));
        var target = ValidResponse(targetTree);
        target.Name = "Stored event tree";

        var ownerTree = new EventTree();
        ownerTree.LinkIndependent(ownerTree.Root.Id, target, sequence.Id, "External sequence");
        ownerTree.Add(ownerTree.Root.Id, new RemainderNode("Owner survival"));
        var original = ValidResponse(ownerTree);
        byte[] baselineHash = original.CanonicalHash();
        double baseline = original.SampleResponseFunction()[0].Y;
        IRiskFunctionResolver resolver = new RiskFunctionResolver(
            id => id == target.Id ? target : null,
            name => name == target.Name ? target : null);

        XElement selfXml = original.ToXElement(RiskSerializationMode.SelfContained);
        XElement referenceXml = original.ToXElement(RiskSerializationMode.ByReference);
        var selfContained = new EventTreeResponse(selfXml);
        var byReference = new EventTreeResponse(referenceXml, resolver);

        Assert.IsNotNull(selfXml.Descendants(nameof(EventTreeResponse)).FirstOrDefault());
        Assert.IsNotNull(referenceXml.Descendants("FunctionReference").FirstOrDefault());
        Assert.AreEqual(0.1d, baseline, 1e-14d);
        Assert.AreEqual(baseline, selfContained.SampleResponseFunction()[0].Y);
        Assert.AreEqual(baseline, byReference.SampleResponseFunction()[0].Y);
        CollectionAssert.AreEqual(baselineHash, selfContained.CanonicalHash());
        CollectionAssert.AreEqual(baselineHash, byReference.CanonicalHash());
        Assert.AreSame(target,
            byReference.EventTree.Nodes.OfType<EventTreeLinkNode>().Single().TargetFunction);

        target.Name = "Renamed target";
        target.AssignNewId();
        sequence.Name = "Renamed sequence";
        ownerTree.Nodes.OfType<EventTreeLinkNode>().Single().Name = "Renamed link";
        CollectionAssert.AreEqual(baselineHash, original.CanonicalHash());

        sequence.ProbabilitySource = new ProbabilitySource(0.45d);
        CollectionAssert.AreNotEqual(baselineHash, original.CanonicalHash());
    }

    /// <summary>Verifies cross-function cycles are rejected with the complete diagnostic path and rollback.</summary>
    [TestMethod]
    public void Test_ExternalLink_CrossFunctionCycle_ReportsFullPathAndRollsBack()
    {
        var treeA = new EventTree();
        var branchA = new ChanceNode("Branch A", new ProbabilitySource(0.4d));
        treeA.Add(treeA.Root.Id, branchA);
        var responseA = ValidResponse(treeA);
        responseA.Name = "Tree A";

        var treeB = new EventTree();
        var branchB = new ChanceNode("Branch B", new ProbabilitySource(0.5d));
        treeB.Add(treeB.Root.Id, branchB);
        var responseB = ValidResponse(treeB);
        responseB.Name = "Tree B";

        treeA.LinkIndependent(branchA.Id, responseB, branchB.Id, "A to B");
        int beforeCount = treeB.Nodes.Count;

        InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(() =>
            treeB.LinkIndependent(branchB.Id, responseA, branchA.Id, "B to A"));

        StringAssert.Contains(exception.Message, "Cross-function event-tree cycle detected");
        StringAssert.Contains(exception.Message, "Tree A");
        StringAssert.Contains(exception.Message, "Tree B");
        StringAssert.Contains(exception.Message, "A to B");
        StringAssert.Contains(exception.Message, "B to A");
        Assert.AreEqual(beforeCount, treeB.Nodes.Count);
    }
    /// <summary>Verifies resolver and node-name fallbacks restore a link and repair ids on write.</summary>
    [TestMethod]
    public void Test_ExternalLink_NameFallbacks_ResolveAndRepairPersistence()
    {
        var targetTree = new EventTree();
        var targetNode = new ChanceNode("Target branch", new ProbabilitySource(0.3d));
        targetTree.Add(targetTree.Root.Id, targetNode);
        var target = ValidResponse(targetTree);
        target.Name = "Stored target";

        var ownerTree = new EventTree();
        ownerTree.LinkIndependent(ownerTree.Root.Id, target, targetNode.Id, "External");
        ownerTree.Add(ownerTree.Root.Id, new RemainderNode("No failure"));
        var owner = ValidResponse(ownerTree);
        XElement serialized = owner.ToXElement(RiskSerializationMode.ByReference);
        serialized.Descendants("FunctionReference").Single().Attribute("Id")!.Remove();
        serialized.Descendants("Target").Single()
            .SetAttributeValue(nameof(TreeNodeReference.NodeId), Guid.NewGuid().ToString("D"));
        IRiskFunctionResolver resolver = new RiskFunctionResolver(
            id => id == target.Id ? target : null,
            name => name == target.Name ? target : null);

        var restored = new EventTreeResponse(serialized, resolver);
        var validation = restored.Validate();
        XElement repaired = restored.ToXElement(RiskSerializationMode.ByReference);

        Assert.IsTrue(validation.IsValid);
        Assert.IsTrue(validation.ValidationMessages.Any(message =>
            message.Contains("name fallback", StringComparison.Ordinal)));
        Assert.AreSame(target,
            restored.EventTree.Nodes.OfType<EventTreeLinkNode>().Single().TargetFunction);
        CollectionAssert.AreEqual(owner.CanonicalHash(), restored.CanonicalHash());
        Assert.AreEqual(targetNode.Id.ToString("D"),
            repaired.Descendants("Target").Single().Attribute(nameof(TreeNodeReference.NodeId))!.Value);
        Assert.AreEqual(target.Id.ToString("D"),
            repaired.Descendants("FunctionReference").Single().Attribute("Id")!.Value);
    }
    /// <summary>Verifies repeated ordinary-response sources receive isolated occurrence samplers.</summary>
    [TestMethod]
    public void Test_InternalLink_RepeatedResponseSource_UsesIsolatedSamplers()
    {
        var child = new TabularResponse
        {
            Name = "Referenced fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0d, new Uniform(0.1d, 0.3d)),
                    new UncertainOrdinate(1d, new Uniform(0.2d, 0.4d)),
                }, true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Uniform),
        };
        var tree = new EventTree();
        var target = new ChanceNode("Direct response", new ProbabilitySource(child));
        tree.Add(tree.Root.Id, target);
        tree.LinkIndependent(tree.Root.Id, target.Id, "Linked response");
        tree.Add(tree.Root.Id, new RemainderNode("No failure"));
        var response = ValidResponse(tree);
        var replay = new EventTreeResponse(response.ToXElement());

        response.SetupSampler(64, 112233, SamplingScheme.LatinHypercube);
        replay.SetupSampler(64, 112233, SamplingScheme.LatinHypercube);

        Assert.AreEqual(2, response.SamplingDimensions);
        Assert.AreEqual(0, child.SampleSize,
            "Independent occurrence setup must preserve the live stored response sampler.");
        bool distinct = false;
        for (int realization = 0; realization < 64; realization++)
        {
            ResponseBranchSample branches = response.SampleBranches(realization);
            int direct = Enumerable.Range(0, branches.Branches.Count)
                .Single(i => branches.Branches[i].Name == "Direct response");
            int linked = Enumerable.Range(0, branches.Branches.Count)
                .Single(i => branches.Branches[i].Name == "Linked response");
            distinct |= branches.Probabilities[direct][0] != branches.Probabilities[linked][0];
            Assert.AreEqual(response.SampleResponseFunction(realization)[0].Y,
                replay.SampleResponseFunction(realization)[0].Y);
            Assert.AreEqual(response.SampleResponseFunction(realization)[1].Y,
                replay.SampleResponseFunction(realization)[1].Y);
        }
        Assert.IsTrue(distinct);
    }
    /// <summary>Verifies an external linked table is evaluated on the caller's hazard axis.</summary>
    [TestMethod]
    public void Test_ExternalLink_DifferentHazardAxis_InterpolatesAtCallerHazards()
    {
        var targetTable = DeterministicTable(new[] { 0d, 2d }, new[] { 0.2d, 0.6d });
        var targetTree = new EventTree();
        var targetNode = new ChanceNode("Target", new ProbabilitySource(targetTable));
        targetTree.Add(targetTree.Root.Id, targetNode);
        var target = new EventTreeResponse(new[] { 0d, 2d }, targetTree)
        {
            Name = "Coarse target",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };

        var ownerTree = new EventTree();
        ownerTree.LinkIndependent(ownerTree.Root.Id, target, targetNode.Id, "Interpolated target");
        ownerTree.Add(ownerTree.Root.Id, new RemainderNode("No failure"));
        var owner = new EventTreeResponse(new[] { 0d, 1d, 2d }, ownerTree)
        {
            Name = "Fine owner",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };

        OrderedPairedData actual = owner.SampleResponseFunction();

        Assert.AreEqual(0.2d, actual[0].Y);
        Assert.AreEqual(0.4d, actual[1].Y, 1e-14d);
        Assert.AreEqual(0.6d, actual[2].Y);
    }
    /// <summary>Builds a valid response wrapper over a supplied tree.</summary>
    /// <param name="tree">The authored tree.</param>
    /// <returns>The labeled response.</returns>
    private static EventTreeResponse ValidResponse(EventTree tree)
    {
        return new EventTreeResponse(new[] { 0d, 1d }, tree)
        {
            Name = "Event tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }

    /// <summary>Builds an uncertain tabular failure branch and residual non-failure branch.</summary>
    /// <returns>The response.</returns>
    private static EventTreeResponse TabularFailureResponse()
    {
        var table = new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Uniform(0.1d, 0.3d)),
                new UncertainOrdinate(1d, new Uniform(0.2d, 0.6d)),
            }, true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Uniform);
        var tree = new EventTree();
        tree.Add(tree.Root.Id, new ChanceNode("Failure", new ProbabilitySource(table)));
        tree.Add(tree.Root.Id, new RemainderNode("No failure"));
        return ValidResponse(tree);
    }

    /// <summary>Builds two explicit terminal branches, optionally reversing presentation order.</summary>
    /// <param name="reverse">Whether to add the branches in reverse order.</param>
    /// <returns>The response.</returns>
    private static EventTreeResponse TwoBranchResponse(bool reverse)
    {
        var tree = new EventTree();
        var failure = new ChanceNode(reverse ? "Renamed fail" : "Failure", new ProbabilitySource(0.2d));
        var nonfailure = new ChanceNode(reverse ? "Renamed survive" : "Survive", new ProbabilitySource(0.3d)) { IsFailure = false };
        if (reverse)
        {
            tree.Add(tree.Root.Id, nonfailure);
            tree.Add(tree.Root.Id, failure);
        }
        else
        {
            tree.Add(tree.Root.Id, failure);
            tree.Add(tree.Root.Id, nonfailure);
        }
        tree.Add(tree.Root.Id, new RemainderNode(reverse ? "Other" : "Remainder"));
        return ValidResponse(tree);
    }

    /// <summary>Builds an aligned deterministic uncertainty table.</summary>
    /// <param name="hazards">The hazards.</param>
    /// <param name="probabilities">The probabilities.</param>
    /// <returns>The table.</returns>
    private static UncertainOrderedPairedData DeterministicTable(double[] hazards, double[] probabilities)
    {
        return new UncertainOrderedPairedData(
            hazards.Select((hazard, index) => new UncertainOrdinate(hazard, new Deterministic(probabilities[index]))).ToArray(),
            true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic);
    }
}
