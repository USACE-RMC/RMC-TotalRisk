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
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.EventTrees;

/// <summary>
/// Fixed-seed generative checks for event-tree mass, recursive-oracle, link, persistence, and
/// canonical-identity properties. The generator and bounded shrinker are test-only and add no
/// runtime dependency.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// </remarks>
[TestClass]
public sealed class EventTreePropertyTests
{
    /// <summary>The fixed generator seeds; 32 cases per seed produce 128 deterministic cases.</summary>
    private static readonly int[] GeneratorSeeds =
    {
        0x10A2_0261,
        0x10A2_0262,
        0x10A2_0263,
        0x10A2_0264,
    };

    /// <summary>The number of generated cases per fixed seed.</summary>
    private const int CasesPerSeed = 32;

    /// <summary>The maximum accepted floating-point difference from independent recursion.</summary>
    private const double ProbabilityTolerance = 1e-13d;

    /// <summary>
    /// Generates 128 valid small event trees at four fixed seeds and verifies mass conservation,
    /// aggregate/leaf parity, an independent recursive oracle, link materialization equivalence,
    /// both persistence modes, identity invariance, and compute-content sensitivity. A failure is
    /// rerun through a bounded deterministic branch/subtree/source shrinker and reports the
    /// minimized self-contained XML counterexample.
    /// </summary>
    [TestMethod]
    public void Test_FixedSeedGeneratedTrees_SatisfyCorpusInvariants()
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

    /// <summary>
    /// Rebuilds every generated case as a shared-logical twin — every link mode rewritten in the
    /// serialized form — and verifies the invariants sharing must preserve: validity, exhaustive
    /// per-realization branch mass, both persistence modes, bitwise mean-curve equality to the
    /// independent original, and a stable rebuilt identity that moves exactly when a link exists.
    /// </summary>
    [TestMethod]
    public void Test_FixedSeedGeneratedTrees_SharedTwinInvariants()
    {
        int linkedTwins = 0;
        foreach (int seed in GeneratorSeeds)
        {
            for (int caseIndex = 0; caseIndex < CasesPerSeed; caseIndex++)
            {
                GeneratedCase generated = Generate(seed, caseIndex);
                XElement rewritten = generated.Response.ToXElement(RiskSerializationMode.SelfContained);
                bool anyLink = false;
                foreach (XElement link in rewritten.Descendants(nameof(EventTreeLinkNode)))
                {
                    link.SetAttributeValue("LinkMode", nameof(TreeLinkMode.SharedLogicalEvent));
                    anyLink = true;
                }
                var twin = new EventTreeResponse(rewritten);
                var twinAgain = new EventTreeResponse(new XElement(rewritten));

                var validation = twin.Validate();
                Assert.IsTrue(validation.IsValid,
                    $"Seed={generated.Seed}, case={generated.CaseIndex}: " +
                    string.Join(Environment.NewLine, validation.ValidationMessages));

                // Sharing never changes a mean value analytically, but the mode attribute is part
                // of each child's identity token and sibling sums evaluate in canonical token
                // order, so flipping every link can reorder a compensated sum at ulp scale.
                OrderedPairedData originalMean = generated.Response.SampleResponseFunction();
                OrderedPairedData twinMean = twin.SampleResponseFunction();
                for (int h = 0; h < originalMean.Count; h++)
                    Assert.AreEqual(originalMean[h].Y, twinMean[h].Y, ProbabilityTolerance,
                        $"Seed={generated.Seed}, case={generated.CaseIndex}: sharing moved the mean curve.");

                string originalHash = Convert.ToHexString(generated.Response.CanonicalHash());
                string twinHash = Convert.ToHexString(twin.CanonicalHash());
                Assert.AreEqual(twinHash, Convert.ToHexString(twinAgain.CanonicalHash()),
                    $"Seed={generated.Seed}, case={generated.CaseIndex}: the shared identity is unstable.");
                if (anyLink)
                {
                    linkedTwins++;
                    Assert.AreNotEqual(originalHash, twinHash,
                        $"Seed={generated.Seed}, case={generated.CaseIndex}: selecting the shared mode must move the hash.");
                }
                else
                {
                    Assert.AreEqual(originalHash, twinHash,
                        $"Seed={generated.Seed}, case={generated.CaseIndex}: a linkless rewrite must be identity-inert.");
                }

                twin.SetupSampler(8, unchecked(seed * 31 + caseIndex), SamplingScheme.LatinHypercube);
                for (int realization = 0; realization < 8; realization++)
                {
                    ResponseBranchSample sample = twin.SampleBranches(realization);
                    for (int h = 0; h < sample.Hazards.Count; h++)
                    {
                        double mass = 0d;
                        for (int branch = 0; branch < sample.Branches.Count; branch++)
                            mass += sample.Probabilities[branch][h];
                        Assert.AreEqual(1d, mass, ProbabilityTolerance,
                            $"Seed={generated.Seed}, case={generated.CaseIndex}: shared branch mass is not exhaustive.");
                    }
                }
                AssertPersistenceModes(twin);
            }
        }
        Assert.IsTrue(linkedTwins >= 32,
            $"The corpus produced only {linkedTwins} linked shared twins; the sharing pass lost its coverage.");
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
            EventTreeResponse minimized = Minimize(generated.Response);
            string xml = minimized.ToXElement(RiskSerializationMode.SelfContained)
                .ToString(SaveOptions.DisableFormatting);
            Assert.Fail(
                $"Generated event-tree property failure. Seed={generated.Seed}, " +
                $"case={generated.CaseIndex}, features={generated.Features}. " +
                $"The bounded shrinker removes branches/subtrees, then simplifies chance sources " +
                $"to scalar 0.5 while the failure remains. Original failure: {original}\n" +
                $"Minimized self-contained counterexample: {xml}");
        }
    }

    /// <summary>Asserts every required property for one generated response.</summary>
    /// <param name="response">The generated response.</param>
    private static void AssertGeneratedProperties(EventTreeResponse response)
    {
        var validation = response.Validate();
        Assert.IsTrue(validation.IsValid, string.Join(Environment.NewLine, validation.ValidationMessages));
        AssertCompiledAgainstRecursiveOracle(response);
        AssertPersistenceModes(response);
        if (CollectFunctions(response).Any(function =>
            function.EventTree.Nodes.Any(node => node is EventTreeLinkNode)))
        {
            AssertMaterializedCloneEquivalent(response);
        }
        AssertIdentityProperties(response);
    }

    /// <summary>Asserts self-contained and resolver-backed by-reference round-trip equivalence.</summary>
    /// <param name="response">The source response.</param>
    private static void AssertPersistenceModes(EventTreeResponse response)
    {
        EventTreeResponse selfContained = CloneSelfContained(response);
        IReadOnlyList<EventTreeResponse> functions = CollectFunctions(response);
        var byId = functions.GroupBy(function => function.Id)
            .ToDictionary(group => group.Key, group => (IRiskFunction)group.First());
        var byName = functions.Where(function => !string.IsNullOrEmpty(function.Name))
            .GroupBy(function => function.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IRiskFunction)group.First(), StringComparer.Ordinal);
        IRiskFunctionResolver resolver = new RiskFunctionResolver(
            id => byId.TryGetValue(id, out IRiskFunction? function) ? function : null,
            name => byName.TryGetValue(name, out IRiskFunction? function) ? function : null);
        var byReference = new EventTreeResponse(
            response.ToXElement(RiskSerializationMode.ByReference), resolver);

        CollectionAssert.AreEqual(response.CanonicalHash(), selfContained.CanonicalHash());
        CollectionAssert.AreEqual(response.CanonicalHash(), byReference.CanonicalHash());
        AssertSamplesEquivalent(response, selfContained, "self-contained round trip");
        AssertSamplesEquivalent(response, byReference, "by-reference round trip");
    }

    /// <summary>Asserts every independent link equals controlled explicit materialization.</summary>
    /// <param name="response">The linked response.</param>
    private static void AssertMaterializedCloneEquivalent(EventTreeResponse response)
    {
        EventTreeResponse explicitClone = CloneSelfContained(response);
        while (true)
        {
            EventTreeLinkNode? link = CollectFunctions(explicitClone)
                .SelectMany(function => function.EventTree.Nodes.OfType<EventTreeLinkNode>())
                .FirstOrDefault();
            if (link == null) break;
            EventTree tree = link.Owner ?? throw new InvalidOperationException(
                "A generated materialization candidate link has no owning tree.");
            tree.MaterializeLink(link.Id);
        }

        AssertSamplesEquivalent(response, explicitClone,
            "independent-link expansion versus explicit clone");
    }

    /// <summary>Asserts GUID/name/metadata/order invariance and compute-content sensitivity.</summary>
    /// <param name="response">The generated response.</param>
    private static void AssertIdentityProperties(EventTreeResponse response)
    {
        byte[] hash = response.CanonicalHash();
        XElement remappedXml = response.ToXElement(RiskSerializationMode.SelfContained);
        RegeneratePersistentIds(remappedXml);
        var remapped = new EventTreeResponse(remappedXml);
        CollectionAssert.AreEqual(hash, remapped.CanonicalHash(), "persistent GUID regeneration");
        AssertSamplesEquivalent(response, remapped, "persistent GUID regeneration");

        EventTreeResponse metadata = CloneSelfContained(response);
        int ordinal = 0;
        foreach (EventTreeResponse function in CollectFunctions(metadata))
        {
            function.Name = $"Renamed function {ordinal}";
            function.Description = $"Metadata {ordinal}";
            function.AssignNewId();
            foreach (EventNodeBase node in function.EventTree.Nodes)
            {
                node.Name = $"Renamed node {ordinal}";
                node.Description = $"Display metadata {ordinal++}";
            }
        }
        ReorderSiblings(metadata);
        CollectionAssert.AreEqual(hash, metadata.CanonicalHash(), "metadata/name/order invariance");
        AssertSamplesEquivalent(response, metadata, "metadata/name/order invariance");

        EventTreeResponse computeEdit = CloneSelfContained(response);
        ChanceNode changed = CollectFunctions(computeEdit)
            .SelectMany(function => function.EventTree.Nodes.OfType<ChanceNode>())
            .First();
        changed.ProbabilitySource = new ProbabilitySource(0.123456789d);
        Assert.IsFalse(hash.AsSpan().SequenceEqual(computeEdit.CanonicalHash()),
            "A compute-relevant probability-source edit must move canonical identity.");
    }

    /// <summary>Asserts two mean branch/aggregate samples are numerically equivalent.</summary>
    /// <param name="expected">The reference response.</param>
    /// <param name="actual">The equivalent response.</param>
    /// <param name="label">The assert label.</param>
    private static void AssertSamplesEquivalent(EventTreeResponse expected,
        EventTreeResponse actual, string label)
    {
        List<BranchVector> expectedBranches = ToVectors(expected.SampleBranches());
        List<BranchVector> actualBranches = ToVectors(actual.SampleBranches());
        Assert.AreEqual(expectedBranches.Count, actualBranches.Count, label);
        for (int i = 0; i < expectedBranches.Count; i++)
        {
            Assert.AreEqual(expectedBranches[i].IsFailure, actualBranches[i].IsFailure, label);
            for (int h = 0; h < expectedBranches[i].Probabilities.Length; h++)
                Assert.AreEqual(expectedBranches[i].Probabilities[h],
                    actualBranches[i].Probabilities[h], ProbabilityTolerance, label);
        }

        OrderedPairedData expectedAggregate = expected.SampleResponseFunction();
        OrderedPairedData actualAggregate = actual.SampleResponseFunction();
        Assert.AreEqual(expectedAggregate.Count, actualAggregate.Count, label);
        for (int h = 0; h < expectedAggregate.Count; h++)
            Assert.AreEqual(expectedAggregate[h].Y, actualAggregate[h].Y,
                ProbabilityTolerance, label);
    }

    /// <summary>Compares compiled evaluation with an independent authored-tree recursive oracle.</summary>
    /// <param name="response">The generated response.</param>
    private static void AssertCompiledAgainstRecursiveOracle(EventTreeResponse response)
    {
        OracleMatrix oracle = EvaluateOracle(response);
        ResponseBranchSample compiled = response.SampleBranches();
        List<BranchVector> expected = oracle.ToVectors();
        List<BranchVector> actual = ToVectors(compiled);
        Assert.AreEqual(expected.Count, actual.Count);

        OrderedPairedData aggregate = response.SampleResponseFunction();
        for (int h = 0; h < response.HazardLevels.Count; h++)
        {
            double terminalMass = 0d;
            double terminalCompensation = 0d;
            double failureMass = 0d;
            double failureCompensation = 0d;
            for (int branch = 0; branch < compiled.Branches.Count; branch++)
            {
                AddCompensated(ref terminalMass, ref terminalCompensation,
                    compiled.Probabilities[branch][h]);
                if (compiled.Branches[branch].IsFailure)
                    AddCompensated(ref failureMass, ref failureCompensation,
                        compiled.Probabilities[branch][h]);
            }
            Assert.AreEqual(1d, terminalMass, ProbabilityTolerance,
                $"Terminal mass at hazard index {h}.");
            Assert.AreEqual(failureMass, aggregate[h].Y, ProbabilityTolerance,
                $"Aggregate failure versus compiled failure leaves at hazard index {h}.");
            Assert.AreEqual(oracle.AggregateFailure[h], aggregate[h].Y, ProbabilityTolerance,
                $"Independent aggregate failure at hazard index {h}.");
        }

        for (int branch = 0; branch < expected.Count; branch++)
        {
            Assert.AreEqual(expected[branch].IsFailure, actual[branch].IsFailure);
            for (int h = 0; h < expected[branch].Probabilities.Length; h++)
                Assert.AreEqual(expected[branch].Probabilities[h],
                    actual[branch].Probabilities[h], ProbabilityTolerance,
                    $"Independent terminal {branch}, hazard {h}.");
        }
    }

    /// <summary>Evaluates every hazard by an independent recursive authored-tree walk.</summary>
    /// <param name="response">The response to evaluate.</param>
    /// <returns>The exhaustive oracle matrix.</returns>
    private static OracleMatrix EvaluateOracle(EventTreeResponse response)
    {
        var rows = new Dictionary<string, OracleRow>(StringComparer.Ordinal);
        var aggregate = new double[response.HazardLevels.Count];
        for (int h = 0; h < response.HazardLevels.Count; h++)
        {
            var leaves = new List<OracleLeaf>();
            double implicitMass = 0d;
            double implicitCompensation = 0d;
            WalkChildren(response, response.EventTree.Root, 1d, h, "root", leaves,
                ref implicitMass, ref implicitCompensation);
            leaves.Add(new OracleLeaf("implicit", false, implicitMass));
            double failureCompensation = 0d;
            for (int i = 0; i < leaves.Count; i++)
            {
                OracleLeaf leaf = leaves[i];
                if (!rows.TryGetValue(leaf.Key, out OracleRow? row))
                {
                    row = new OracleRow(leaf.IsFailure,
                        new double[response.HazardLevels.Count]);
                    rows.Add(leaf.Key, row);
                }
                Assert.AreEqual(row.IsFailure, leaf.IsFailure,
                    "Terminal classification must be stable across hazards.");
                row.Probabilities[h] = leaf.Probability;
                if (leaf.IsFailure)
                    AddCompensated(ref aggregate[h], ref failureCompensation, leaf.Probability);
            }
        }
        return new OracleMatrix(rows.Values.ToArray(), aggregate);
    }

    /// <summary>Recursively propagates one parent mass through its effective authored children.</summary>
    /// <param name="function">The function owning the effective parent.</param>
    /// <param name="parent">The effective parent node.</param>
    /// <param name="parentMass">The incoming path mass.</param>
    /// <param name="hazardIndex">The aligned hazard index.</param>
    /// <param name="path">The deterministic oracle occurrence path.</param>
    /// <param name="leaves">The terminal sink.</param>
    /// <param name="implicitMass">The accumulated unmodeled mass.</param>
    /// <param name="implicitCompensation">The compensated implicit-mass correction.</param>
    private static void WalkChildren(EventTreeResponse function, EventNodeBase parent,
        double parentMass, int hazardIndex, string path, List<OracleLeaf> leaves,
        ref double implicitMass, ref double implicitCompensation)
    {
        var effective = new EffectiveNode[parent.Children.Count];
        var raw = new double[parent.Children.Count];
        int remainder = -1;
        double explicitSum = 0d;
        double sumCompensation = 0d;
        for (int i = 0; i < parent.Children.Count; i++)
        {
            effective[i] = ResolveEffective(function, parent.Children[i]);
            if (effective[i].Node is RemainderNode)
            {
                remainder = i;
                continue;
            }
            ChanceNode chance = effective[i].Node as ChanceNode
                ?? throw new InvalidOperationException("The generated oracle found a non-chance explicit branch.");
            raw[i] = EvaluateSourceMean(chance.ProbabilitySource, hazardIndex);
            AddCompensated(ref explicitSum, ref sumCompensation, raw[i]);
        }

        double scale = explicitSum > 1d ? 1d / explicitSum : 1d;
        double residual = explicitSum < 1d ? 1d - explicitSum : 0d;
        if (remainder < 0 && residual > 0d)
            AddCompensated(ref implicitMass, ref implicitCompensation, parentMass * residual);

        for (int i = 0; i < effective.Length; i++)
        {
            double conditional = i == remainder ? residual : raw[i] * scale;
            WalkEffective(effective[i], parentMass * conditional, hazardIndex,
                $"{path}/{i}", leaves, ref implicitMass, ref implicitCompensation);
        }
    }

    /// <summary>Continues recursion or records one effective terminal.</summary>
    /// <param name="effective">The resolved direct/link occurrence.</param>
    /// <param name="mass">The occurrence path mass.</param>
    /// <param name="hazardIndex">The aligned hazard index.</param>
    /// <param name="path">The deterministic oracle occurrence path.</param>
    /// <param name="leaves">The terminal sink.</param>
    /// <param name="implicitMass">The accumulated unmodeled mass.</param>
    /// <param name="implicitCompensation">The compensated implicit-mass correction.</param>
    private static void WalkEffective(EffectiveNode effective, double mass, int hazardIndex,
        string path, List<OracleLeaf> leaves, ref double implicitMass,
        ref double implicitCompensation)
    {
        if (effective.Node.Children.Count == 0)
        {
            bool isFailure = effective.TerminalLink?.IsFailure ?? effective.Node.IsFailure;
            leaves.Add(new OracleLeaf(path, isFailure, mass));
            return;
        }
        WalkChildren(effective.Function, effective.Node, mass, hazardIndex, path, leaves,
            ref implicitMass, ref implicitCompensation);
    }

    /// <summary>Resolves internal/external independent links without using the compiled plan.</summary>
    /// <param name="function">The current owning function.</param>
    /// <param name="node">The authored child.</param>
    /// <returns>The effective node and outer terminal wrapper.</returns>
    private static EffectiveNode ResolveEffective(EventTreeResponse function, EventNodeBase node)
    {
        EventTreeResponse currentFunction = function;
        EventNodeBase currentNode = node;
        EventTreeLinkNode? outerLink = null;
        while (currentNode is EventTreeLinkNode link)
        {
            outerLink ??= link;
            currentFunction = link.TargetFunction ?? currentFunction;
            currentNode = link.ResolveNode(currentFunction.EventTree, out _)
                ?? throw new InvalidOperationException("The generated oracle could not resolve a link target.");
        }
        return new EffectiveNode(currentFunction, currentNode, outerLink);
    }

    /// <summary>Evaluates one source mean independently on the shared aligned hazard index.</summary>
    /// <param name="source">The probability source.</param>
    /// <param name="hazardIndex">The aligned hazard index.</param>
    /// <returns>The raw conditional probability.</returns>
    private static double EvaluateSourceMean(ProbabilitySource source, int hazardIndex)
    {
        return source.Kind switch
        {
            ProbabilitySourceKind.DeterministicScalar => source.ScalarProbability!.Value,
            ProbabilitySourceKind.UncertainTabular => source.Table!.CurveSample()[hazardIndex].Y,
            ProbabilitySourceKind.ResponseFunctionReference
                when source.ResponseFunction is EventTreeResponse nested =>
                    EvaluateOracle(nested).AggregateFailure[hazardIndex],
            _ => throw new InvalidOperationException(
                "The generated property corpus only creates scalar, aligned table, and nested event-tree sources."),
        };
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

    /// <summary>Generates one deterministic valid case from a fixed seed and case index.</summary>
    /// <param name="seed">The fixed corpus seed.</param>
    /// <param name="caseIndex">The case index within the seed.</param>
    /// <returns>The generated response and exercised feature mask.</returns>
    private static GeneratedCase Generate(int seed, int caseIndex)
    {
        var random = new DeterministicRandom(
            ((ulong)(uint)seed << 32) ^ (uint)caseIndex ^ 0x9E3779B97F4A7C15UL);
        var context = new GenerationContext(seed, caseIndex, random);
        var tree = new EventTree();
        int shape = caseIndex % 3;
        if (shape == 0)
        {
            context.Features |= GeneratedFeature.Shallow;
            AddGroup(tree, tree.Root, 2 + random.Next(3), 0, context);
        }
        else if (shape == 1)
        {
            context.Features |= GeneratedFeature.Deep;
            EventNodeBase parent = tree.Root;
            int depth = 3 + random.Next(3);
            for (int level = 0; level < depth; level++)
                parent = AddGroup(tree, parent, 2, level, context);
        }
        else
        {
            context.Features |= GeneratedFeature.Wide;
            AddGroup(tree, tree.Root, 4 + random.Next(3), 0, context);
            ChanceNode[] firstLevel = tree.Root.Children.OfType<ChanceNode>().Take(2).ToArray();
            for (int i = 0; i < firstLevel.Length; i++)
                AddGroup(tree, firstLevel[i], 2 + random.Next(2), i + 1, context);
        }

        ChanceNode internalTarget = tree.Root.Children.OfType<ChanceNode>().First();
        if (caseIndex % 4 is 0 or 3)
        {
            tree.LinkIndependent(tree.Root.Id, internalTarget.Id, "Internal occurrence A");
            tree.LinkIndependent(tree.Root.Id, internalTarget.Id, "Internal occurrence B");
            context.Features |= GeneratedFeature.InternalLink | GeneratedFeature.RepeatedOccurrence;
        }
        if (caseIndex % 4 is 1 or 3)
        {
            var external = CreateExternalTarget(context);
            tree.LinkIndependent(tree.Root.Id, external.Response, external.Target.Id,
                "External occurrence A");
            tree.LinkIndependent(tree.Root.Id, external.Response, external.Target.Id,
                "External occurrence B");
            context.Features |= GeneratedFeature.ExternalLink | GeneratedFeature.RepeatedOccurrence;
        }

        EventTreeResponse response = CreateResponse(tree,
            $"Generated {seed.ToString(CultureInfo.InvariantCulture)}-{caseIndex}");
        context.Features |= GeneratedFeature.BothSerializationModes;
        return new GeneratedCase(seed, caseIndex, response, context.Features);
    }

    /// <summary>Adds one sibling group with a controlled below/equal/above-one raw sum.</summary>
    /// <param name="tree">The authored tree.</param>
    /// <param name="parent">The receiving parent.</param>
    /// <param name="requestedWidth">The requested explicit width.</param>
    /// <param name="groupIndex">The deterministic group index.</param>
    /// <param name="context">The generation context.</param>
    /// <returns>The first chance child, used as the deep-chain continuation.</returns>
    private static ChanceNode AddGroup(EventTree tree, EventNodeBase parent,
        int requestedWidth, int groupIndex, GenerationContext context)
    {
        int category = (context.CaseIndex + groupIndex) % 3;
        int width = category == 2 ? Math.Max(2, requestedWidth) : requestedWidth;
        double targetSum = category switch { 0 => 0.65d, 1 => 1d, _ => 1.3d };
        context.Features |= category switch
        {
            0 => GeneratedFeature.SumBelowOne,
            1 => GeneratedFeature.SumEqualOne,
            _ => GeneratedFeature.SumAboveOne,
        };

        ChanceNode? first = null;
        for (int i = 0; i < width; i++)
        {
            double probability = targetSum / width;
            int ordinal = context.NodeOrdinal++;
            bool isFailure = ((ordinal + context.CaseIndex) & 1) == 0;
            var node = new ChanceNode($"Chance {groupIndex}-{i}",
                CreateProbabilitySource(probability, context))
            {
                IsFailure = isFailure,
            };
            context.Features |= isFailure
                ? GeneratedFeature.FailureTerminal : GeneratedFeature.NonFailureTerminal;
            tree.Add(parent.Id, node);
            first ??= node;
        }

        if (((context.CaseIndex + groupIndex) & 1) == 0)
        {
            tree.Add(parent.Id, new RemainderNode($"Residual {groupIndex}"));
            context.Features |= GeneratedFeature.ExplicitRemainder |
                GeneratedFeature.NonFailureTerminal;
        }
        else
        {
            context.Features |= GeneratedFeature.ImplicitResidual;
        }
        return first!;
    }

    /// <summary>Creates a scalar, aligned uncertain table, or nested event-tree source.</summary>
    /// <param name="probability">The desired mean probability.</param>
    /// <param name="context">The generation context.</param>
    /// <returns>The generated probability source.</returns>
    private static ProbabilitySource CreateProbabilitySource(double probability,
        GenerationContext context)
    {
        bool requestNested = !context.NestedUsed &&
            (context.CaseIndex % 4 == 2 || context.CaseIndex % 8 == 3);
        if (requestNested)
        {
            context.NestedUsed = true;
            context.Features |= GeneratedFeature.NestedResponse;
            int depth = context.CaseIndex % 8 == 6 ? 2 : 1;
            return new ProbabilitySource(CreateNestedResponse(probability, depth, context));
        }

        bool useTable = (context.NodeOrdinal + context.CaseIndex) % 3 == 0;
        double delta = Math.Min(0.04d, Math.Min(probability, 1d - probability) * 0.25d);
        if (useTable && delta > 0d)
        {
            context.Features |= GeneratedFeature.AlignedUncertainTable;
            return new ProbabilitySource(AlignedTable(probability, delta));
        }

        context.Features |= GeneratedFeature.DeterministicSource;
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

    /// <summary>Creates one- or two-level nested event-tree probability sources.</summary>
    /// <param name="probability">The nested aggregate mean.</param>
    /// <param name="depth">The nesting depth.</param>
    /// <param name="context">The generation context.</param>
    /// <returns>The nested response.</returns>
    private static EventTreeResponse CreateNestedResponse(double probability, int depth,
        GenerationContext context)
    {
        var innerTree = new EventTree();
        double delta = Math.Min(0.03d, Math.Min(probability, 1d - probability) * 0.2d);
        ProbabilitySource source = delta > 0d
            ? new ProbabilitySource(AlignedTable(probability, delta))
            : new ProbabilitySource(probability);
        context.Features |= delta > 0d
            ? GeneratedFeature.AlignedUncertainTable : GeneratedFeature.DeterministicSource;
        innerTree.Add(innerTree.Root.Id, new ChanceNode("Nested failure", source));
        innerTree.Add(innerTree.Root.Id, new RemainderNode("Nested survival"));
        EventTreeResponse current = CreateResponse(innerTree,
            $"Nested {context.Seed}-{context.CaseIndex}-0");
        for (int level = 1; level < depth; level++)
        {
            var outerTree = new EventTree();
            outerTree.Add(outerTree.Root.Id,
                new ChanceNode($"Nested response {level}", new ProbabilitySource(current)));
            outerTree.Add(outerTree.Root.Id, new RemainderNode($"Nested survival {level}"));
            current = CreateResponse(outerTree,
                $"Nested {context.Seed}-{context.CaseIndex}-{level}");
        }
        return current;
    }

    /// <summary>Creates a reusable external subtree with scalar and aligned-table descendants.</summary>
    /// <param name="context">The generation context.</param>
    /// <returns>The external response and linked subtree root.</returns>
    private static ExternalTarget CreateExternalTarget(GenerationContext context)
    {
        var tree = new EventTree();
        var target = new ChanceNode("External sequence", new ProbabilitySource(0.4d))
        {
            IsFailure = false,
        };
        tree.Add(tree.Root.Id, target);
        tree.Add(target.Id, new ChanceNode("External failure", new ProbabilitySource(0.3d)));
        tree.Add(target.Id, new ChanceNode("External survival",
            new ProbabilitySource(AlignedTable(0.25d, 0.03d))) { IsFailure = false });
        tree.Add(target.Id, new RemainderNode("External residual"));
        context.Features |= GeneratedFeature.DeterministicSource |
            GeneratedFeature.AlignedUncertainTable | GeneratedFeature.ExplicitRemainder;
        return new ExternalTarget(CreateResponse(tree,
            $"External {context.Seed}-{context.CaseIndex}"), target);
    }

    /// <summary>Creates a labeled response on the generated corpus's common aligned hazard axis.</summary>
    /// <param name="tree">The authored tree.</param>
    /// <param name="name">The unique response name.</param>
    /// <returns>The response.</returns>
    private static EventTreeResponse CreateResponse(EventTree tree, string name)
    {
        return new EventTreeResponse(new[] { 0d, 1d, 2d }, tree)
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }

    /// <summary>Clones one response through the headless self-contained persistence path.</summary>
    /// <param name="response">The source response.</param>
    /// <returns>The isolated clone.</returns>
    private static EventTreeResponse CloneSelfContained(EventTreeResponse response)
    {
        return new EventTreeResponse(response.ToXElement(RiskSerializationMode.SelfContained));
    }

    /// <summary>Collects every recursively referenced event-tree function by reference identity.</summary>
    /// <param name="root">The root response.</param>
    /// <returns>The deterministic depth-first function list.</returns>
    private static IReadOnlyList<EventTreeResponse> CollectFunctions(EventTreeResponse root)
    {
        var result = new List<EventTreeResponse>();
        var visited = new HashSet<EventTreeResponse>(ReferenceEqualityComparer.Instance);
        Visit(root);
        return result;

        // Visits one recursive response dependency.
        void Visit(EventTreeResponse function)
        {
            if (!visited.Add(function)) return;
            result.Add(function);
            foreach (EventNodeBase node in function.EventTree.Nodes)
            {
                if (node is ChanceNode chance &&
                    chance.ProbabilitySource.ResponseFunction is EventTreeResponse nested)
                    Visit(nested);
                if (node is EventTreeLinkNode link && link.TargetFunction != null)
                    Visit(link.TargetFunction);
            }
        }
    }

    /// <summary>Reverses one pair of explicit siblings at every eligible generated parent.</summary>
    /// <param name="root">The recursive response root.</param>
    private static void ReorderSiblings(EventTreeResponse root)
    {
        foreach (EventTreeResponse function in CollectFunctions(root))
        {
            EventNodeBase[] parents = function.EventTree.Nodes.ToArray();
            for (int i = 0; i < parents.Length; i++)
            {
                EventNodeBase[] explicitChildren = parents[i].Children
                    .Where(child => child is not RemainderNode).ToArray();
                if (explicitChildren.Length >= 2)
                    function.EventTree.Move(explicitChildren[^1].Id, parents[i].Id,
                        explicitChildren[0].Id);
            }
        }
    }

    /// <summary>Regenerates every serialized function/node/branch GUID and matching reference.</summary>
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
            else if (attribute.Name.LocalName == "PersistencePath")
            {
                string[] segments = attribute.Value.Split('/');
                for (int i = 0; i < segments.Length; i++)
                {
                    if (Guid.TryParse(segments[i], out oldId) &&
                        map.TryGetValue(oldId, out replacement))
                        segments[i] = replacement.ToString("N", CultureInfo.InvariantCulture);
                }
                attribute.Value = string.Join("/", segments);
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
            0x10, 0x0A, 0x20, 0x26, 0x52, 0x4D, 0x43, 0x54,
            0, 0, 0, 0, 0, 0, 0, 0,
        };
        BitConverter.GetBytes(ordinal).CopyTo(bytes, 8);
        return new Guid(bytes);
    }

    /// <summary>
    /// Deterministically minimizes a failing case in at most 32 accepted reductions. Each pass
    /// first attempts to remove one branch/subtree, then simplifies one chance source to scalar
    /// 0.5. A candidate is retained only when it remains valid and preserves a property failure.
    /// </summary>
    /// <param name="failing">The original failing response.</param>
    /// <returns>The bounded minimized response.</returns>
    private static EventTreeResponse Minimize(EventTreeResponse failing)
    {
        EventTreeResponse current = CloneSelfContained(failing);
        const int maximumAcceptedReductions = 32;
        for (int reduction = 0; reduction < maximumAcceptedReductions; reduction++)
        {
            bool accepted = false;
            IReadOnlyList<EventTreeResponse> functions = CollectFunctions(current);
            for (int functionIndex = functions.Count - 1; functionIndex >= 0 && !accepted;
                 functionIndex--)
            {
                Guid[] nodeIds = functions[functionIndex].EventTree.Nodes
                    .Where(node => node != functions[functionIndex].EventTree.Root)
                    .Select(node => node.Id).Reverse().ToArray();
                for (int nodeIndex = 0; nodeIndex < nodeIds.Length; nodeIndex++)
                {
                    EventTreeResponse candidate = CloneSelfContained(current);
                    IReadOnlyList<EventTreeResponse> candidateFunctions = CollectFunctions(candidate);
                    if (functionIndex >= candidateFunctions.Count) continue;
                    EventNodeBase? node =
                        candidateFunctions[functionIndex].EventTree.FindById(nodeIds[nodeIndex]);
                    if (node == null || node == candidateFunctions[functionIndex].EventTree.Root)
                        continue;
                    try
                    {
                        candidateFunctions[functionIndex].EventTree.Delete(node.Id,
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
            functions = CollectFunctions(current);
            for (int functionIndex = functions.Count - 1; functionIndex >= 0 && !accepted;
                 functionIndex--)
            {
                Guid[] chanceIds = functions[functionIndex].EventTree.Nodes.OfType<ChanceNode>()
                    .Where(chance => chance.ProbabilitySource.Kind !=
                        ProbabilitySourceKind.DeterministicScalar ||
                        chance.ProbabilitySource.ScalarProbability != 0.5d)
                    .Select(chance => chance.Id).Reverse().ToArray();
                for (int chanceIndex = 0; chanceIndex < chanceIds.Length; chanceIndex++)
                {
                    EventTreeResponse candidate = CloneSelfContained(current);
                    IReadOnlyList<EventTreeResponse> candidateFunctions = CollectFunctions(candidate);
                    if (functionIndex >= candidateFunctions.Count) continue;
                    ChanceNode? chance = candidateFunctions[functionIndex].EventTree
                        .FindById(chanceIds[chanceIndex]) as ChanceNode;
                    if (chance == null) continue;
                    chance.ProbabilitySource = new ProbabilitySource(0.5d);
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
    private static bool IsValidFailingCandidate(EventTreeResponse candidate)
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

    /// <summary>Converts and deterministically sorts one production branch sample.</summary>
    /// <param name="sample">The sampled branches.</param>
    /// <returns>Classification/probability vectors sorted independently of identity metadata.</returns>
    private static List<BranchVector> ToVectors(ResponseBranchSample sample)
    {
        var vectors = new List<BranchVector>(sample.Branches.Count);
        for (int i = 0; i < sample.Branches.Count; i++)
            vectors.Add(new BranchVector(sample.Branches[i].IsFailure,
                sample.Probabilities[i].ToArray()));
        vectors.Sort(CompareBranchVectors);
        return vectors;
    }

    /// <summary>Compares two branch vectors lexicographically by classification and ordinates.</summary>
    /// <param name="left">The left vector.</param>
    /// <param name="right">The right vector.</param>
    /// <returns>The deterministic comparison result.</returns>
    private static int CompareBranchVectors(BranchVector left, BranchVector right)
    {
        int comparison = left.IsFailure.CompareTo(right.IsFailure);
        if (comparison != 0) return comparison;
        comparison = left.Probabilities.Length.CompareTo(right.Probabilities.Length);
        if (comparison != 0) return comparison;
        for (int i = 0; i < left.Probabilities.Length; i++)
        {
            comparison = left.Probabilities[i].CompareTo(right.Probabilities[i]);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    /// <summary>The required generated-corpus feature coverage.</summary>
    [Flags]
    private enum GeneratedFeature
    {
        /// <summary>No observed feature.</summary>
        None = 0,
        /// <summary>A shallow tree.</summary>
        Shallow = 1 << 0,
        /// <summary>A deep tree.</summary>
        Deep = 1 << 1,
        /// <summary>A wide tree.</summary>
        Wide = 1 << 2,
        /// <summary>An explicit remainder node.</summary>
        ExplicitRemainder = 1 << 3,
        /// <summary>An implicit residual branch.</summary>
        ImplicitResidual = 1 << 4,
        /// <summary>An explicit sibling sum below one.</summary>
        SumBelowOne = 1 << 5,
        /// <summary>An explicit sibling sum equal to one.</summary>
        SumEqualOne = 1 << 6,
        /// <summary>An explicit sibling sum above one.</summary>
        SumAboveOne = 1 << 7,
        /// <summary>A failure terminal.</summary>
        FailureTerminal = 1 << 8,
        /// <summary>A non-failure terminal.</summary>
        NonFailureTerminal = 1 << 9,
        /// <summary>A deterministic scalar source.</summary>
        DeterministicSource = 1 << 10,
        /// <summary>An aligned uncertain-table source.</summary>
        AlignedUncertainTable = 1 << 11,
        /// <summary>An internal independent-clone link.</summary>
        InternalLink = 1 << 12,
        /// <summary>An external independent-clone link.</summary>
        ExternalLink = 1 << 13,
        /// <summary>A repeated linked occurrence.</summary>
        RepeatedOccurrence = 1 << 14,
        /// <summary>A nested event-tree response probability source.</summary>
        NestedResponse = 1 << 15,
        /// <summary>Both persistence modes were exercised.</summary>
        BothSerializationModes = 1 << 16,
        /// <summary>Every required generated feature.</summary>
        All = Shallow | Deep | Wide | ExplicitRemainder | ImplicitResidual |
            SumBelowOne | SumEqualOne | SumAboveOne | FailureTerminal |
            NonFailureTerminal | DeterministicSource | AlignedUncertainTable |
            InternalLink | ExternalLink | RepeatedOccurrence | NestedResponse |
            BothSerializationModes,
    }

    /// <summary>One deterministic generated case and its feature ledger.</summary>
    private sealed record GeneratedCase(int Seed, int CaseIndex, EventTreeResponse Response,
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
        /// <summary>Whether this case already contains its bounded nested source.</summary>
        public bool NestedUsed { get; set; }
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

    /// <summary>An externally owned response and linked subtree root.</summary>
    private readonly record struct ExternalTarget(EventTreeResponse Response, ChanceNode Target);

    /// <summary>A directly authored or link-resolved effective node occurrence.</summary>
    private readonly record struct EffectiveNode(EventTreeResponse Function, EventNodeBase Node,
        EventTreeLinkNode? TerminalLink);

    /// <summary>One terminal emitted by the independent recursive oracle.</summary>
    private readonly record struct OracleLeaf(string Key, bool IsFailure, double Probability);

    /// <summary>One oracle branch probability row.</summary>
    private sealed record OracleRow(bool IsFailure, double[] Probabilities);

    /// <summary>The independent recursive oracle's branch matrix and aggregate failure row.</summary>
    private sealed class OracleMatrix
    {
        /// <summary>Initializes one oracle matrix.</summary>
        /// <param name="rows">The terminal rows.</param>
        /// <param name="aggregateFailure">The aggregate failure ordinates.</param>
        public OracleMatrix(IReadOnlyList<OracleRow> rows, double[] aggregateFailure)
        {
            Rows = rows;
            AggregateFailure = aggregateFailure;
        }

        /// <summary>The terminal rows.</summary>
        public IReadOnlyList<OracleRow> Rows { get; }
        /// <summary>The aggregate failure ordinates.</summary>
        public double[] AggregateFailure { get; }

        /// <summary>Converts and sorts the oracle rows for identity-independent comparison.</summary>
        /// <returns>The sorted branch vectors.</returns>
        public List<BranchVector> ToVectors()
        {
            var vectors = Rows.Select(row =>
                new BranchVector(row.IsFailure, row.Probabilities.ToArray())).ToList();
            vectors.Sort(CompareBranchVectors);
            return vectors;
        }
    }

    /// <summary>A terminal classification and its hazard-aligned probability ordinates.</summary>
    private sealed record BranchVector(bool IsFailure, double[] Probabilities);
}
