using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.Systems.Components;
using RMC.TotalRisk.Systems.Components.Graph;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;

namespace RMC.TotalRisk.Verification.RiskFunctions.Responses.FaultTrees;

/// <summary>
/// Core fault-tree verification against independent exhaustive Boolean enumeration and
/// closed-form gate identities. This greenfield family has no legacy oracle: every expected
/// value is derived in the test from first principles — weighted truth sums over every variable
/// assignment, series/parallel/exclusive/threshold gate formulas, hand inclusion–exclusion for
/// a repeated-event bridge, hand-derived minimal cut sets with an explicit anti-approximation
/// proof, referenced-source inlining, graph-connected risk equivalence against an identical
/// tabular fragility, and the loud decision-diagram resource diagnostics.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// The enumeration oracle walks the authored trees itself: it resolves transfers from their
/// public targets, unifies shared-logical occurrences onto one Boolean variable per independent
/// context, forks a fresh context at every independent-clone traversal, and sums
/// probability-weighted gate truth over all <c>2^V</c> assignments with compensated addition. It
/// never touches the compiled decision diagram, so agreement at 1e-13 — floating-point roundoff
/// scale for these expression sizes — verifies the production kernel end to end.
/// </para>
/// </remarks>
[TestClass]
public class FaultTreeVerification
{
    #region Independent exhaustive enumeration oracle

    /// <summary>Sums probability-weighted top-gate truth over every unique-variable assignment.</summary>
    /// <param name="response">The authored response.</param>
    /// <param name="hazard">The evaluated hazard level.</param>
    /// <param name="percentile">The co-monotonic source percentile, or -1 for source means.</param>
    /// <returns>The exhaustive top-event probability.</returns>
    private static double Enumerate(FaultTreeResponse response, double hazard, double percentile)
    {
        var variables = new List<(FaultTreeResponse Function, FaultTreeBasicEventNode Node)>();
        var index = new Dictionary<(FaultTreeBasicEventNode, object), int>();
        OracleNode root = ExpandOracle(response, response.FaultTree.Root, new object(), variables, index);
        Assert.IsTrue(variables.Count <= 20, "The enumeration fixture must stay exhaustively small.");

        var probabilities = new double[variables.Count];
        for (int i = 0; i < variables.Count; i++)
        {
            probabilities[i] = EvaluateOracleSource(variables[i].Function, variables[i].Node,
                hazard, percentile);
        }

        double sum = 0d;
        double compensation = 0d;
        for (ulong mask = 0; mask < 1UL << variables.Count; mask++)
        {
            double weight = 1d;
            for (int i = 0; i < variables.Count; i++)
            {
                weight *= (mask & (1UL << i)) != 0 ? probabilities[i] : 1d - probabilities[i];
            }
            if (EvaluateOracleTruth(root, mask))
            {
                double adjusted = weight - compensation;
                double next = sum + adjusted;
                compensation = (next - sum) - adjusted;
                sum = next;
            }
        }
        return sum;
    }

    /// <summary>
    /// Sums probability-weighted top-gate truth with one unified variable forced to a fixed
    /// probability — the conditional-evaluation oracle behind the exact importance measures
    /// (exact for forced values of zero and one). The variable is addressed by its authored
    /// node id, which the caller keeps unique per unified variable.
    /// </summary>
    /// <param name="response">The authored response.</param>
    /// <param name="hazard">The evaluated hazard level.</param>
    /// <param name="percentile">The co-monotonic source percentile, or -1 for source means.</param>
    /// <param name="nodeId">The forced variable's authored basic-event node id.</param>
    /// <param name="forced">The forced probability (zero or one for the exact conditionals).</param>
    /// <param name="baselineProbability">The variable's unforced probability at the percentile.</param>
    /// <returns>The exhaustive conditional top-event probability.</returns>
    private static double EnumerateWithOverride(FaultTreeResponse response, double hazard,
        double percentile, Guid nodeId, double forced, out double baselineProbability)
    {
        var variables = new List<(FaultTreeResponse Function, FaultTreeBasicEventNode Node)>();
        var index = new Dictionary<(FaultTreeBasicEventNode, object), int>();
        OracleNode root = ExpandOracle(response, response.FaultTree.Root, new object(), variables, index);
        Assert.IsTrue(variables.Count <= 20, "The enumeration fixture must stay exhaustively small.");

        int overrideIndex = -1;
        var probabilities = new double[variables.Count];
        for (int i = 0; i < variables.Count; i++)
        {
            probabilities[i] = EvaluateOracleSource(variables[i].Function, variables[i].Node,
                hazard, percentile);
            if (variables[i].Node.Id == nodeId)
            {
                Assert.AreEqual(-1, overrideIndex, "The forced node id must map onto exactly one unified variable.");
                overrideIndex = i;
            }
        }
        Assert.IsTrue(overrideIndex >= 0, "The forced node id must exist in the oracle expansion.");
        baselineProbability = probabilities[overrideIndex];
        probabilities[overrideIndex] = forced;

        double sum = 0d;
        double compensation = 0d;
        for (ulong mask = 0; mask < 1UL << variables.Count; mask++)
        {
            double weight = 1d;
            for (int i = 0; i < variables.Count; i++)
            {
                weight *= (mask & (1UL << i)) != 0 ? probabilities[i] : 1d - probabilities[i];
            }
            if (EvaluateOracleTruth(root, mask))
            {
                double adjusted = weight - compensation;
                double next = sum + adjusted;
                compensation = (next - sum) - adjusted;
                sum = next;
            }
        }
        return sum;
    }

    /// <summary>Expands one authored node into the oracle structure with independent contexts.</summary>
    /// <param name="function">The owning function.</param>
    /// <param name="node">The authored node.</param>
    /// <param name="context">The current independent-variable context.</param>
    /// <param name="variables">The accumulating unified-variable table.</param>
    /// <param name="index">The unification lookup keyed by node and context.</param>
    /// <returns>The oracle node.</returns>
    private static OracleNode ExpandOracle(FaultTreeResponse function, FaultTreeNodeBase node,
        object context, List<(FaultTreeResponse, FaultTreeBasicEventNode)> variables,
        Dictionary<(FaultTreeBasicEventNode, object), int> index)
    {
        if (node is FaultTreeTransferNode transfer)
        {
            FaultTreeResponse targetFunction = transfer.TargetFunction ?? function;
            FaultTreeNodeBase target = targetFunction.FaultTree.FindById(transfer.Target.NodeId)!;
            object targetContext = transfer.LinkMode == TreeLinkMode.IndependentClone
                ? new object() : context;
            return ExpandOracle(targetFunction, target, targetContext, variables, index);
        }
        if (node is FaultTreeBasicEventNode basic)
        {
            if (!index.TryGetValue((basic, context), out int ordinal))
            {
                ordinal = variables.Count;
                index.Add((basic, context), ordinal);
                variables.Add((function, basic));
            }
            return new OracleNode(ordinal, false, default, 0, null);
        }
        if (node is FaultTreeHouseEventNode house)
            return new OracleNode(-1, house.State, default, 0, null);
        var gate = (FaultTreeGateNode)node;
        var children = new OracleNode[node.Children.Count];
        for (int i = 0; i < children.Length; i++)
            children[i] = ExpandOracle(function, node.Children[i], context, variables, index);
        return new OracleNode(-1, false, gate.GateType, gate.K, children);
    }

    /// <summary>Evaluates one oracle node's truth for one variable assignment.</summary>
    /// <param name="node">The oracle node.</param>
    /// <param name="mask">The assignment bit mask.</param>
    /// <returns>The node truth.</returns>
    private static bool EvaluateOracleTruth(OracleNode node, ulong mask)
    {
        if (node.VariableIndex >= 0) return (mask & (1UL << node.VariableIndex)) != 0;
        if (node.Children == null) return node.ConstantState;
        int trueCount = 0;
        for (int i = 0; i < node.Children.Length; i++)
        {
            if (EvaluateOracleTruth(node.Children[i], mask)) trueCount++;
        }
        return node.GateType switch
        {
            FaultTreeGateType.And => trueCount == node.Children.Length,
            FaultTreeGateType.Or => trueCount > 0,
            FaultTreeGateType.Xor => trueCount == 1,
            FaultTreeGateType.KOfN => trueCount >= node.K,
            _ => throw new InvalidOperationException("Unsupported oracle gate."),
        };
    }

    /// <summary>Evaluates one variable's source directly, bypassing the production evaluator.</summary>
    /// <param name="function">The owning function.</param>
    /// <param name="node">The authored basic event.</param>
    /// <param name="hazard">The evaluated hazard level.</param>
    /// <param name="percentile">The co-monotonic percentile, or -1 for the mean.</param>
    /// <returns>The event probability.</returns>
    private static double EvaluateOracleSource(FaultTreeResponse function,
        FaultTreeBasicEventNode node, double hazard, double percentile)
    {
        ProbabilitySource source = node.ProbabilitySource;
        if (source.Kind == ProbabilitySourceKind.DeterministicScalar)
            return source.ScalarProbability!.Value;
        if (source.Kind == ProbabilitySourceKind.UncertainTabular)
        {
            OrderedPairedData curve = percentile < 0d
                ? source.Table!.CurveSample()
                : source.Table!.CurveSample(percentile);
            int hazardIndex = -1;
            for (int i = 0; i < function.HazardLevels.Count; i++)
            {
                if (function.HazardLevels[i] == hazard) hazardIndex = i;
            }
            return hazardIndex >= 0 ? curve[hazardIndex].Y : curve.GetYFromX(hazard);
        }
        if (source.ResponseFunction is FaultTreeResponse nested)
            return Enumerate(nested, hazard, percentile);
        return percentile < 0d
            ? source.ResponseFunction!.SampleFunction().CDF(hazard)
            : source.ResponseFunction!.SampleFunction(percentile).CDF(hazard);
    }

    /// <summary>One immutable oracle structure node.</summary>
    /// <param name="VariableIndex">The unified variable index, or -1 for gates and constants.</param>
    /// <param name="ConstantState">The constant truth for house events.</param>
    /// <param name="GateType">The gate combination for gate nodes.</param>
    /// <param name="K">The k-of-n threshold for gate nodes.</param>
    /// <param name="Children">The expanded inputs, or null for leaves.</param>
    private sealed record OracleNode(int VariableIndex, bool ConstantState,
        FaultTreeGateType GateType, int K, OracleNode[]? Children);

    #endregion

    /// <summary>Builds a labeled fault-tree response over the common three-knot hazard axis.</summary>
    /// <param name="tree">The authored tree.</param>
    /// <param name="name">The response name.</param>
    /// <returns>The response.</returns>
    private static FaultTreeResponse Response(FaultTree tree, string name = "Verification fault tree")
    {
        return new FaultTreeResponse(new[] { 0d, 1d, 2d }, tree)
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }

    /// <summary>Builds an aligned three-knot uniform table.</summary>
    /// <param name="minimum">The uniform lower bound.</param>
    /// <param name="maximum">The uniform upper bound.</param>
    /// <returns>The aligned uncertainty table.</returns>
    private static UncertainOrderedPairedData UniformTable(double minimum, double maximum)
    {
        return new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Uniform(minimum, maximum)),
                new UncertainOrdinate(1d, new Uniform(minimum, maximum)),
                new UncertainOrdinate(2d, new Uniform(minimum, maximum)),
            }, true, SortOrder.Ascending, false, SortOrder.None,
            UnivariateDistributionType.Uniform);
    }

    /// <summary>
    /// Verifies a feature-rich tree — shared internal and external transfers, an independent
    /// clone, Xor, k-of-n, both house-event states, scalar and aligned-table sources — against
    /// the exhaustive enumeration oracle at every hazard knot, at the mean and at one prepared
    /// median-LHS realization. A single median stratum pins every local and nested percentile at
    /// one half for any seed, so the realization comparison is independent of dimension layout.
    /// Tolerance 1e-13 is floating-point roundoff scale for these expression sizes.
    /// </summary>
    [TestMethod]
    public void Test_FeatureRichTree_EqualsExhaustiveEnumeration()
    {
        var externalTree = new FaultTree();
        externalTree.Add(externalTree.Root.Id, new FaultTreeBasicEventNode("External A",
            new ProbabilitySource(0.22d)));
        externalTree.Add(externalTree.Root.Id, new FaultTreeBasicEventNode("External B",
            new ProbabilitySource(UniformTable(0.3d, 0.4d))));
        FaultTreeResponse external = Response(externalTree, "External enumeration target");

        var tree = new FaultTree();
        var voting = new FaultTreeGateNode("Voting", FaultTreeGateType.KOfN, 2);
        tree.Add(tree.Root.Id, voting);
        tree.Add(voting.Id, new FaultTreeBasicEventNode("V1", new ProbabilitySource(0.3d)));
        tree.Add(voting.Id, new FaultTreeBasicEventNode("V2", new ProbabilitySource(0.4d)));
        tree.Add(voting.Id, new FaultTreeBasicEventNode("V3",
            new ProbabilitySource(UniformTable(0.2d, 0.3d))));

        var exclusive = new FaultTreeGateNode("Exclusive", FaultTreeGateType.Xor);
        tree.Add(tree.Root.Id, exclusive);
        tree.Add(exclusive.Id, new FaultTreeBasicEventNode("X1", new ProbabilitySource(0.25d)));
        tree.Add(exclusive.Id, new FaultTreeBasicEventNode("X2", new ProbabilitySource(0.35d)));

        var housed = new FaultTreeGateNode("Housed", FaultTreeGateType.And);
        tree.Add(tree.Root.Id, housed);
        var repeated = new FaultTreeBasicEventNode("Repeated", new ProbabilitySource(0.45d));
        tree.Add(housed.Id, repeated);
        tree.Add(housed.Id, new FaultTreeHouseEventNode("On", true));
        tree.LinkShared(housed.Id, repeated.Id, "Shared repeat");

        var isolated = new FaultTreeGateNode("Isolated", FaultTreeGateType.And);
        tree.Add(tree.Root.Id, isolated);
        tree.Add(isolated.Id, new FaultTreeHouseEventNode("Off", false));
        tree.Add(isolated.Id, new FaultTreeBasicEventNode("I1", new ProbabilitySource(0.5d)));

        tree.LinkShared(tree.Root.Id, external, external.FaultTree.Root.Id, "External shared");
        tree.LinkIndependent(tree.Root.Id, external, external.FaultTree.Root.Id, "External clone");
        FaultTreeResponse response = Response(tree, "Feature-rich enumeration fixture");
        Assert.IsTrue(response.Validate().IsValid,
            string.Join(" | ", response.Validate().ValidationMessages));

        OrderedPairedData mean = response.SampleResponseFunction();
        for (int h = 0; h < response.HazardLevels.Count; h++)
        {
            Assert.AreEqual(Enumerate(response, response.HazardLevels[h], -1d), mean[h].Y, 1e-13d,
                $"Exhaustive mean parity at hazard index {h}.");
        }

        response.SetupSampler(1, 10_203_611, SamplingScheme.LatinHypercubeMedian);
        OrderedPairedData realization = response.SampleResponseFunction(0);
        for (int h = 0; h < response.HazardLevels.Count; h++)
        {
            Assert.AreEqual(Enumerate(response, response.HazardLevels[h], 0.5d),
                realization[h].Y, 1e-13d,
                $"Median-realization parity at hazard index {h}.");
        }
    }

    /// <summary>
    /// Verifies indexed Latin-hypercube realizations one-for-one against the enumeration oracle
    /// on a tree with exactly one uncertain dimension: the sampled local percentile read back
    /// through <c>SampledPercentile(r, 0)</c> feeds the oracle directly, so every realization is
    /// an exact-parity check rather than a statistical one.
    /// </summary>
    [TestMethod]
    public void Test_SingleUncertainDimension_IndexedRealizationsEqualEnumeration()
    {
        var tree = new FaultTree();
        var block = new FaultTreeGateNode("Block", FaultTreeGateType.And);
        tree.Add(tree.Root.Id, block);
        var repeated = new FaultTreeBasicEventNode("Repeated", new ProbabilitySource(0.4d));
        tree.Add(block.Id, repeated);
        tree.LinkShared(block.Id, repeated.Id, "Shared repeat");
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("Uncertain",
            new ProbabilitySource(UniformTable(0.1d, 0.5d))));
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("Scalar", new ProbabilitySource(0.2d)));
        FaultTreeResponse response = Response(tree, "Single-dimension realization fixture");
        Assert.AreEqual(1, response.SamplingDimensions);

        const int realizations = 64;
        response.SetupSampler(realizations, 10_203_617, SamplingScheme.LatinHypercube);
        for (int r = 0; r < realizations; r++)
        {
            double percentile = response.SampledPercentile(r, 0);
            OrderedPairedData actual = response.SampleResponseFunction(r);
            for (int h = 0; h < response.HazardLevels.Count; h++)
            {
                Assert.AreEqual(Enumerate(response, response.HazardLevels[h], percentile),
                    actual[h].Y, 1e-13d,
                    $"Indexed realization {r} parity at hazard index {h}.");
            }
        }
    }

    /// <summary>
    /// Verifies the closed-form gate pins: a series gate multiplies, a parallel gate complements
    /// the survival product, shared repetition is idempotent while independent repetition
    /// multiplies, a two-input exclusive gate is <c>p1(1-p2)+(1-p1)p2</c>, and identical-input
    /// k-of-n thresholds equal the binomial upper tail. Tolerance 1e-15 covers only expression
    /// roundoff on exact algebra.
    /// </summary>
    [TestMethod]
    public void Test_GateAlgebra_EqualsClosedFormPins()
    {
        // Series: AND over three independent events.
        var seriesTree = new FaultTree();
        seriesTree.Root.GateType = FaultTreeGateType.And;
        seriesTree.Add(seriesTree.Root.Id, new FaultTreeBasicEventNode("S1", new ProbabilitySource(0.3d)));
        seriesTree.Add(seriesTree.Root.Id, new FaultTreeBasicEventNode("S2", new ProbabilitySource(0.5d)));
        seriesTree.Add(seriesTree.Root.Id, new FaultTreeBasicEventNode("S3", new ProbabilitySource(0.7d)));
        Assert.AreEqual(0.3d * 0.5d * 0.7d,
            Response(seriesTree).SampleResponseFunction()[0].Y, 1e-15d);

        // Parallel: OR over the same probabilities.
        var parallelTree = new FaultTree();
        parallelTree.Add(parallelTree.Root.Id, new FaultTreeBasicEventNode("P1", new ProbabilitySource(0.3d)));
        parallelTree.Add(parallelTree.Root.Id, new FaultTreeBasicEventNode("P2", new ProbabilitySource(0.5d)));
        parallelTree.Add(parallelTree.Root.Id, new FaultTreeBasicEventNode("P3", new ProbabilitySource(0.7d)));
        Assert.AreEqual(1d - 0.7d * 0.5d * 0.3d,
            Response(parallelTree).SampleResponseFunction()[0].Y, 1e-15d);

        // Shared repetition is idempotent; independent repetition multiplies.
        const double p = 0.35d;
        Assert.AreEqual(p, RepetitionValue(FaultTreeGateType.And, shared: true), 1e-15d);
        Assert.AreEqual(p * p, RepetitionValue(FaultTreeGateType.And, shared: false), 1e-15d);
        Assert.AreEqual(p, RepetitionValue(FaultTreeGateType.Or, shared: true), 1e-15d);
        Assert.AreEqual(1d - (1d - p) * (1d - p),
            RepetitionValue(FaultTreeGateType.Or, shared: false), 1e-15d);

        // Two-input exclusive disjunction.
        var xorTree = new FaultTree();
        xorTree.Root.GateType = FaultTreeGateType.Xor;
        xorTree.Add(xorTree.Root.Id, new FaultTreeBasicEventNode("X1", new ProbabilitySource(0.25d)));
        xorTree.Add(xorTree.Root.Id, new FaultTreeBasicEventNode("X2", new ProbabilitySource(0.4d)));
        Assert.AreEqual(0.25d * 0.6d + 0.75d * 0.4d,
            Response(xorTree).SampleResponseFunction()[0].Y, 1e-15d);

        // Identical-p k-of-n equals the binomial upper tail for every threshold.
        const double q = 0.3d;
        double[] binomial =
        {
            1d - (1d - q) * (1d - q) * (1d - q),
            3d * q * q * (1d - q) + q * q * q,
            q * q * q,
        };
        for (int k = 1; k <= 3; k++)
        {
            var votingTree = new FaultTree();
            var gate = new FaultTreeGateNode("Voting", FaultTreeGateType.KOfN, k);
            votingTree.Add(votingTree.Root.Id, gate);
            for (int i = 0; i < 3; i++)
            {
                votingTree.Add(gate.Id, new FaultTreeBasicEventNode($"V{i}", new ProbabilitySource(q)));
            }
            Assert.AreEqual(binomial[k - 1],
                Response(votingTree).SampleResponseFunction()[0].Y, 1e-15d,
                $"Binomial tail for k = {k}.");
        }
    }

    /// <summary>Builds and evaluates one two-occurrence repetition tree.</summary>
    /// <param name="gateType">The root combination.</param>
    /// <param name="shared">Whether the repeat is a shared-logical transfer.</param>
    /// <returns>The top-event probability.</returns>
    private static double RepetitionValue(FaultTreeGateType gateType, bool shared)
    {
        var tree = new FaultTree();
        tree.Root.GateType = gateType;
        var basic = new FaultTreeBasicEventNode("A", new ProbabilitySource(0.35d));
        tree.Add(tree.Root.Id, basic);
        if (shared) tree.LinkShared(tree.Root.Id, basic.Id, "Repeat");
        else tree.LinkIndependent(tree.Root.Id, basic.Id, "Repeat");
        return Response(tree).SampleResponseFunction()[0].Y;
    }

    /// <summary>
    /// Verifies a repeated-event bridge-style tree — four minimal paths over five shared
    /// components — against hand inclusion–exclusion: <c>P(∪Ci) = Σ|single| − Σ|pairs| +
    /// Σ|triples| − |quad|</c>, where every term is the product over the union of its member
    /// variables because shared events are idempotent. Tolerance 1e-13 is roundoff scale for the
    /// fifteen-term alternating sum.
    /// </summary>
    [TestMethod]
    public void Test_BridgeTree_EqualsHandInclusionExclusion()
    {
        double[] probabilities = { 0.3d, 0.4d, 0.5d, 0.2d, 0.35d };
        var tree = new FaultTree();
        var first = new FaultTreeGateNode("Path AB", FaultTreeGateType.And);
        var second = new FaultTreeGateNode("Path CD", FaultTreeGateType.And);
        var third = new FaultTreeGateNode("Path AED", FaultTreeGateType.And);
        var fourth = new FaultTreeGateNode("Path CEB", FaultTreeGateType.And);
        tree.Add(tree.Root.Id, first);
        tree.Add(tree.Root.Id, second);
        tree.Add(tree.Root.Id, third);
        tree.Add(tree.Root.Id, fourth);
        var a = new FaultTreeBasicEventNode("A", new ProbabilitySource(probabilities[0]));
        var b = new FaultTreeBasicEventNode("B", new ProbabilitySource(probabilities[1]));
        var c = new FaultTreeBasicEventNode("C", new ProbabilitySource(probabilities[2]));
        var d = new FaultTreeBasicEventNode("D", new ProbabilitySource(probabilities[3]));
        var e = new FaultTreeBasicEventNode("E", new ProbabilitySource(probabilities[4]));
        tree.Add(first.Id, a);
        tree.Add(first.Id, b);
        tree.Add(second.Id, c);
        tree.Add(second.Id, d);
        tree.LinkShared(third.Id, a.Id, "A again");
        tree.Add(third.Id, e);
        tree.LinkShared(third.Id, d.Id, "D again");
        tree.LinkShared(fourth.Id, c.Id, "C again");
        tree.LinkShared(fourth.Id, e.Id, "E again");
        tree.LinkShared(fourth.Id, b.Id, "B again");
        FaultTreeResponse response = Response(tree, "Bridge fixture");

        int[][] paths = { new[] { 0, 1 }, new[] { 2, 3 }, new[] { 0, 4, 3 }, new[] { 2, 4, 1 } };
        double expected = 0d;
        for (int subset = 1; subset < 1 << paths.Length; subset++)
        {
            var union = new HashSet<int>();
            int members = 0;
            for (int i = 0; i < paths.Length; i++)
            {
                if ((subset & (1 << i)) == 0) continue;
                members++;
                union.UnionWith(paths[i]);
            }
            double term = 1d;
            foreach (int variable in union) term *= probabilities[variable];
            expected += (members % 2 == 1 ? 1d : -1d) * term;
        }

        Assert.AreEqual(expected, response.SampleResponseFunction()[0].Y, 1e-13d);
        Assert.AreEqual(Enumerate(response, 0d, -1d),
            response.SampleResponseFunction()[0].Y, 1e-13d);
    }

    /// <summary>
    /// Verifies minimal cut sets against hand derivation on <c>OR(AND(A,B), C)</c> — the sets
    /// are exactly <c>{C}</c> and <c>{A,B}</c>, ordered by cardinality — and proves the rare-event
    /// cut-set sum is NOT the probability engine: with large probabilities the sum
    /// <c>pC + pA·pB</c> measurably exceeds the exact union, and the response equals exhaustive
    /// enumeration, not the bound. Non-coherent trees refuse cut sets loudly.
    /// </summary>
    [TestMethod]
    public void Test_MinimalCutSets_MatchHandDerivation_NotTheProbabilityEngine()
    {
        const double pA = 0.6d;
        const double pB = 0.7d;
        const double pC = 0.5d;
        var tree = new FaultTree();
        var joint = new FaultTreeGateNode("Joint", FaultTreeGateType.And);
        tree.Add(tree.Root.Id, joint);
        tree.Add(joint.Id, new FaultTreeBasicEventNode("A", new ProbabilitySource(pA)));
        tree.Add(joint.Id, new FaultTreeBasicEventNode("B", new ProbabilitySource(pB)));
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("C", new ProbabilitySource(pC)));
        FaultTreeResponse response = Response(tree, "Cut-set fixture");

        IReadOnlyList<FaultTreeCutSet> cutSets = response.GetMinimalCutSets();
        Assert.AreEqual(2, cutSets.Count);
        Assert.AreEqual(1, cutSets[0].Events.Count);
        Assert.AreEqual("C", cutSets[0].Events[0].Name);
        Assert.AreEqual(2, cutSets[1].Events.Count);
        CollectionAssert.AreEquivalent(new[] { "A", "B" },
            cutSets[1].Events.Select(member => member.Name).ToArray());

        double exact = 1d - (1d - pC) * (1d - pA * pB);
        double rareEventSum = pC + pA * pB;
        double production = response.SampleResponseFunction()[0].Y;
        Assert.AreEqual(exact, production, 1e-15d,
            "The response must equal the exact union probability.");
        Assert.AreEqual(Enumerate(response, 0d, -1d), production, 1e-13d);
        Assert.IsTrue(Math.Abs(rareEventSum - exact) > 0.2d,
            $"The fixture must separate the rare-event bound {rareEventSum:G17} from the exact " +
            $"value {exact:G17} by a macroscopic margin.");
        Assert.IsTrue(Math.Abs(production - rareEventSum) > 0.2d,
            "The response must not degrade to the rare-event cut-set approximation.");

        var exclusiveTree = new FaultTree();
        exclusiveTree.Root.GateType = FaultTreeGateType.Xor;
        exclusiveTree.Add(exclusiveTree.Root.Id, new FaultTreeBasicEventNode("X1", new ProbabilitySource(0.2d)));
        exclusiveTree.Add(exclusiveTree.Root.Id, new FaultTreeBasicEventNode("X2", new ProbabilitySource(0.3d)));
        FaultTreeResponse exclusive = Response(exclusiveTree, "Non-coherent fixture");
        InvalidOperationException error = Assert.ThrowsException<InvalidOperationException>(
            () => exclusive.GetMinimalCutSets());
        StringAssert.Contains(error.Message, "not defined for a non-coherent fault tree");
    }

    /// <summary>
    /// Verifies referenced sources — an ordinary tabular fragility, a nested event tree, and a
    /// nested fault tree — against manually inlined equivalents at every knot, and both
    /// serialization modes against the live model with a resolver re-attaching the stored
    /// instances. The referenced tabular curve is evaluated at the caller's hazards through its
    /// own sampled function, exactly as production does.
    /// </summary>
    [TestMethod]
    public void Test_ReferencedSources_EqualManualInlines_AcrossModes()
    {
        var tabular = new TabularResponse
        {
            Name = "Referenced fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0d, new Deterministic(0.1d)),
                    new UncertainOrdinate(2d, new Deterministic(0.5d)),
                }, true, SortOrder.Ascending, false, SortOrder.None,
                UnivariateDistributionType.Deterministic),
        };

        var eventTree = new EventTree();
        var load = new ChanceNode("Load", new ProbabilitySource(0.6d)) { IsFailure = false };
        eventTree.Add(eventTree.Root.Id, load);
        eventTree.Add(load.Id, new ChanceNode("Load failure", new ProbabilitySource(0.25d)));
        eventTree.Add(load.Id, new RemainderNode("Load survival") { IsFailure = false });
        eventTree.Add(eventTree.Root.Id, new RemainderNode("No load") { IsFailure = false });
        var nestedEvent = new EventTreeResponse(new[] { 0d, 1d, 2d }, eventTree)
        {
            Name = "Referenced event tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
        const double eventAggregate = 0.6d * 0.25d;

        var nestedFaultTree = new FaultTree();
        nestedFaultTree.Root.GateType = FaultTreeGateType.And;
        nestedFaultTree.Add(nestedFaultTree.Root.Id,
            new FaultTreeBasicEventNode("N1", new ProbabilitySource(0.5d)));
        nestedFaultTree.Add(nestedFaultTree.Root.Id,
            new FaultTreeBasicEventNode("N2", new ProbabilitySource(0.4d)));
        FaultTreeResponse nestedFault = Response(nestedFaultTree, "Referenced fault tree");
        const double faultAggregate = 0.5d * 0.4d;

        var tree = new FaultTree();
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("Tabular",
            new ProbabilitySource(tabular)));
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("Event",
            new ProbabilitySource((IResponseFunction)nestedEvent)));
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("Fault",
            new ProbabilitySource((IResponseFunction)nestedFault)));
        FaultTreeResponse response = Response(tree, "Referenced-source fixture");

        OrderedPairedData actual = response.SampleResponseFunction();
        for (int h = 0; h < response.HazardLevels.Count; h++)
        {
            double tabularValue = tabular.SampleFunction().CDF(response.HazardLevels[h]);
            double expected = 1d - (1d - tabularValue) * (1d - eventAggregate) * (1d - faultAggregate);
            Assert.AreEqual(expected, actual[h].Y, 1e-14d, $"Inlined parity at hazard index {h}.");
        }

        var byId = new Dictionary<Guid, IRiskFunction>
        {
            [tabular.Id] = tabular,
            [nestedEvent.Id] = nestedEvent,
            [nestedFault.Id] = nestedFault,
        };
        IRiskFunctionResolver resolver = new RiskFunctionResolver(
            id => byId.TryGetValue(id, out IRiskFunction? function) ? function : null, _ => null);
        var selfContained = new FaultTreeResponse(
            response.ToXElement(RiskSerializationMode.SelfContained));
        var byReference = new FaultTreeResponse(
            response.ToXElement(RiskSerializationMode.ByReference), resolver);
        CollectionAssert.AreEqual(response.CanonicalHash(), selfContained.CanonicalHash());
        CollectionAssert.AreEqual(response.CanonicalHash(), byReference.CanonicalHash());
        for (int h = 0; h < response.HazardLevels.Count; h++)
        {
            Assert.AreEqual(actual[h].Y, selfContained.SampleResponseFunction()[h].Y, 0d);
            Assert.AreEqual(actual[h].Y, byReference.SampleResponseFunction()[h].Y, 0d);
        }
    }

    /// <summary>
    /// Verifies referenced-source realizations one-for-one against an independently prepared
    /// child: the production setup clone is seeded from the variable's content identity and
    /// canonical ordinal, so an isolated clone of the referenced table prepared with the same
    /// derived seed must reproduce every indexed curve exactly.
    /// </summary>
    [TestMethod]
    public void Test_ReferencedSourceRealizations_EqualIndependentChildSamples()
    {
        var child = new TabularResponse
        {
            Name = "Uncertain referenced fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0d, new Uniform(0.05d, 0.25d)),
                    new UncertainOrdinate(2d, new Uniform(0.4d, 0.8d)),
                }, true, SortOrder.Ascending, false, SortOrder.None,
                UnivariateDistributionType.Uniform),
        };
        var tree = new FaultTree();
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("Referenced",
            new ProbabilitySource(child)));
        FaultTreeResponse response = Response(tree, "Referenced realization fixture");

        const int realizations = 64;
        const int seed = 10_203_641;
        response.SetupSampler(realizations, seed, SamplingScheme.LatinHypercube);

        string contentToken = response.GetOccurrencePlan().Variables[0].ContentToken;
        int derivedSeed = SeedHelpers.HashCombine(seed, Convert.FromHexString(contentToken), 0);
        IResponseFunction independent = RiskFunctionFactory.CreateResponseFunction(child.ToXElement())!;
        independent.SetupSampler(realizations, derivedSeed, SamplingScheme.LatinHypercube);

        for (int r = 0; r < realizations; r++)
        {
            IUnivariateDistribution childSample = independent.SampleFunction(r);
            OrderedPairedData actual = response.SampleResponseFunction(r);
            for (int h = 0; h < response.HazardLevels.Count; h++)
            {
                Assert.AreEqual(childSample.CDF(response.HazardLevels[h]), actual[h].Y, 0d,
                    $"Independent child realization {r} at hazard index {h}.");
            }
        }
    }

    /// <summary>
    /// Verifies internal and external shared and independent transfers against explicit
    /// expansions: shared external occurrences unify to one copy of the target algebra while
    /// independent occurrences multiply survival, and each equals a hand-built explicit twin.
    /// </summary>
    [TestMethod]
    public void Test_TransferExpansions_EqualExplicitExpansions()
    {
        var externalTree = new FaultTree();
        externalTree.Add(externalTree.Root.Id, new FaultTreeBasicEventNode("External event",
            new ProbabilitySource(0.35d)));
        FaultTreeResponse external = Response(externalTree, "Transfer target");

        var sharedTree = new FaultTree();
        sharedTree.LinkShared(sharedTree.Root.Id, external, external.FaultTree.Root.Id, "Use A");
        sharedTree.LinkShared(sharedTree.Root.Id, external, external.FaultTree.Root.Id, "Use B");
        Assert.AreEqual(0.35d,
            Response(sharedTree, "Shared pair").SampleResponseFunction()[0].Y, 1e-15d,
            "Two shared occurrences must contribute the target once.");

        var independentTree = new FaultTree();
        independentTree.LinkIndependent(independentTree.Root.Id, external,
            external.FaultTree.Root.Id, "Clone A");
        independentTree.LinkIndependent(independentTree.Root.Id, external,
            external.FaultTree.Root.Id, "Clone B");
        double independentValue =
            Response(independentTree, "Independent pair").SampleResponseFunction()[0].Y;
        Assert.AreEqual(1d - (1d - 0.35d) * (1d - 0.35d), independentValue, 1e-15d,
            "Two independent occurrences must multiply survival.");

        var explicitTree = new FaultTree();
        explicitTree.Add(explicitTree.Root.Id, new FaultTreeBasicEventNode("Copy A",
            new ProbabilitySource(0.35d)));
        explicitTree.Add(explicitTree.Root.Id, new FaultTreeBasicEventNode("Copy B",
            new ProbabilitySource(0.35d)));
        Assert.AreEqual(Response(explicitTree, "Explicit twin").SampleResponseFunction()[0].Y,
            independentValue, 1e-15d,
            "The independent expansion must equal the hand-built explicit twin.");

        var internalTree = new FaultTree();
        var block = new FaultTreeGateNode("Block", FaultTreeGateType.And);
        internalTree.Add(internalTree.Root.Id, block);
        var basic = new FaultTreeBasicEventNode("Local", new ProbabilitySource(0.45d));
        internalTree.Add(block.Id, basic);
        internalTree.LinkShared(block.Id, basic.Id, "Local shared");
        internalTree.LinkIndependent(internalTree.Root.Id, block.Id, "Local clone");
        double expected = 1d - (1d - 0.45d) * (1d - 0.45d);
        Assert.AreEqual(expected,
            Response(internalTree, "Internal mix").SampleResponseFunction()[0].Y, 1e-15d,
            "AND(A, shared A) collapses to A, and the independent block clone multiplies survival.");
    }

    /// <summary>
    /// Verifies graph-connected risk equivalence: a deterministic fault-tree response inside a
    /// component graph produces the same mean risk as a tabular response pinned to the same
    /// fragility ordinates. The parallel-gate ordinates are computed in the diagram's own
    /// symmetric Shannon form, so both responses expose bit-identical sampled functions and the
    /// hazard/consequence integration — which lives entirely outside the tree — returns equal
    /// annualized failure probability and expected consequences to quadrature exactness.
    /// </summary>
    [TestMethod]
    public void Test_GraphConnectedFaultResponse_EqualsTabularResponseRisk()
    {
        double[] stages = { 0d, 15d, 30d };
        double[] eventProbabilities = { 0.02d, 0.3d, 0.6d };
        var faultTree = new FaultTree();
        faultTree.Add(faultTree.Root.Id, new FaultTreeBasicEventNode("Mechanism A",
            new ProbabilitySource(DeterministicTable(stages, eventProbabilities))));
        faultTree.Add(faultTree.Root.Id, new FaultTreeBasicEventNode("Mechanism B",
            new ProbabilitySource(DeterministicTable(stages, eventProbabilities))));
        var fault = new FaultTreeResponse(stages, faultTree)
        {
            Name = "Graph-connected fault tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };

        var unionOrdinates = new double[stages.Length];
        for (int i = 0; i < stages.Length; i++)
        {
            double p = eventProbabilities[i];
            unionOrdinates[i] = p + (1d - p) * p;
        }
        var tabular = new TabularResponse
        {
            Name = "Equivalent tabular fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = DeterministicTable(stages, unionOrdinates),
            // The tree responses expose their curves through the sampled-function default
            // Normal-Z probability interpolation; the tabular twin adopts the same transform so
            // both responses present bit-identical functions between the shared knots.
            ProbabilityTransform = Transform.NormalZ,
        };

        OrderedPairedData faultCurve = fault.SampleResponseFunction();
        for (int i = 0; i < stages.Length; i++)
        {
            Assert.AreEqual(unionOrdinates[i], faultCurve[i].Y, 0d,
                "The symmetric Shannon ordinates must match the diagram bit-for-bit.");
        }

        RiskAnalysis faultAnalysis = RunMeanOnly(BuildRiskComponent(fault, "Fault component"));
        RiskAnalysis tabularAnalysis = RunMeanOnly(BuildRiskComponent(tabular, "Tabular component"));
        var faultOutput = faultAnalysis.MeanRiskResults!.Components[0].Curves.Fail;
        var tabularOutput = tabularAnalysis.MeanRiskResults!.Components[0].Curves.Fail;
        Assert.AreEqual(tabularOutput.MassBalance, faultOutput.MassBalance, 0d,
            "Identical fragility functions must integrate to identical mass balances.");
        Assert.AreEqual(tabularOutput.Mean, faultOutput.Mean, 0d,
            "Identical fragility functions must produce identical mean consequences.");
        Assert.AreEqual(tabularOutput.CumulativeFailureProbabilities[0],
            faultOutput.CumulativeFailureProbabilities[0], 0d,
            "Identical fragility functions must produce identical annualized failure probability.");
    }

    /// <summary>Builds an aligned deterministic table.</summary>
    /// <param name="hazards">The hazard knots.</param>
    /// <param name="probabilities">The deterministic ordinates.</param>
    /// <returns>The aligned table.</returns>
    private static UncertainOrderedPairedData DeterministicTable(double[] hazards,
        double[] probabilities)
    {
        return new UncertainOrderedPairedData(
            hazards.Select((hazard, index) =>
                new UncertainOrdinate(hazard, new Deterministic(probabilities[index]))).ToArray(),
            true, SortOrder.Ascending, false, SortOrder.None,
            UnivariateDistributionType.Deterministic);
    }

    /// <summary>Builds one hazard/response/consequence component around a response function.</summary>
    /// <param name="response">The response function.</param>
    /// <param name="name">The component name.</param>
    /// <returns>The graph-owned component.</returns>
    private static SystemComponent BuildRiskComponent(IResponseFunction response, string name)
    {
        var hazardFunction = new TabularHazard
        {
            Name = "Stage frequency",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            NoUncertaintyFunction = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0.999d, new Deterministic(0d)),
                    new UncertainOrdinate(0.5d, new Deterministic(15d)),
                    new UncertainOrdinate(0.001d, new Deterministic(30d)),
                }, true, SortOrder.Descending, true, SortOrder.Ascending,
                UnivariateDistributionType.Deterministic),
        };
        var component = new SystemComponent(hazardFunction) { Name = name };
        HazardElement hazard = component.Graph.GetElements<HazardElement>().Single();
        var responseElement = new ResponseElement("Response")
        {
            Function = response,
            Input = new RiskConnection(hazard),
        };
        component.Graph.AddElement(responseElement);
        var consequence = new ConsequenceElement("Consequence")
        {
            Input = new RiskConnection(responseElement),
        };
        consequence.Functions.Add(new TabularConsequence
        {
            Name = name + " damage",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Damage",
            ConsequenceUnit = "$",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0d, new Deterministic(0d)),
                    new UncertainOrdinate(30d, new Deterministic(1000d)),
                }, true, SortOrder.Ascending, false, SortOrder.None,
                UnivariateDistributionType.Deterministic),
        });
        component.Graph.AddElement(consequence);
        return component;
    }

    /// <summary>Runs one default mean-only analysis.</summary>
    /// <param name="component">The configured component.</param>
    /// <returns>The estimated analysis.</returns>
    private static RiskAnalysis RunMeanOnly(SystemComponent component)
    {
        var analysis = new RiskAnalysis(new[] { component });
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated, "The mean-only run must estimate.");
        return analysis;
    }

    /// <summary>
    /// Verifies the decision-diagram resource budget diagnostics: a deliberately tiny
    /// <c>BddNodeLimit</c> produces the loud validation failure carrying the observed count, the
    /// configured limit, and the exactness-preserving remediation guidance, and restoring the
    /// default limit restores exact enumeration parity.
    /// </summary>
    [TestMethod]
    public void Test_BddBudget_FailsLoudlyAndRestoresExactness()
    {
        var tree = new FaultTree();
        var voting = new FaultTreeGateNode("Voting", FaultTreeGateType.KOfN, 2);
        tree.Add(tree.Root.Id, voting);
        for (int i = 0; i < 4; i++)
        {
            tree.Add(voting.Id, new FaultTreeBasicEventNode($"V{i}",
                new ProbabilitySource(0.2d + 0.1d * i)));
        }
        FaultTreeResponse response = Response(tree, "Budget fixture");
        double expected = Enumerate(response, 0d, -1d);
        Assert.AreEqual(expected, response.SampleResponseFunction()[0].Y, 1e-13d);

        response.BddNodeLimit = 2;
        var validation = response.Validate();
        Assert.IsFalse(validation.IsValid);
        string diagnostic = validation.ValidationMessages.Single(message =>
            message.Contains("decision-diagram resource budget"));
        StringAssert.Contains(diagnostic, "BddNodeLimit is 2");
        StringAssert.Contains(diagnostic, "nodes were created");
        StringAssert.Contains(diagnostic, "Exact evaluation is never approximated");
        Assert.ThrowsException<InvalidOperationException>(() => response.SampleResponseFunction());

        response.BddNodeLimit = 1_000_000;
        Assert.IsTrue(response.Validate().IsValid);
        Assert.AreEqual(expected, response.SampleResponseFunction()[0].Y, 1e-13d,
            "Raising the budget must restore exact-enumeration parity.");
    }

    /// <summary>
    /// Verifies the exact importance measures against the exhaustive enumeration oracle on a
    /// shared-transfer tree with a k-of-n vote: every measure of every unified variable —
    /// Birnbaum, criticality, Fussell-Vesely, risk achievement worth, and risk reduction worth
    /// — is reproduced from the oracle's own conditional enumerations with the variable forced
    /// certain and impossible, at the source means and at a co-monotonic percentile.
    /// </summary>
    /// <remarks>
    /// <b>Tolerance derivation:</b> both sides are exact algebra over the same double-precision
    /// source probabilities — the production side through the frozen decision diagram, the
    /// oracle through the 2^V probability-weighted truth sum — so agreement at 1e-13 is
    /// floating-point roundoff scale for these expression sizes; the criticality and worth
    /// ratios compound two such values and carry the same bound relative to their magnitude.
    /// </remarks>
    [TestMethod]
    public void Test_ExactImportance_MatchesExhaustiveEnumeration()
    {
        // Arrange — Or(And(A, C), 2-of-3(B, D, shared C)): C is one unified variable under
        // both gates; sources mix deterministic scalars and an uncertain tabular.
        var table = new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Uniform(0.1d, 0.3d)),
                new UncertainOrdinate(1d, new Uniform(0.2d, 0.4d)),
            }, true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Uniform);
        var tree = new FaultTree();
        var top = new FaultTreeGateNode("Top", FaultTreeGateType.Or);
        tree.Add(tree.Root.Id, top);
        var left = new FaultTreeGateNode("Left", FaultTreeGateType.And);
        tree.Add(top.Id, left);
        tree.Add(left.Id, new FaultTreeBasicEventNode("A", new ProbabilitySource(0.05d)));
        var sharedC = new FaultTreeBasicEventNode("C", new ProbabilitySource(table));
        tree.Add(left.Id, sharedC);
        var vote = new FaultTreeGateNode("Vote", FaultTreeGateType.KOfN) { K = 2 };
        tree.Add(top.Id, vote);
        tree.Add(vote.Id, new FaultTreeBasicEventNode("B", new ProbabilitySource(0.15d)));
        tree.Add(vote.Id, new FaultTreeBasicEventNode("D", new ProbabilitySource(0.25d)));
        tree.LinkShared(vote.Id, sharedC.Id, "Shared C");
        var response = new FaultTreeResponse(new[] { 0d, 1d }, tree)
        {
            Name = "Importance oracle",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
        Assert.IsTrue(response.Validate().IsValid);

        foreach (double percentile in new[] { -1d, 0.75d })
        {
            // Act
            var options = new FaultTreeImportanceOptions(1d);
            if (percentile >= 0d) options.Percentile = percentile;
            var result = FaultTreeImportance.Compute(response, options);

            // Assert — the baseline against the plain enumeration, then every measure from the
            // oracle's own conditionals.
            double oracleTop = Enumerate(response, 1d, percentile);
            Assert.AreEqual(oracleTop, result.TopEventProbability, 1e-13,
                "The baseline top-event probability must match the exhaustive enumeration.");
            Assert.AreEqual(4, result.Entries.Count, "A, unified C, B, and D.");

            for (int j = 0; j < result.Entries.Count; j++)
            {
                var entry = result.Entries[j];
                double atOne = EnumerateWithOverride(response, 1d, percentile, entry.NodeId, 1d, out double q);
                double atZero = EnumerateWithOverride(response, 1d, percentile, entry.NodeId, 0d, out _);
                Assert.AreEqual(q, entry.BaselineProbability, 1e-13, $"{entry.Name}: baseline probability.");
                Assert.AreEqual(atOne - atZero, entry.Birnbaum, 1e-13, $"{entry.Name}: Birnbaum.");
                Assert.AreEqual((atOne - atZero) * q / oracleTop, entry.Criticality, 1e-12, $"{entry.Name}: criticality.");
                Assert.AreEqual(1d - atZero / oracleTop, entry.FussellVesely, 1e-12, $"{entry.Name}: Fussell-Vesely.");
                Assert.AreEqual(atOne / oracleTop, entry.RiskAchievementWorth, 1e-12 * (atOne / oracleTop), $"{entry.Name}: risk achievement worth.");
                Assert.AreEqual(oracleTop / atZero, entry.RiskReductionWorth, 1e-12 * (oracleTop / atZero), $"{entry.Name}: risk reduction worth.");
            }
        }
    }
}
