using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.FaultTrees;

/// <summary>
/// Fixed-seed generative checks for fault-tree gate algebra, shared-variable unification,
/// transfer materialization, persistence, and canonical-identity properties. Every generated case
/// is checked against an independent exhaustive Boolean-enumeration oracle that walks the authored
/// tree, resolves transfers, and unifies shared variables itself without touching the compiled
/// decision diagram. The generator and bounded shrinker are test-only and add no runtime
/// dependency.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// </remarks>
[TestClass]
public sealed class FaultTreePropertyTests
{
    /// <summary>The fixed generator seeds; 32 cases per seed produce 128 deterministic cases.</summary>
    private static readonly int[] GeneratorSeeds =
    {
        0x10B2_0261,
        0x10B2_0262,
        0x10B2_0263,
        0x10B2_0264,
    };

    /// <summary>The number of generated cases per fixed seed.</summary>
    private const int CasesPerSeed = 32;

    /// <summary>The maximum accepted floating-point difference from exhaustive enumeration.</summary>
    private const double ProbabilityTolerance = 1e-13d;

    /// <summary>The fixed co-monotonic percentile exercised against the enumeration oracle.</summary>
    private const double OraclePercentile = 0.75d;

    /// <summary>
    /// The fixed sampler seed. One median-LHS realization pins every local and nested percentile
    /// at exactly one half, so indexed-realization parity is independent of stratum permutation.
    /// </summary>
    private const int SamplerSeed = 918273;

    /// <summary>The exclusive unified-variable cap that keeps exhaustive enumeration fast.</summary>
    private const int MaximumUniqueVariables = 16;

    /// <summary>
    /// Generates 128 valid small fault trees at four fixed seeds and verifies exhaustive-oracle
    /// parity at the mean, a fixed percentile, and one indexed realization; both persistence
    /// modes; independent-versus-shared transfer materialization; identity invariance; and
    /// compute-content sensitivity. A failure is rerun through a bounded deterministic
    /// subtree/source shrinker and reports the minimized self-contained XML counterexample.
    /// </summary>
    [TestMethod]
    public void Test_FixedSeedGeneratedTrees_SatisfyFaultTreeProperties()
    {
        GeneratedFeature observed = GeneratedFeature.None;
        int caseCount = 0;
        foreach (int seed in GeneratorSeeds)
        {
            for (int caseIndex = 0; caseIndex < CasesPerSeed; caseIndex++)
            {
                GeneratedCase generated = Generate(seed, caseIndex);
                observed |= generated.Features;
                RunWithMinimizedCounterexample(generated);
                caseCount++;
            }
        }

        Assert.AreEqual(GeneratorSeeds.Length * CasesPerSeed, caseCount);
        Assert.AreEqual(GeneratedFeature.All, observed & GeneratedFeature.All,
            $"The fixed generator corpus did not exercise every required feature. Observed: {observed}.");
    }

    /// <summary>Runs every property and reports a deterministically minimized counterexample on failure.</summary>
    /// <param name="generated">The generated case.</param>
    private static void RunWithMinimizedCounterexample(GeneratedCase generated)
    {
        try
        {
            AssertGeneratedProperties(generated.Response);
        }
        catch (Exception original)
        {
            FaultTreeResponse minimized = Minimize(generated.Response);
            string xml = minimized.ToXElement(RiskSerializationMode.SelfContained)
                .ToString(SaveOptions.DisableFormatting);
            Assert.Fail(
                $"Generated fault-tree property failure. Seed={generated.Seed}, " +
                $"case={generated.CaseIndex}, features={generated.Features}. " +
                $"The bounded shrinker deletes subtrees innermost-first with cascade-link policy, " +
                $"then simplifies basic-event sources to scalar 0.5 while the failure remains. " +
                $"Original failure: {original}\n" +
                $"Minimized self-contained counterexample: {xml}");
        }
    }

    /// <summary>Asserts every required property for one generated response.</summary>
    /// <param name="response">The generated response.</param>
    private static void AssertGeneratedProperties(FaultTreeResponse response)
    {
        var validation = response.Validate();
        Assert.IsTrue(validation.IsValid, string.Join(Environment.NewLine, validation.ValidationMessages));
        AssertOracleParity(response);
        AssertPersistenceModes(response);
        AssertMaterializationDirections(response);
        AssertIdentityProperties(response);
    }

    /// <summary>
    /// Asserts mean, fixed-percentile, and one-median-realization parity between the compiled
    /// diagram and the independent exhaustive enumeration, plus unification agreement.
    /// </summary>
    /// <param name="response">The generated response.</param>
    private static void AssertOracleParity(FaultTreeResponse response)
    {
        OracleModel oracle = BuildOracle(response);
        Assert.IsTrue(oracle.Variables.Count < MaximumUniqueVariables,
            $"The generated case produced {oracle.Variables.Count} unique variables; the corpus caps enumeration size.");
        Assert.AreEqual(oracle.Variables.Count, response.CompiledVariableCount,
            "Independent shared-variable unification must agree with the compiled plan.");
        List<FaultTreeBasicEventNode> compiledNodes = response.GetOccurrencePlan().Variables
            .Select(variable => variable.SourceNode).OrderBy(node => node.Id).ToList();
        List<FaultTreeBasicEventNode> oracleNodes = oracle.Variables
            .Select(variable => variable.Node).OrderBy(node => node.Id).ToList();
        for (int i = 0; i < oracleNodes.Count; i++)
            Assert.AreSame(compiledNodes[i], oracleNodes[i],
                "Independent unification must map onto the same authored basic events.");

        OrderedPairedData mean = response.SampleResponseFunction();
        OrderedPairedData percentile = response.SampleResponseFunction(OraclePercentile);
        for (int h = 0; h < response.HazardLevels.Count; h++)
        {
            double hazard = response.HazardLevels[h];
            Assert.AreEqual(Enumerate(oracle, hazard, OracleMode.Mean, 0d), mean[h].Y,
                ProbabilityTolerance, $"Exhaustive mean parity at hazard index {h}.");
            Assert.AreEqual(Enumerate(oracle, hazard, OracleMode.Percentile, OraclePercentile),
                percentile[h].Y, ProbabilityTolerance,
                $"Exhaustive percentile parity at hazard index {h}.");
        }

        response.SetupSampler(1, SamplerSeed, SamplingScheme.LatinHypercubeMedian);
        OrderedPairedData realization = response.SampleResponseFunction(0);
        for (int h = 0; h < response.HazardLevels.Count; h++)
        {
            Assert.AreEqual(Enumerate(oracle, response.HazardLevels[h], OracleMode.MedianRealization, 0d),
                realization[h].Y, ProbabilityTolerance,
                $"Exhaustive indexed-realization parity at hazard index {h}.");
        }
    }

    /// <summary>Asserts self-contained and resolver-backed by-reference round-trip equivalence.</summary>
    /// <param name="response">The source response.</param>
    private static void AssertPersistenceModes(FaultTreeResponse response)
    {
        FaultTreeResponse selfContained = CloneSelfContained(response);
        IReadOnlyList<IRiskFunction> functions = CollectReferencedFunctions(response);
        var byId = functions.GroupBy(function => function.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var byName = functions.Where(function => !string.IsNullOrEmpty(function.Name))
            .GroupBy(function => function.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        IRiskFunctionResolver resolver = new RiskFunctionResolver(
            id => byId.TryGetValue(id, out IRiskFunction? function) ? function : null,
            name => byName.TryGetValue(name, out IRiskFunction? function) ? function : null);
        var byReference = new FaultTreeResponse(
            response.ToXElement(RiskSerializationMode.ByReference), resolver);

        CollectionAssert.AreEqual(response.CanonicalHash(), selfContained.CanonicalHash());
        CollectionAssert.AreEqual(response.CanonicalHash(), byReference.CanonicalHash());
        AssertSamplesEquivalent(response, selfContained, "self-contained round trip");
        AssertSamplesEquivalent(response, byReference, "by-reference round trip");
    }

    /// <summary>
    /// Asserts both materialization directions: replacing independent transfers with explicit
    /// clones preserves every sampled value, while materializing a shared transfer whose target
    /// repeats in the same context deliberately moves the top-event probability.
    /// </summary>
    /// <param name="response">The generated response.</param>
    private static void AssertMaterializationDirections(FaultTreeResponse response)
    {
        if (FindTransfers(response).Any(item => item.Transfer.LinkMode == TreeLinkMode.IndependentClone))
        {
            FaultTreeResponse materialized = CloneSelfContained(response);
            while (true)
            {
                (FaultTreeResponse Function, FaultTreeTransferNode Transfer) independent =
                    FindTransfers(materialized).FirstOrDefault(item =>
                        item.Transfer.LinkMode == TreeLinkMode.IndependentClone);
                if (independent.Transfer == null) break;
                independent.Function.FaultTree.MaterializeLink(independent.Transfer.Id);
            }
            AssertSamplesEquivalent(response, materialized,
                "independent-transfer materialization versus the live transfers");
        }

        FaultTreeResponse sharedClone = CloneSelfContained(response);
        (FaultTreeResponse Function, FaultTreeTransferNode Transfer) repeated =
            FindTransfers(sharedClone).FirstOrDefault(item =>
                item.Transfer.LinkMode == TreeLinkMode.SharedLogicalEvent
                && string.Equals(item.Transfer.Name, RepeatedSharedTransferName, StringComparison.Ordinal));
        if (repeated.Transfer == null) return;

        OrderedPairedData before = sharedClone.SampleResponseFunction();
        repeated.Function.FaultTree.MaterializeLink(repeated.Transfer.Id);
        OrderedPairedData after = sharedClone.SampleResponseFunction();
        double maximumShift = 0d;
        for (int h = 0; h < before.Count; h++)
            maximumShift = Math.Max(maximumShift, Math.Abs(before[h].Y - after[h].Y));
        Assert.IsTrue(maximumShift > 1e-9d,
            "Materializing a shared transfer whose target repeats in the same context must move the top-event probability.");
    }

    /// <summary>Asserts GUID/name/metadata/order invariance and compute-content sensitivity.</summary>
    /// <param name="response">The generated response.</param>
    private static void AssertIdentityProperties(FaultTreeResponse response)
    {
        byte[] hash = response.CanonicalHash();
        XElement remappedXml = response.ToXElement(RiskSerializationMode.SelfContained);
        RegeneratePersistentIds(remappedXml);
        var remapped = new FaultTreeResponse(remappedXml);
        CollectionAssert.AreEqual(hash, remapped.CanonicalHash(), "persistent GUID regeneration");
        AssertSamplesEquivalent(response, remapped, "persistent GUID regeneration");

        FaultTreeResponse metadata = CloneSelfContained(response);
        int ordinal = 0;
        foreach (FaultTreeResponse function in CollectFaultFunctions(metadata))
        {
            function.Name = $"Renamed function {ordinal}";
            function.Description = $"Metadata {ordinal}";
            function.AssignNewId();
            foreach (FaultTreeNodeBase node in function.FaultTree.Nodes)
            {
                node.Name = $"Renamed node {ordinal}";
                node.Description = $"Display metadata {ordinal++}";
            }
        }
        ReorderSiblings(metadata);
        CollectionAssert.AreEqual(hash, metadata.CanonicalHash(), "metadata/name/order invariance");
        AssertSamplesEquivalent(response, metadata, "metadata/name/order invariance");

        FaultTreeResponse computeEdit = CloneSelfContained(response);
        FaultTreeBasicEventNode changed = CollectFaultFunctions(computeEdit)
            .SelectMany(function => function.FaultTree.Nodes.OfType<FaultTreeBasicEventNode>())
            .First();
        changed.ProbabilitySource = new ProbabilitySource(0.123456789d);
        Assert.IsFalse(hash.AsSpan().SequenceEqual(computeEdit.CanonicalHash()),
            "A compute-relevant probability-source edit must move canonical identity.");
    }

    /// <summary>Asserts two responses sample numerically equivalent mean and percentile curves.</summary>
    /// <param name="expected">The reference response.</param>
    /// <param name="actual">The equivalent response.</param>
    /// <param name="label">The assert label.</param>
    private static void AssertSamplesEquivalent(FaultTreeResponse expected,
        FaultTreeResponse actual, string label)
    {
        OrderedPairedData expectedMean = expected.SampleResponseFunction();
        OrderedPairedData actualMean = actual.SampleResponseFunction();
        Assert.AreEqual(expectedMean.Count, actualMean.Count, label);
        for (int h = 0; h < expectedMean.Count; h++)
            Assert.AreEqual(expectedMean[h].Y, actualMean[h].Y, ProbabilityTolerance, label);

        OrderedPairedData expectedPercentile = expected.SampleResponseFunction(OraclePercentile);
        OrderedPairedData actualPercentile = actual.SampleResponseFunction(OraclePercentile);
        for (int h = 0; h < expectedPercentile.Count; h++)
            Assert.AreEqual(expectedPercentile[h].Y, actualPercentile[h].Y, ProbabilityTolerance, label);
    }

    #region Independent exhaustive oracle

    /// <summary>The oracle sampling mode.</summary>
    private enum OracleMode
    {
        /// <summary>Mean source values.</summary>
        Mean,
        /// <summary>One co-monotonic percentile.</summary>
        Percentile,
        /// <summary>The single median-LHS realization, whose local percentiles are all one half.</summary>
        MedianRealization,
    }

    /// <summary>
    /// Builds the oracle's own expansion of the authored tree: transfers are resolved directly
    /// from their public targets, shared-logical traversal preserves the variable context, and
    /// every independent-clone traversal forks a fresh context, so unification is derived without
    /// consulting the compiled plan.
    /// </summary>
    /// <param name="response">The root response.</param>
    /// <returns>The oracle model.</returns>
    private static OracleModel BuildOracle(FaultTreeResponse response)
    {
        var model = new OracleModel();
        var rootContext = new object();
        model.Root = ExpandOracle(response, response.FaultTree.Root, rootContext, model);
        return model;
    }

    /// <summary>Expands one authored node into the oracle structure.</summary>
    /// <param name="function">The function owning the node.</param>
    /// <param name="node">The authored node.</param>
    /// <param name="context">The current independent-variable context.</param>
    /// <param name="model">The accumulating oracle model.</param>
    /// <returns>The oracle node.</returns>
    private static OracleNode ExpandOracle(FaultTreeResponse function, FaultTreeNodeBase node,
        object context, OracleModel model)
    {
        if (node is FaultTreeTransferNode transfer)
        {
            FaultTreeResponse targetFunction = transfer.TargetFunction ?? function;
            FaultTreeNodeBase targetNode = targetFunction.FaultTree.FindById(transfer.Target.NodeId)
                ?? throw new InvalidOperationException("The oracle could not resolve a transfer target.");
            object targetContext = transfer.LinkMode == TreeLinkMode.IndependentClone
                ? new object()
                : context;
            return ExpandOracle(targetFunction, targetNode, targetContext, model);
        }

        if (node is FaultTreeBasicEventNode basic)
        {
            if (!model.VariableIndex.TryGetValue((basic, context), out int index))
            {
                index = model.Variables.Count;
                model.VariableIndex.Add((basic, context), index);
                model.Variables.Add(new OracleVariable(function, basic));
            }
            return OracleNode.Variable(index);
        }

        if (node is FaultTreeHouseEventNode house) return OracleNode.Constant(house.State);

        var gate = (FaultTreeGateNode)node;
        var children = new OracleNode[node.Children.Count];
        for (int i = 0; i < node.Children.Count; i++)
            children[i] = ExpandOracle(function, node.Children[i], context, model);
        return OracleNode.Gate(gate.GateType, gate.K, children);
    }

    /// <summary>Sums probability-weighted gate truth over every assignment of the unique variables.</summary>
    /// <param name="model">The oracle model.</param>
    /// <param name="hazard">The current hazard value.</param>
    /// <param name="mode">The oracle sampling mode.</param>
    /// <param name="percentile">The co-monotonic percentile when applicable.</param>
    /// <returns>The exhaustive top-event probability.</returns>
    private static double Enumerate(OracleModel model, double hazard, OracleMode mode, double percentile)
    {
        int count = model.Variables.Count;
        var probabilities = new double[count];
        for (int i = 0; i < count; i++)
            probabilities[i] = EvaluateVariable(model.Variables[i], hazard, mode, percentile);

        double sum = 0d;
        double compensation = 0d;
        for (ulong mask = 0; mask < 1UL << count; mask++)
        {
            double weight = 1d;
            for (int i = 0; i < count; i++)
            {
                weight *= (mask & (1UL << i)) != 0 ? probabilities[i] : 1d - probabilities[i];
            }
            if (EvaluateTruth(model.Root!, mask)) AddCompensated(ref sum, ref compensation, weight);
        }
        return sum;
    }

    /// <summary>Evaluates one oracle node's Boolean truth for one variable assignment.</summary>
    /// <param name="node">The oracle node.</param>
    /// <param name="mask">The assignment bit mask.</param>
    /// <returns>The node truth.</returns>
    private static bool EvaluateTruth(OracleNode node, ulong mask)
    {
        if (node.VariableIndex >= 0) return (mask & (1UL << node.VariableIndex)) != 0;
        if (node.Children == null) return node.ConstantState;
        int trueCount = 0;
        for (int i = 0; i < node.Children.Length; i++)
        {
            if (EvaluateTruth(node.Children[i], mask)) trueCount++;
        }
        return node.GateType switch
        {
            FaultTreeGateType.And => trueCount == node.Children.Length,
            FaultTreeGateType.Or => trueCount > 0,
            FaultTreeGateType.Xor => trueCount == 1,
            FaultTreeGateType.KOfN => trueCount >= node.K,
            _ => throw new InvalidOperationException($"Unsupported oracle gate '{node.GateType}'."),
        };
    }

    /// <summary>Evaluates one unified variable's probability without the compiled diagram.</summary>
    /// <param name="variable">The oracle variable.</param>
    /// <param name="hazard">The current hazard value.</param>
    /// <param name="mode">The oracle sampling mode.</param>
    /// <param name="percentile">The co-monotonic percentile when applicable.</param>
    /// <returns>The conditional event probability.</returns>
    private static double EvaluateVariable(OracleVariable variable, double hazard, OracleMode mode,
        double percentile)
    {
        ProbabilitySource source = variable.Node.ProbabilitySource;
        if (source.Kind == ProbabilitySourceKind.DeterministicScalar) return source.ScalarProbability!.Value;

        if (source.Kind == ProbabilitySourceKind.UncertainTabular)
        {
            double effectivePercentile = mode switch
            {
                OracleMode.Mean => -1d,
                OracleMode.Percentile => percentile,
                _ => 0.5d,
            };
            OrderedPairedData curve = effectivePercentile < 0d
                ? source.Table!.CurveSample()
                : source.Table!.CurveSample(effectivePercentile);
            int index = IndexOfHazard(variable.Function, hazard);
            return index >= 0 ? curve[index].Y : curve.GetYFromX(hazard);
        }

        if (source.ResponseFunction is FaultTreeResponse nested)
        {
            return Enumerate(BuildOracle(nested), hazard, mode, percentile);
        }

        IResponseFunction referenced = source.ResponseFunction!;
        if (mode == OracleMode.Mean) return referenced.SampleFunction().CDF(hazard);
        if (mode == OracleMode.Percentile) return referenced.SampleFunction(percentile).CDF(hazard);

        // One median-LHS realization: an isolated clone reproduces the production setup clone
        // because a single median stratum pins every percentile at one half for any seed.
        IResponseFunction clone = RiskFunctionFactory.CreateResponseFunction(referenced.ToXElement())
            ?? throw new InvalidOperationException("The oracle could not clone a referenced response.");
        clone.SetupSampler(1, SamplerSeed, SamplingScheme.LatinHypercubeMedian);
        return clone.SampleFunction(0).CDF(hazard);
    }

    /// <summary>Finds a hazard's aligned index on one owning function's axis.</summary>
    /// <param name="function">The owning function.</param>
    /// <param name="hazard">The hazard value.</param>
    /// <returns>The aligned index, or -1.</returns>
    private static int IndexOfHazard(FaultTreeResponse function, double hazard)
    {
        for (int i = 0; i < function.HazardLevels.Count; i++)
        {
            if (function.HazardLevels[i] == hazard) return i;
        }
        return -1;
    }

    /// <summary>Adds one value with Kahan compensation, independently of production helpers.</summary>
    /// <param name="sum">The running sum.</param>
    /// <param name="compensation">The running compensation.</param>
    /// <param name="value">The value to add.</param>
    private static void AddCompensated(ref double sum, ref double compensation, double value)
    {
        double adjusted = value - compensation;
        double next = sum + adjusted;
        compensation = (next - sum) - adjusted;
        sum = next;
    }

    /// <summary>The oracle's expansion, unified variables, and unification key table.</summary>
    private sealed class OracleModel
    {
        /// <summary>The expanded oracle root.</summary>
        public OracleNode? Root { get; set; }

        /// <summary>The unified variables in oracle discovery order.</summary>
        public List<OracleVariable> Variables { get; } = new List<OracleVariable>();

        /// <summary>The unification table keyed by authored node and independence context.</summary>
        public Dictionary<(FaultTreeBasicEventNode Node, object Context), int> VariableIndex { get; }
            = new Dictionary<(FaultTreeBasicEventNode, object), int>();
    }

    /// <summary>One unified oracle variable: the authored basic event and its owning function.</summary>
    private sealed record OracleVariable(FaultTreeResponse Function, FaultTreeBasicEventNode Node);

    /// <summary>One immutable oracle structure node.</summary>
    private sealed class OracleNode
    {
        /// <summary>The unified variable index, or -1 for gates and constants.</summary>
        public int VariableIndex { get; private init; } = -1;

        /// <summary>The constant truth for house events.</summary>
        public bool ConstantState { get; private init; }

        /// <summary>The gate combination for gate nodes.</summary>
        public FaultTreeGateType GateType { get; private init; }

        /// <summary>The k-of-n threshold for gate nodes.</summary>
        public int K { get; private init; }

        /// <summary>The gate children, or null for leaves.</summary>
        public OracleNode[]? Children { get; private init; }

        /// <summary>Creates a variable leaf.</summary>
        /// <param name="index">The unified variable index.</param>
        /// <returns>The leaf node.</returns>
        public static OracleNode Variable(int index) => new OracleNode { VariableIndex = index };

        /// <summary>Creates a constant leaf.</summary>
        /// <param name="state">The house-event state.</param>
        /// <returns>The leaf node.</returns>
        public static OracleNode Constant(bool state) => new OracleNode { ConstantState = state };

        /// <summary>Creates a gate node.</summary>
        /// <param name="gateType">The Boolean combination.</param>
        /// <param name="k">The k-of-n threshold.</param>
        /// <param name="children">The expanded inputs.</param>
        /// <returns>The gate node.</returns>
        public static OracleNode Gate(FaultTreeGateType gateType, int k, OracleNode[] children)
            => new OracleNode { GateType = gateType, K = k, Children = children };
    }

    #endregion

    #region Deterministic generator

    /// <summary>The display name that marks the deliberately repeated shared transfer.</summary>
    private const string RepeatedSharedTransferName = "Repeated shared occurrence";

    /// <summary>Generates one deterministic valid case from a fixed seed and case index.</summary>
    /// <param name="seed">The fixed corpus seed.</param>
    /// <param name="caseIndex">The case index within the seed.</param>
    /// <returns>The generated response and exercised feature mask.</returns>
    private static GeneratedCase Generate(int seed, int caseIndex)
    {
        var random = new DeterministicRandom(
            ((ulong)(uint)seed << 32) ^ (uint)caseIndex ^ 0x9E3779B97F4A7C15UL);
        var context = new GenerationContext(seed, caseIndex, random);
        var tree = new FaultTree();
        int shape = caseIndex % 3;
        bool carriesTransfers = caseIndex % 4 != 2;
        int groupCount = shape == 2 && !carriesTransfers ? 3 : 2;
        if (shape == 1)
        {
            FaultTreeGateNode parent = tree.Root;
            for (int level = 0; level < 2; level++)
            {
                var next = new FaultTreeGateNode($"Chain {level}", FaultTreeGateType.And);
                tree.Add(parent.Id, next);
                context.Features |= GeneratedFeature.GateAnd;
                AddEvents(tree, next, 1, level, context);
                parent = next;
            }
            AddEvents(tree, parent, 2, 2, context);
        }
        else
        {
            for (int group = 0; group < groupCount; group++)
                AddGateGroup(tree, tree.Root, group, context);
        }

        if (caseIndex % 4 is 0 or 3)
        {
            AddSharedRepeatBlock(tree, context);
        }
        if (caseIndex % 4 is 1 or 3)
        {
            AddExternalTransfers(tree, context);
        }

        FaultTreeResponse response = CreateResponse(tree,
            $"Generated {seed.ToString(CultureInfo.InvariantCulture)}-{caseIndex}");
        context.Features |= GeneratedFeature.BothSerializationModes;
        return new GeneratedCase(seed, caseIndex, response, context.Features);
    }

    /// <summary>Adds one gate group under the requested parent with rotating gate types.</summary>
    /// <param name="tree">The authored tree.</param>
    /// <param name="parent">The receiving parent gate.</param>
    /// <param name="groupIndex">The deterministic group index.</param>
    /// <param name="context">The generation context.</param>
    private static void AddGateGroup(FaultTree tree, FaultTreeGateNode parent, int groupIndex,
        GenerationContext context)
    {
        int rotation = (context.CaseIndex + groupIndex) % 4;
        if (rotation == 0)
        {
            var gate = new FaultTreeGateNode($"And group {groupIndex}", FaultTreeGateType.And);
            tree.Add(parent.Id, gate);
            context.Features |= GeneratedFeature.GateAnd;
            AddEvents(tree, gate, 2, groupIndex, context);
            bool state = ((context.CaseIndex + groupIndex) & 4) == 0;
            tree.Add(gate.Id, new FaultTreeHouseEventNode($"House {groupIndex}", state));
            context.Features |= state
                ? GeneratedFeature.HouseEventTrue : GeneratedFeature.HouseEventFalse;
        }
        else if (rotation == 1)
        {
            var gate = new FaultTreeGateNode($"Or group {groupIndex}", FaultTreeGateType.Or);
            tree.Add(parent.Id, gate);
            context.Features |= GeneratedFeature.GateOr;
            AddEvents(tree, gate, 2, groupIndex, context);
        }
        else if (rotation == 2)
        {
            var gate = new FaultTreeGateNode($"Xor group {groupIndex}", FaultTreeGateType.Xor);
            tree.Add(parent.Id, gate);
            context.Features |= GeneratedFeature.GateXor;
            AddEvents(tree, gate, 2, groupIndex, context);
        }
        else
        {
            int k = (((context.CaseIndex + groupIndex) / 4) % 3) switch { 0 => 1, 1 => 3, _ => 2 };
            var gate = new FaultTreeGateNode($"Vote group {groupIndex}", FaultTreeGateType.KOfN, k);
            tree.Add(parent.Id, gate);
            context.Features |= GeneratedFeature.GateKOfN;
            if (k == 1) context.Features |= GeneratedFeature.KOfNThresholdOne;
            if (k == 3) context.Features |= GeneratedFeature.KOfNThresholdAll;
            AddEvents(tree, gate, 3, groupIndex, context);
        }
    }

    /// <summary>Adds basic events with deterministically rotating probability sources.</summary>
    /// <param name="tree">The authored tree.</param>
    /// <param name="gate">The receiving gate.</param>
    /// <param name="count">The number of events.</param>
    /// <param name="groupIndex">The deterministic group index.</param>
    /// <param name="context">The generation context.</param>
    private static void AddEvents(FaultTree tree, FaultTreeGateNode gate, int count, int groupIndex,
        GenerationContext context)
    {
        for (int i = 0; i < count; i++)
        {
            int ordinal = context.NodeOrdinal++;
            double probability = 0.2d + 0.05d * (ordinal % 9);
            tree.Add(gate.Id, new FaultTreeBasicEventNode($"Event {groupIndex}-{i}",
                CreateProbabilitySource(probability, context)));
        }
    }

    /// <summary>Creates a scalar, aligned uncertain table, or referenced-response source.</summary>
    /// <param name="probability">The desired mean probability.</param>
    /// <param name="context">The generation context.</param>
    /// <returns>The generated probability source.</returns>
    private static ProbabilitySource CreateProbabilitySource(double probability,
        GenerationContext context)
    {
        if (!context.NestedUsed && context.CaseIndex % 8 == 5)
        {
            context.NestedUsed = true;
            context.Features |= GeneratedFeature.ReferencedResponseSource;
            return new ProbabilitySource(CreateNestedFaultResponse(probability, context));
        }
        if (!context.TabularUsed && context.CaseIndex % 8 == 2)
        {
            context.TabularUsed = true;
            context.Features |= GeneratedFeature.ReferencedResponseSource;
            return new ProbabilitySource(CreateReferencedTabularResponse(probability, context));
        }

        if ((context.NodeOrdinal + context.CaseIndex) % 3 == 0)
        {
            double delta = Math.Min(0.04d, Math.Min(probability, 1d - probability) * 0.25d);
            context.Features |= GeneratedFeature.AlignedTableSource;
            return new ProbabilitySource(AlignedTable(probability, delta));
        }

        context.Features |= GeneratedFeature.ScalarSource;
        return new ProbabilitySource(probability);
    }

    /// <summary>Creates an aligned three-knot uncertain table with the requested constant mean.</summary>
    /// <param name="mean">The ordinate mean.</param>
    /// <param name="delta">The symmetric uniform half-range.</param>
    /// <returns>The aligned uncertainty table.</returns>
    private static UncertainOrderedPairedData AlignedTable(double mean, double delta)
    {
        return new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Uniform(mean - delta, mean + delta)),
                new UncertainOrdinate(1d, new Uniform(mean - delta, mean + delta)),
                new UncertainOrdinate(2d, new Uniform(mean - delta, mean + delta)),
            }, true, SortOrder.Ascending, false, SortOrder.None,
            UnivariateDistributionType.Uniform);
    }

    /// <summary>Creates a single-event nested fault-tree response probability source.</summary>
    /// <param name="probability">The nested aggregate mean.</param>
    /// <param name="context">The generation context.</param>
    /// <returns>The nested fault-tree response.</returns>
    private static FaultTreeResponse CreateNestedFaultResponse(double probability,
        GenerationContext context)
    {
        var innerTree = new FaultTree();
        double delta = Math.Min(0.03d, Math.Min(probability, 1d - probability) * 0.2d);
        innerTree.Add(innerTree.Root.Id, new FaultTreeBasicEventNode("Nested event",
            new ProbabilitySource(AlignedTable(probability, delta))));
        context.Features |= GeneratedFeature.AlignedTableSource;
        return CreateResponse(innerTree, $"Nested {context.Seed}-{context.CaseIndex}");
    }

    /// <summary>Creates a deterministic ordinary tabular fragility used as a referenced source.</summary>
    /// <param name="probability">The fragility ordinate at the highest hazard.</param>
    /// <param name="context">The generation context.</param>
    /// <returns>The tabular response.</returns>
    private static TabularResponse CreateReferencedTabularResponse(double probability,
        GenerationContext context)
    {
        return new TabularResponse
        {
            Name = $"Referenced tabular {context.Seed}-{context.CaseIndex}",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0d, new Deterministic(probability * 0.5d)),
                    new UncertainOrdinate(2d, new Deterministic(probability)),
                }, true, SortOrder.Ascending, false, SortOrder.None,
                UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>
    /// Adds the shared-repeat block: one gate that references its own basic event through a
    /// shared transfer, plus an internal independent transfer to the whole block.
    /// </summary>
    /// <param name="tree">The authored tree.</param>
    /// <param name="context">The generation context.</param>
    private static void AddSharedRepeatBlock(FaultTree tree, GenerationContext context)
    {
        var block = new FaultTreeGateNode("Shared repeat block", FaultTreeGateType.And);
        tree.Add(tree.Root.Id, block);
        var target = new FaultTreeBasicEventNode("Repeated shared target", new ProbabilitySource(0.35d));
        tree.Add(block.Id, target);
        tree.LinkShared(block.Id, target.Id, RepeatedSharedTransferName);
        tree.LinkIndependent(tree.Root.Id, block.Id, "Independent internal occurrence");
        context.Features |= GeneratedFeature.GateAnd | GeneratedFeature.ScalarSource
            | GeneratedFeature.SharedInternalTransfer | GeneratedFeature.RepeatedSharedTarget
            | GeneratedFeature.IndependentInternalTransfer;
    }

    /// <summary>Adds shared and independent external transfers to one external fault function.</summary>
    /// <param name="tree">The authored tree.</param>
    /// <param name="context">The generation context.</param>
    private static void AddExternalTransfers(FaultTree tree, GenerationContext context)
    {
        var externalTree = new FaultTree();
        externalTree.Add(externalTree.Root.Id,
            new FaultTreeBasicEventNode("External event A", new ProbabilitySource(0.25d)));
        externalTree.Add(externalTree.Root.Id, new FaultTreeBasicEventNode("External event B",
            new ProbabilitySource(AlignedTable(0.3d, 0.03d))));
        FaultTreeResponse external = CreateResponse(externalTree,
            $"External {context.Seed}-{context.CaseIndex}");

        tree.LinkShared(tree.Root.Id, external, external.FaultTree.Root.Id, "External shared A");
        tree.LinkShared(tree.Root.Id, external, external.FaultTree.Root.Id, "External shared B");
        tree.LinkIndependent(tree.Root.Id, external, external.FaultTree.Root.Id,
            "External independent occurrence");
        context.Features |= GeneratedFeature.ScalarSource | GeneratedFeature.AlignedTableSource
            | GeneratedFeature.SharedExternalTransfer | GeneratedFeature.IndependentExternalTransfer;
    }

    /// <summary>Creates a labeled response on the generated corpus's common aligned hazard axis.</summary>
    /// <param name="tree">The authored tree.</param>
    /// <param name="name">The unique response name.</param>
    /// <returns>The response.</returns>
    private static FaultTreeResponse CreateResponse(FaultTree tree, string name)
    {
        return new FaultTreeResponse(new[] { 0d, 1d, 2d }, tree)
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }

    #endregion

    #region Case plumbing, minimization, and identity helpers

    /// <summary>Clones one response through the headless self-contained persistence path.</summary>
    /// <param name="response">The source response.</param>
    /// <returns>The isolated clone.</returns>
    private static FaultTreeResponse CloneSelfContained(FaultTreeResponse response)
    {
        return new FaultTreeResponse(response.ToXElement(RiskSerializationMode.SelfContained));
    }

    /// <summary>Collects every recursively referenced fault-tree function by reference identity.</summary>
    /// <param name="root">The root response.</param>
    /// <returns>The deterministic depth-first function list.</returns>
    private static IReadOnlyList<FaultTreeResponse> CollectFaultFunctions(FaultTreeResponse root)
    {
        var result = new List<FaultTreeResponse>();
        var visited = new HashSet<FaultTreeResponse>(ReferenceEqualityComparer.Instance);
        Visit(root);
        return result;

        // Visits one recursive fault-tree dependency.
        void Visit(FaultTreeResponse function)
        {
            if (!visited.Add(function)) return;
            result.Add(function);
            foreach (FaultTreeNodeBase node in function.FaultTree.Nodes)
            {
                if (node is FaultTreeBasicEventNode basic
                    && basic.ProbabilitySource.ResponseFunction is FaultTreeResponse nested)
                    Visit(nested);
                if (node is FaultTreeTransferNode transfer && transfer.TargetFunction != null)
                    Visit(transfer.TargetFunction);
            }
        }
    }

    /// <summary>Collects every function a by-reference resolver must supply.</summary>
    /// <param name="root">The root response.</param>
    /// <returns>The fault functions plus every ordinary referenced response.</returns>
    private static IReadOnlyList<IRiskFunction> CollectReferencedFunctions(FaultTreeResponse root)
    {
        var result = new List<IRiskFunction>();
        foreach (FaultTreeResponse function in CollectFaultFunctions(root))
        {
            result.Add(function);
            foreach (FaultTreeBasicEventNode basic in
                function.FaultTree.Nodes.OfType<FaultTreeBasicEventNode>())
            {
                if (basic.ProbabilitySource.ResponseFunction is IRiskFunction referenced
                    && referenced is not FaultTreeResponse)
                    result.Add(referenced);
            }
        }
        return result;
    }

    /// <summary>Finds every authored transfer with its owning function.</summary>
    /// <param name="root">The root response.</param>
    /// <returns>The transfers in deterministic function/node order.</returns>
    private static IReadOnlyList<(FaultTreeResponse Function, FaultTreeTransferNode Transfer)>
        FindTransfers(FaultTreeResponse root)
    {
        var result = new List<(FaultTreeResponse, FaultTreeTransferNode)>();
        foreach (FaultTreeResponse function in CollectFaultFunctions(root))
        {
            foreach (FaultTreeTransferNode transfer in
                function.FaultTree.Nodes.OfType<FaultTreeTransferNode>())
                result.Add((function, transfer));
        }
        return result;
    }

    /// <summary>Reverses one pair of siblings at every eligible generated gate.</summary>
    /// <param name="root">The recursive response root.</param>
    private static void ReorderSiblings(FaultTreeResponse root)
    {
        foreach (FaultTreeResponse function in CollectFaultFunctions(root))
        {
            FaultTreeNodeBase[] parents = function.FaultTree.Nodes.ToArray();
            for (int i = 0; i < parents.Length; i++)
            {
                if (parents[i] is not FaultTreeGateNode gate || gate.Children.Count < 2) continue;
                function.FaultTree.Move(gate.Children[^1].Id, gate.Id, gate.Children[0].Id);
            }
        }
    }

    /// <summary>Regenerates every serialized function/node GUID and matching reference.</summary>
    /// <param name="xml">The self-contained response XML.</param>
    private static void RegeneratePersistentIds(XElement xml)
    {
        var map = new Dictionary<Guid, Guid>();
        int ordinal = 1;
        foreach (XAttribute attribute in xml.DescendantsAndSelf().Attributes()
            .Where(attribute => attribute.Name.LocalName == "Id"))
        {
            if (Guid.TryParse(attribute.Value, out Guid oldId) && oldId != Guid.Empty &&
                !map.ContainsKey(oldId))
                map.Add(oldId, DeterministicGuid(ordinal++));
        }

        foreach (XAttribute attribute in xml.DescendantsAndSelf().Attributes())
        {
            if (Guid.TryParse(attribute.Value, out Guid oldId) &&
                map.TryGetValue(oldId, out Guid replacement))
            {
                attribute.Value = replacement.ToString("D", CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>Creates one deterministic non-empty GUID for test-only identity remapping.</summary>
    /// <param name="ordinal">The one-based remap ordinal.</param>
    /// <returns>The deterministic GUID.</returns>
    private static Guid DeterministicGuid(int ordinal)
    {
        byte[] bytes =
        {
            0x10, 0x0B, 0x20, 0x26, 0x52, 0x4D, 0x43, 0x54,
            0, 0, 0, 0, 0, 0, 0, 0,
        };
        BitConverter.GetBytes(ordinal).CopyTo(bytes, 8);
        return new Guid(bytes);
    }

    /// <summary>
    /// Deterministically minimizes a failing case in at most 32 accepted reductions. Each pass
    /// first attempts to delete one subtree with the cascade-link policy, innermost function
    /// first, then simplifies one basic-event source to scalar 0.5. A candidate is retained only
    /// when it remains valid and preserves a property failure.
    /// </summary>
    /// <param name="failing">The original failing response.</param>
    /// <returns>The bounded minimized response.</returns>
    private static FaultTreeResponse Minimize(FaultTreeResponse failing)
    {
        FaultTreeResponse current = CloneSelfContained(failing);
        const int maximumAcceptedReductions = 32;
        for (int reduction = 0; reduction < maximumAcceptedReductions; reduction++)
        {
            bool accepted = false;
            IReadOnlyList<FaultTreeResponse> functions = CollectFaultFunctions(current);
            for (int functionIndex = functions.Count - 1; functionIndex >= 0 && !accepted;
                 functionIndex--)
            {
                Guid[] nodeIds = functions[functionIndex].FaultTree.Nodes
                    .Where(node => node != functions[functionIndex].FaultTree.Root)
                    .Select(node => node.Id).Reverse().ToArray();
                for (int nodeIndex = 0; nodeIndex < nodeIds.Length; nodeIndex++)
                {
                    FaultTreeResponse candidate = CloneSelfContained(current);
                    IReadOnlyList<FaultTreeResponse> candidateFunctions = CollectFaultFunctions(candidate);
                    if (functionIndex >= candidateFunctions.Count) continue;
                    FaultTreeNodeBase? node =
                        candidateFunctions[functionIndex].FaultTree.FindById(nodeIds[nodeIndex]);
                    if (node == null || node == candidateFunctions[functionIndex].FaultTree.Root)
                        continue;
                    try
                    {
                        candidateFunctions[functionIndex].FaultTree.Delete(node.Id,
                            TreeDeletePolicy.CascadeLinks);
                    }
                    catch (InvalidOperationException)
                    {
                        continue;
                    }
                    if (!IsValidFailingCandidate(candidate)) continue;
                    current = candidate;
                    accepted = true;
                    break;
                }
            }

            if (accepted) continue;
            functions = CollectFaultFunctions(current);
            for (int functionIndex = functions.Count - 1; functionIndex >= 0 && !accepted;
                 functionIndex--)
            {
                Guid[] basicIds = functions[functionIndex].FaultTree.Nodes
                    .OfType<FaultTreeBasicEventNode>()
                    .Where(basic => basic.ProbabilitySource.Kind !=
                        ProbabilitySourceKind.DeterministicScalar ||
                        basic.ProbabilitySource.ScalarProbability != 0.5d)
                    .Select(basic => basic.Id).Reverse().ToArray();
                for (int basicIndex = 0; basicIndex < basicIds.Length; basicIndex++)
                {
                    FaultTreeResponse candidate = CloneSelfContained(current);
                    IReadOnlyList<FaultTreeResponse> candidateFunctions = CollectFaultFunctions(candidate);
                    if (functionIndex >= candidateFunctions.Count) continue;
                    var basic = candidateFunctions[functionIndex].FaultTree
                        .FindById(basicIds[basicIndex]) as FaultTreeBasicEventNode;
                    if (basic == null) continue;
                    basic.ProbabilitySource = new ProbabilitySource(0.5d);
                    if (!IsValidFailingCandidate(candidate)) continue;
                    current = candidate;
                    accepted = true;
                    break;
                }
            }

            if (!accepted) break;
        }
        return current;
    }

    /// <summary>Returns whether one valid candidate still violates at least one property.</summary>
    /// <param name="candidate">The minimized candidate.</param>
    /// <returns><see langword="true"/> only when the candidate is valid and still fails.</returns>
    private static bool IsValidFailingCandidate(FaultTreeResponse candidate)
    {
        if (!candidate.Validate().IsValid) return false;
        try
        {
            AssertGeneratedProperties(candidate);
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>The required generated-corpus feature coverage.</summary>
    [Flags]
    private enum GeneratedFeature
    {
        /// <summary>No observed feature.</summary>
        None = 0,
        /// <summary>An And gate.</summary>
        GateAnd = 1 << 0,
        /// <summary>An Or gate.</summary>
        GateOr = 1 << 1,
        /// <summary>A two-input Xor gate.</summary>
        GateXor = 1 << 2,
        /// <summary>A k-of-n gate.</summary>
        GateKOfN = 1 << 3,
        /// <summary>A degenerate k-of-n threshold of one.</summary>
        KOfNThresholdOne = 1 << 4,
        /// <summary>A degenerate k-of-n threshold equal to the input count.</summary>
        KOfNThresholdAll = 1 << 5,
        /// <summary>A house event held true.</summary>
        HouseEventTrue = 1 << 6,
        /// <summary>A house event held false.</summary>
        HouseEventFalse = 1 << 7,
        /// <summary>An internal shared-logical transfer.</summary>
        SharedInternalTransfer = 1 << 8,
        /// <summary>An external shared-logical transfer.</summary>
        SharedExternalTransfer = 1 << 9,
        /// <summary>An internal independent-clone transfer.</summary>
        IndependentInternalTransfer = 1 << 10,
        /// <summary>An external independent-clone transfer.</summary>
        IndependentExternalTransfer = 1 << 11,
        /// <summary>The same target referenced twice in one variable context.</summary>
        RepeatedSharedTarget = 1 << 12,
        /// <summary>A deterministic scalar source.</summary>
        ScalarSource = 1 << 13,
        /// <summary>An aligned uncertain-table source.</summary>
        AlignedTableSource = 1 << 14,
        /// <summary>A referenced response-function source.</summary>
        ReferencedResponseSource = 1 << 15,
        /// <summary>Both persistence modes were exercised.</summary>
        BothSerializationModes = 1 << 16,
        /// <summary>Every required generated feature.</summary>
        All = GateAnd | GateOr | GateXor | GateKOfN | KOfNThresholdOne | KOfNThresholdAll |
            HouseEventTrue | HouseEventFalse | SharedInternalTransfer | SharedExternalTransfer |
            IndependentInternalTransfer | IndependentExternalTransfer | RepeatedSharedTarget |
            ScalarSource | AlignedTableSource | ReferencedResponseSource | BothSerializationModes,
    }

    /// <summary>One deterministic generated case and its feature ledger.</summary>
    private sealed record GeneratedCase(int Seed, int CaseIndex, FaultTreeResponse Response,
        GeneratedFeature Features);

    /// <summary>Mutable state shared while generating one deterministic case.</summary>
    private sealed class GenerationContext
    {
        /// <summary>Initializes one generation context.</summary>
        /// <param name="seed">The corpus seed.</param>
        /// <param name="caseIndex">The case index.</param>
        /// <param name="random">The deterministic random stream.</param>
        public GenerationContext(int seed, int caseIndex, DeterministicRandom random)
        {
            Seed = seed;
            CaseIndex = caseIndex;
            Random = random;
        }

        /// <summary>The corpus seed.</summary>
        public int Seed { get; }
        /// <summary>The case index.</summary>
        public int CaseIndex { get; }
        /// <summary>The deterministic random stream.</summary>
        public DeterministicRandom Random { get; }
        /// <summary>The observed feature mask.</summary>
        public GeneratedFeature Features { get; set; }
        /// <summary>The next node ordinal.</summary>
        public int NodeOrdinal { get; set; }
        /// <summary>Whether this case already contains its bounded nested fault source.</summary>
        public bool NestedUsed { get; set; }
        /// <summary>Whether this case already contains its bounded referenced tabular source.</summary>
        public bool TabularUsed { get; set; }
    }

    /// <summary>A tiny deterministic SplitMix64 stream used only to generate test structures.</summary>
    private sealed class DeterministicRandom
    {
        private ulong _state;

        /// <summary>Initializes the stream.</summary>
        /// <param name="seed">The fixed seed.</param>
        public DeterministicRandom(ulong seed) => _state = seed;

        /// <summary>Returns a nonnegative integer less than <paramref name="exclusiveMaximum"/>.</summary>
        /// <param name="exclusiveMaximum">The exclusive upper bound.</param>
        /// <returns>The deterministic value.</returns>
        public int Next(int exclusiveMaximum)
        {
            if (exclusiveMaximum <= 0)
                throw new ArgumentOutOfRangeException(nameof(exclusiveMaximum));
            _state += 0x9E3779B97F4A7C15UL;
            ulong value = _state;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            value ^= value >> 31;
            return (int)(value % (uint)exclusiveMaximum);
        }
    }

    #endregion
}
