using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.Systems.Components;
using RMC.TotalRisk.Systems.Components.Graph;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;

namespace RMC.TotalRisk.Verification.RiskFunctions.Responses.EventTrees;

/// <summary>
/// Focused event-tree verification against independently derived conditional path
/// products and direct uncertainty-table samples. This partial family covers scalar and aligned
/// tables, recursive response probability sources, internal/external independent-clone links,
/// two-mode round trips, occurrence-level LHS reproducibility, legacy recursive XML conversion,
/// representative shipped templates, graph-connected arbitrary n-way per-leaf consequences,
/// independent fixed-seed branch-routing Monte Carlo, aggregate LHS variance reduction with
/// complete strata coverage, and end-to-end sequential/multi-worker/default-scheduler
/// reproducibility. The isolated F5 harness supplies the family performance and byte-gate
/// evidence recorded in <c>scripts/perf/RESULTS.md</c>.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// No legacy <c>Test_Product</c> value is used as an oracle. Expected values below are derived
/// directly from conditional probability identities: a terminal path is the product of its
/// conditional branches, explicit sibling values above one are divided by their compensated sum,
/// and aggregate failure is the sum over explicitly classified failure terminals.
/// </para>
/// </remarks>
[TestClass]
public partial class EventTreeVerification
{
    /// <summary>Verifies a representative deep/wide tree against hand-derived terminal path products.</summary>
    [TestMethod]
    public void Test_DeepWideTree_EqualsAnalyticPathProducts()
    {
        var tree = new EventTree();
        var loadCase = new ChanceNode("Load case", new ProbabilitySource(0.6d)) { IsFailure = false };
        tree.Add(tree.Root.Id, loadCase);
        tree.Add(tree.Root.Id, new ChanceNode("Direct failure", new ProbabilitySource(0.1d)));
        tree.Add(tree.Root.Id, new RemainderNode("Root residual"));
        tree.Add(loadCase.Id, new ChanceNode("Mechanism A", new ProbabilitySource(0.25d)));
        tree.Add(loadCase.Id, new ChanceNode("Mechanism B", new ProbabilitySource(0.15d)));
        tree.Add(loadCase.Id, new RemainderNode("Survival"));
        var response = Response(tree);

        var sample = response.SampleBranches();
        double expectedFailure = 0.1d + (0.6d * 0.25d) + (0.6d * 0.15d);

        Assert.AreEqual(expectedFailure, response.SampleResponseFunction()[0].Y, 1e-14d);
        Assert.AreEqual(1d, sample.Probabilities.Sum(row => row[0]), 1e-14d);
        Assert.AreEqual(1d, sample.Probabilities.Sum(row => row[1]), 1e-14d);
    }

    /// <summary>
    /// Verifies graph-connected n-way terminal consequences against independent conditional
    /// path products and preserves aggregate failure as the exact sum of failure leaves.
    /// </summary>
    [TestMethod]
    public void Test_GraphConnectedPerLeafConsequences_EqualAnalyticPathProducts()
    {
        var tree = new EventTree();
        var load = new ChanceNode("Load case", new ProbabilitySource(0.6d))
        {
            IsFailure = false,
        };
        var direct = new ChanceNode("Direct failure", new ProbabilitySource(0.1d));
        var rootResidual = new RemainderNode("Root survival") { IsFailure = false };
        var mechanismA = new ChanceNode("Mechanism A", new ProbabilitySource(0.25d));
        var mechanismB = new ChanceNode("Mechanism B", new ProbabilitySource(0.15d));
        var loadedResidual = new RemainderNode("Loaded survival") { IsFailure = false };
        tree.Add(tree.Root.Id, load);
        tree.Add(tree.Root.Id, direct);
        tree.Add(tree.Root.Id, rootResidual);
        tree.Add(load.Id, mechanismA);
        tree.Add(load.Id, mechanismB);
        tree.Add(load.Id, loadedResidual);
        EventTreeResponse response = Response(tree);

        var component = new SystemComponent(new TabularHazard
        {
            Name = "Stage frequency",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        });
        HazardElement hazard = component.Graph.GetElements<HazardElement>().Single();
        var responseElement = new ResponseElement("Expanded event tree")
        {
            Function = response,
            ExpandBranchOutputs = true,
            Input = new RiskConnection(hazard),
        };
        component.Graph.AddElement(responseElement);

        var oracle = new Dictionary<System.Guid, double>
        {
            [direct.Id] = 0.1d,
            [mechanismA.Id] = 0.6d * 0.25d,
            [mechanismB.Id] = 0.6d * 0.15d,
            [loadedResidual.Id] = 0.6d * (1d - 0.25d - 0.15d),
            [rootResidual.Id] = 1d - 0.6d - 0.1d,
        };
        var selected = new List<ResponseBranchDescriptor>();
        foreach (ResponseBranchDescriptor branch in response.GetBranches()
            .Where(branch => oracle.ContainsKey(branch.Id)))
        {
            selected.Add(branch);
            var consequence = new ConsequenceElement(branch.Name)
            {
                Input = responseElement.CreateBranchConnection(branch.Id),
            };
            consequence.Functions.Add(new TabularConsequence
            {
                Name = branch.Name + " consequence",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                SpecifiedConsequence = "Damage",
                ConsequenceUnit = "$",
            });
            component.Graph.AddElement(consequence);
        }

        IReadOnlyList<FailureMode> modes = component.FailureModes;
        Assert.AreEqual(selected.Count, modes.Count);
        double projectedFailure = 0d;
        for (int i = 0; i < modes.Count; i++)
        {
            double expected = oracle[selected[i].Id];
            double actual = modes[i].Sample(null).SRP(0d);
            Assert.AreEqual(expected, actual, 1e-14d, selected[i].Name);
            if (selected[i].IsFailure) projectedFailure += actual;
        }
        Assert.AreEqual(0.1d + 0.6d * 0.25d + 0.6d * 0.15d,
            projectedFailure, 1e-14d);
        Assert.AreEqual(projectedFailure, response.SampleResponseFunction()[0].Y, 1e-14d);
    }

    /// <summary>Verifies proportional sibling normalization and zero residual in the over-allocated case.</summary>
    [TestMethod]
    public void Test_OverAllocatedSiblings_EqualsAnalyticNormalization()
    {
        var tree = new EventTree();
        tree.Add(tree.Root.Id, new ChanceNode("Failure", new ProbabilitySource(0.8d)));
        tree.Add(tree.Root.Id, new ChanceNode("Survival", new ProbabilitySource(0.7d)) { IsFailure = false });
        tree.Add(tree.Root.Id, new RemainderNode("Residual"));
        var response = Response(tree);

        var sample = response.SampleBranches();
        int residual = Enumerable.Range(0, sample.Branches.Count).Single(i => sample.Branches[i].Name == "Residual");

        Assert.AreEqual(0.8d / (0.8d + 0.7d), response.SampleResponseFunction()[0].Y, 1e-14d);
        Assert.AreEqual(0d, sample.Probabilities[residual][0], 1e-14d);
    }

    /// <summary>Verifies indexed uncertain-tree outputs realization-for-realization against the table source.</summary>
    [TestMethod]
    public void Test_IndexedUncertainty_EqualsDirectChildCurveSamples()
    {
        var table = new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Uniform(0.05d, 0.25d)),
                new UncertainOrdinate(1d, new Uniform(0.4d, 0.8d)),
            }, true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Uniform);
        var tree = new EventTree();
        tree.Add(tree.Root.Id, new ChanceNode("Failure", new ProbabilitySource(table)));
        tree.Add(tree.Root.Id, new RemainderNode("No failure"));
        var response = Response(tree);
        response.SetupSampler(256, 8675309, SamplingScheme.LatinHypercube);

        for (int realization = 0; realization < 256; realization++)
        {
            double percentile = response.SampledPercentile(realization, 0);
            OrderedPairedData direct = table.CurveSample(percentile);
            OrderedPairedData actual = response.SampleResponseFunction(realization);
            Assert.AreEqual(direct[0].Y, actual[0].Y);
            Assert.AreEqual(direct[1].Y, actual[1].Y);
        }
    }

    /// <summary>
    /// Verifies direct and multi-level nested response sources against an independent Normal-Z
    /// interpolation oracle evaluated at the caller's hazard levels.
    /// </summary>
    [TestMethod]
    public void Test_NestedResponses_EqualAnalyticCallerHazardOracle()
    {
        var deepestTable = new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Deterministic(0.2d)),
                new UncertainOrdinate(2d, new Deterministic(0.6d)),
            }, true, SortOrder.Ascending, false, SortOrder.None,
            UnivariateDistributionType.Deterministic);
        var deepestTree = new EventTree();
        deepestTree.Add(deepestTree.Root.Id,
            new ChanceNode("Deep failure", new ProbabilitySource(deepestTable)));
        deepestTree.Add(deepestTree.Root.Id, new RemainderNode("Deep survival"));
        EventTreeResponse deepest = Response(new[] { 0d, 2d }, deepestTree);

        var middleTree = new EventTree();
        middleTree.Add(middleTree.Root.Id,
            new ChanceNode("Nested deep response", new ProbabilitySource(deepest)));
        middleTree.Add(middleTree.Root.Id, new RemainderNode("Middle survival"));
        EventTreeResponse middle = Response(new[] { 0d, 2d }, middleTree);

        var outerTree = new EventTree();
        var load = new ChanceNode("Load case", new ProbabilitySource(0.6d))
        {
            IsFailure = false,
        };
        outerTree.Add(outerTree.Root.Id, load);
        outerTree.Add(outerTree.Root.Id, new RemainderNode("No load"));
        outerTree.Add(load.Id,
            new ChanceNode("Nested middle response", new ProbabilitySource(middle)));
        outerTree.Add(load.Id, new RemainderNode("Loaded survival"));
        EventTreeResponse outer = Response(new[] { 0d, 1d, 2d }, outerTree);

        OrderedPairedData actual = outer.SampleResponseFunction();
        double[] expected =
        {
            0.6d * 0.2d,
            0.6d * NormalZInterpolate(0.2d, 0.6d, 0.5d),
            0.6d * 0.6d,
        };

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(expected[i], actual[i].Y, 1e-14d);
        }
    }

    /// <summary>
    /// Verifies every nested Latin-hypercube realization against a direct deepest-table draw and
    /// an independent caller-hazard interpolation/path-product calculation.
    /// </summary>
    [TestMethod]
    public void Test_NestedResponses_LhsRealizationsEqualDirectDeepestSamples()
    {
        var deepestTable = new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Uniform(0.1d, 0.3d)),
                new UncertainOrdinate(2d, new Uniform(0.3d, 0.7d)),
            }, true, SortOrder.Ascending, false, SortOrder.None,
            UnivariateDistributionType.Uniform);
        var deepestTree = new EventTree();
        deepestTree.Add(deepestTree.Root.Id,
            new ChanceNode("Deep failure", new ProbabilitySource(deepestTable)));
        deepestTree.Add(deepestTree.Root.Id, new RemainderNode("Deep survival"));
        EventTreeResponse deepest = Response(new[] { 0d, 2d }, deepestTree);
        var middleTree = new EventTree();
        middleTree.Add(middleTree.Root.Id,
            new ChanceNode("Nested deep response", new ProbabilitySource(deepest)));
        middleTree.Add(middleTree.Root.Id, new RemainderNode("Middle survival"));
        EventTreeResponse middle = Response(new[] { 0d, 2d }, middleTree);
        var outerTree = new EventTree();
        outerTree.Add(outerTree.Root.Id,
            new ChanceNode("Nested middle response", new ProbabilitySource(middle)));
        outerTree.Add(outerTree.Root.Id, new RemainderNode("Outer survival"));
        EventTreeResponse outer = Response(new[] { 0d, 1d, 2d }, outerTree);
        outer.SetupSampler(256, 24681357, SamplingScheme.LatinHypercube);

        Assert.AreEqual(1, outer.SamplingDimensions);
        for (int realization = 0; realization < 256; realization++)
        {
            double percentile = outer.SampledPercentile(realization, 0);
            OrderedPairedData deepestSample = deepestTable.CurveSample(percentile);
            OrderedPairedData actual = outer.SampleResponseFunction(realization);
            for (int hazardIndex = 0; hazardIndex < outer.HazardLevels.Count; hazardIndex++)
            {
                double expected = InterpolateResponseCurve(
                    deepestSample, outer.HazardLevels[hazardIndex]);
                Assert.AreEqual(expected, actual[hazardIndex].Y, 1e-14d);
            }
        }
    }

    /// <summary>
    /// Verifies two external independent links against an explicitly cloned analytic model,
    /// including the established over-allocation normalization rule.
    /// </summary>
    [TestMethod]
    public void Test_LinkedSubtrees_EqualExplicitCloneAndSerializationModes()
    {
        var targetTree = new EventTree();
        var targetSequence = new ChanceNode("Reusable sequence", new ProbabilitySource(0.6d)) { IsFailure = false };
        targetTree.Add(targetTree.Root.Id, targetSequence);
        targetTree.Add(targetTree.Root.Id, new RemainderNode("Target root survival"));
        targetTree.Add(targetSequence.Id, new ChanceNode("Failure", new ProbabilitySource(0.25d)));
        targetTree.Add(targetSequence.Id, new RemainderNode("Sequence survival"));
        var target = Response(targetTree);
        target.Name = "Stored linked tree";

        var linkedTree = new EventTree();
        linkedTree.LinkIndependent(linkedTree.Root.Id, target, targetSequence.Id, "Occurrence A");
        linkedTree.LinkIndependent(linkedTree.Root.Id, target, targetSequence.Id, "Occurrence B");
        linkedTree.Add(linkedTree.Root.Id, new RemainderNode("Owner survival"));
        var linked = Response(linkedTree);

        var explicitTree = new EventTree();
        for (int occurrence = 0; occurrence < 2; occurrence++)
        {
            var sequence = new ChanceNode($"Explicit {occurrence}", new ProbabilitySource(0.6d)) { IsFailure = false };
            explicitTree.Add(explicitTree.Root.Id, sequence);
            explicitTree.Add(sequence.Id, new ChanceNode($"Failure {occurrence}", new ProbabilitySource(0.25d)));
            explicitTree.Add(sequence.Id, new RemainderNode($"Survival {occurrence}"));
        }
        explicitTree.Add(explicitTree.Root.Id, new RemainderNode("Owner survival"));
        var explicitClone = Response(explicitTree);
        IRiskFunctionResolver resolver = new RiskFunctionResolver(
            id => id == target.Id ? target : null,
            name => name == target.Name ? target : null);
        var selfContained = new EventTreeResponse(
            linked.ToXElement(RiskSerializationMode.SelfContained));
        var byReference = new EventTreeResponse(
            linked.ToXElement(RiskSerializationMode.ByReference), resolver);

        double expected = (0.6d / 1.2d * 0.25d) + (0.6d / 1.2d * 0.25d);
        Assert.AreEqual(expected, linked.SampleResponseFunction()[0].Y, 1e-14d);
        Assert.AreEqual(explicitClone.SampleResponseFunction()[0].Y,
            linked.SampleResponseFunction()[0].Y, 1e-14d);
        Assert.AreEqual(linked.SampleResponseFunction()[0].Y,
            selfContained.SampleResponseFunction()[0].Y);
        Assert.AreEqual(linked.SampleResponseFunction()[0].Y,
            byReference.SampleResponseFunction()[0].Y);
        CollectionAssert.AreEqual(linked.CanonicalHash(), selfContained.CanonicalHash());
        CollectionAssert.AreEqual(linked.CanonicalHash(), byReference.CanonicalHash());
    }

    /// <summary>
    /// Verifies 256 linked LHS realizations reproduce bit-for-bit across self-contained,
    /// by-reference, metadata, and sibling-order representations while the two independent
    /// occurrences demonstrably consume distinct streams.
    /// </summary>
    [TestMethod]
    public void Test_LinkedUncertainty_ReproducesAcrossRoundTripsAndOrder()
    {
        var table = new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Uniform(0.1d, 0.3d)),
                new UncertainOrdinate(1d, new Uniform(0.2d, 0.4d)),
            }, true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Uniform);
        var targetTree = new EventTree();
        var targetNode = new ChanceNode("Target", new ProbabilitySource(table));
        targetTree.Add(targetTree.Root.Id, targetNode);
        var target = Response(targetTree);
        target.Name = "Uncertain target";

        var ownerTree = new EventTree();
        ownerTree.LinkIndependent(ownerTree.Root.Id, target, targetNode.Id, "Occurrence A");
        ownerTree.LinkIndependent(ownerTree.Root.Id, target, targetNode.Id, "Occurrence B");
        ownerTree.Add(ownerTree.Root.Id, new RemainderNode("No failure"));
        var original = Response(ownerTree);
        IRiskFunctionResolver resolver = new RiskFunctionResolver(
            id => id == target.Id ? target : null,
            name => name == target.Name ? target : null);
        var selfContained = new EventTreeResponse(
            original.ToXElement(RiskSerializationMode.SelfContained));
        var byReference = new EventTreeResponse(
            original.ToXElement(RiskSerializationMode.ByReference), resolver);
        EventTreeLinkNode[] reorderedLinks = byReference.EventTree.Nodes.OfType<EventTreeLinkNode>().ToArray();
        byReference.EventTree.Move(reorderedLinks[1].Id, byReference.EventTree.Root.Id, reorderedLinks[0].Id);
        byReference.Name = "Renamed owner";
        target.Name = "Renamed target";
        targetNode.Name = "Renamed node";

        original.SetupSampler(256, 97531, SamplingScheme.LatinHypercube);
        selfContained.SetupSampler(256, 97531, SamplingScheme.LatinHypercube);
        byReference.SetupSampler(256, 97531, SamplingScheme.LatinHypercube);

        CollectionAssert.AreEqual(original.CanonicalHash(), selfContained.CanonicalHash());
        CollectionAssert.AreEqual(original.CanonicalHash(), byReference.CanonicalHash());
        bool distinctStreams = false;
        for (int realization = 0; realization < 256; realization++)
        {
            OrderedPairedData expected = original.SampleResponseFunction(realization);
            Assert.AreEqual(expected[0].Y, selfContained.SampleResponseFunction(realization)[0].Y);
            Assert.AreEqual(expected[1].Y, selfContained.SampleResponseFunction(realization)[1].Y);
            Assert.AreEqual(expected[0].Y, byReference.SampleResponseFunction(realization)[0].Y);
            Assert.AreEqual(expected[1].Y, byReference.SampleResponseFunction(realization)[1].Y);

            var branches = original.SampleBranches(realization);
            int first = Enumerable.Range(0, branches.Branches.Count)
                .Single(i => branches.Branches[i].Name == "Occurrence A");
            int second = Enumerable.Range(0, branches.Branches.Count)
                .Single(i => branches.Branches[i].Name == "Occurrence B");
            distinctStreams |= branches.Probabilities[first][0] != branches.Probabilities[second][0];
        }
        Assert.IsTrue(distinctStreams);
    }

    /// <summary>Builds a labeled event-tree response over the common two-knot hazard axis.</summary>
    /// <param name="tree">The authored tree.</param>
    /// <returns>The valid response.</returns>
    private static EventTreeResponse Response(EventTree tree)
    {
        return new EventTreeResponse(new[] { 0d, 1d }, tree)
        {
            Name = "Verification event tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }

    /// <summary>Builds a labeled event-tree response over the supplied hazard axis.</summary>
    /// <param name="hazards">The caller hazard levels.</param>
    /// <param name="tree">The authored tree.</param>
    /// <returns>The valid response.</returns>
    private static EventTreeResponse Response(double[] hazards, EventTree tree)
    {
        return new EventTreeResponse(hazards, tree)
        {
            Name = "Verification event tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }

    /// <summary>Evaluates a two-knot response curve using the established Normal-Z contract.</summary>
    private static double InterpolateResponseCurve(OrderedPairedData curve, double hazard)
    {
        if (hazard <= curve[0].X) return curve[0].Y;
        if (hazard >= curve[1].X) return curve[1].Y;
        double fraction = (hazard - curve[0].X) / (curve[1].X - curve[0].X);
        return NormalZInterpolate(curve[0].Y, curve[1].Y, fraction);
    }

    /// <summary>Interpolates probability on the standard-normal quantile axis.</summary>
    private static double NormalZInterpolate(
        double lowerProbability, double upperProbability, double fraction)
    {
        var standardNormal = new Normal(0d, 1d);
        double lowerZ = standardNormal.InverseCDF(lowerProbability);
        double upperZ = standardNormal.InverseCDF(upperProbability);
        return standardNormal.CDF(lowerZ + fraction * (upperZ - lowerZ));
    }
}
