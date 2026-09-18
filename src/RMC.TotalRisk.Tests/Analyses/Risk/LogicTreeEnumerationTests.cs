using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Transforms;
using RMC.TotalRisk.Results.Risk;
using RMC.TotalRisk.Systems.Components;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;

namespace RMC.TotalRisk.Tests.Analyses.Risk;

/// <summary>
/// Unit tests for the exact logic-tree enumeration mode: the runtime-only property contract,
/// the validation gate matrix and guardrails, the exact deterministic-tree enumeration with its
/// published weights and attribution map, shared-variable and nested/unbound axis forcing, the
/// sensitivity re-derivation consistency, and the published-map lifecycle.
/// </summary>
[TestClass]
public class LogicTreeEnumerationTests
{
    #region Fixtures

    /// <summary>Builds the shared deterministic stage-frequency hazard.</summary>
    private static TabularHazard StageFrequency(string name = "Stage Frequency", double shift = 0d)
    {
        return new TabularHazard
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            NoUncertaintyFunction = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0.999d, new Deterministic(0d + shift)),
                    new UncertainOrdinate(0.5d, new Deterministic(10d + shift)),
                    new UncertainOrdinate(0.001d, new Deterministic(30d + shift)),
                },
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds a deterministic fragility rising (10 → 0) to (top → 1).</summary>
    private static TabularResponse Fragility(string name, double top)
    {
        return new TabularResponse
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(10d, new Deterministic(0d)), new UncertainOrdinate(top, new Deterministic(1d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds an epistemic fragility composite over three deterministic branches at 0.3/0.4/0.3.</summary>
    private static CompositeResponse EpistemicFragility(string name, double topA, double topB, double topC, string? variable = null)
    {
        return new CompositeResponse(new[]
        {
            new WeightedResponseFunction(Fragility($"{name} A", topA), 0.3d),
            new WeightedResponseFunction(Fragility($"{name} B", topB), 0.4d),
            new WeightedResponseFunction(Fragility($"{name} C", topC), 0.3d),
        })
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            CompositeCombinationType = CompositeCombinationType.EpistemicMixture,
            EpistemicVariable = variable ?? string.Empty,
        };
    }

    /// <summary>Builds a deterministic consequence, linear from (0 → 0) to (30 → top).</summary>
    private static TabularConsequence Consequence(string name, double top)
    {
        return new TabularConsequence
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(30d, new Deterministic(top)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds a single-mode component around the given fragility.</summary>
    private static SystemComponent Component(string name, IResponseFunction response, IConsequenceFunction? failureConsequence = null)
    {
        var component = new SystemComponent { Name = name };
        component.HazardFunction = StageFrequency();
        component.AddFailureMode(new FailureMode(null, null, response, failureConsequence ?? Consequence("Failure Loss", 300d)));
        component.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Loss", 60d)));
        return component;
    }

    /// <summary>
    /// Runs the exact logic-tree enumeration at M realizations per combination, with the
    /// ensemble integration discipline pinned to the mean-only discipline (1e-8, minimum depth
    /// 2) so per-realization integrals are exactly comparable to single-combination mean-only
    /// reference runs — the documented ensemble/mean discipline rule.
    /// </summary>
    private static async Task<RiskAnalysis> RunEnumerated(int perCombination, params SystemComponent[] components)
    {
        var analysis = new RiskAnalysis(components);
        analysis.Options.UseDefaults = false;
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.EnsembleTolerance = 1e-8;
        analysis.Options.EnsembleMinDepth = 2;
        analysis.LogicTreeEnumerationRealizations = perCombination;
        await analysis.RunAsync();
        Assert.IsTrue(analysis.IsEstimated);
        return analysis;
    }

    /// <summary>Runs a mean-only analysis of one component and returns its failure probability.</summary>
    private static async Task<double> MeanFailureProbability(SystemComponent component)
    {
        var analysis = new RiskAnalysis(new[] { component });
        analysis.Options.EstimateMeanRiskOnly = true;
        await analysis.RunAsync();
        Assert.IsTrue(analysis.IsEstimated);
        return analysis.MeanRiskResults!.Components[0].Curves.Fail.TotalProbability;
    }

    /// <summary>Reads the per-realization failure probabilities of one component.</summary>
    private static double[] FailureProbabilities(RiskAnalysis analysis, int componentIndex, int count)
    {
        var values = new double[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = analysis.RiskResults![i]!.ComponentResults[componentIndex].Fail.TotalProbability;
        }
        return values;
    }

    #endregion

    /// <summary>
    /// Verifies the property contract: values below one throw, assignment invalidates the
    /// estimated state, and clearing restores the sampled mode.
    /// </summary>
    [TestMethod]
    public async Task Test_Property_SetterGuards_AndInvalidation()
    {
        // Arrange
        var analysis = await RunEnumerated(2, Component("Dam", EpistemicFragility("Tree", 12d, 20d, 28d)));

        // Act & Assert — the guard, the invalidation, and the clear.
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => analysis.LogicTreeEnumerationRealizations = 0);
        Assert.IsTrue(analysis.IsEstimated);
        analysis.LogicTreeEnumerationRealizations = 3;
        Assert.IsFalse(analysis.IsEstimated, "Reassigning the enumeration count must invalidate the published results.");
        analysis.LogicTreeEnumerationRealizations = null;
        Assert.IsNull(analysis.LogicTreeEnumerationRealizations);
    }

    /// <summary>
    /// Verifies the validation gate matrix: enumeration refuses a mean-only run, user-supplied
    /// realization weights, a model with no epistemic axis, an epistemic consequence composite,
    /// a fractile pin on an enumerated composite, and a shared variable whose binders declare
    /// differing weight vectors.
    /// </summary>
    [TestMethod]
    public void Test_Validate_EnumerationGateMatrix()
    {
        // Mean-only refuses.
        var meanOnly = new RiskAnalysis(new[] { Component("Dam", EpistemicFragility("Tree", 12d, 20d, 28d)) });
        meanOnly.Options.EstimateMeanRiskOnly = true;
        meanOnly.LogicTreeEnumerationRealizations = 2;
        Assert.IsTrue(meanOnly.Validate().ValidationMessages.Any(m =>
            m.StartsWith("Error:", StringComparison.Ordinal) && m.Contains("mean-only", StringComparison.OrdinalIgnoreCase)));

        // User weights refuse — the enumerator owns the vector.
        var weighted = new RiskAnalysis(new[] { Component("Dam", EpistemicFragility("Tree", 12d, 20d, 28d)) });
        weighted.LogicTreeEnumerationRealizations = 2;
        weighted.RealizationWeights = Enumerable.Repeat(1d, weighted.Options.Realizations).ToArray();
        Assert.IsTrue(weighted.Validate().ValidationMessages.Any(m =>
            m.StartsWith("Error:", StringComparison.Ordinal) && m.Contains("computes the realization weight vector")));

        // No epistemic axis refuses.
        var plain = new RiskAnalysis(new[] { Component("Dam", Fragility("Plain", 20d)) });
        plain.LogicTreeEnumerationRealizations = 2;
        Assert.IsTrue(plain.Validate().ValidationMessages.Any(m =>
            m.StartsWith("Error:", StringComparison.Ordinal) && m.Contains("nothing to enumerate")));

        // An epistemic consequence composite refuses — its branch rides the coupling draw.
        var consequenceTree = new CompositeConsequence(new[]
        {
            new WeightedConsequenceFunction(Consequence("Model A", 200d), 0.5d),
            new WeightedConsequenceFunction(Consequence("Model B", 400d), 0.5d),
        })
        {
            Name = "Loss Tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
            CompositeFunctionType = CompositeFunctionType.EpistemicMixture,
        };
        var consequence = new RiskAnalysis(new[] { Component("Dam", EpistemicFragility("Tree", 12d, 20d, 28d), consequenceTree) });
        consequence.LogicTreeEnumerationRealizations = 2;
        Assert.IsTrue(consequence.Validate().ValidationMessages.Any(m =>
            m.StartsWith("Error:", StringComparison.Ordinal) && m.Contains("coupling draw")));

        // A fractile pin on an enumerated composite refuses — the pin would overwrite the forcing.
        var pinnedComposite = EpistemicFragility("Tree", 12d, 20d, 28d);
        var pinned = new RiskAnalysis(new[] { Component("Dam", pinnedComposite) });
        pinned.LogicTreeEnumerationRealizations = 2;
        pinned.FractilePins = new[] { new FractilePin(pinnedComposite.Id, 0.95d) };
        Assert.IsTrue(pinned.Validate().ValidationMessages.Any(m =>
            m.StartsWith("Error:", StringComparison.Ordinal) && m.Contains("forced branch selection")));

        // Differing binder weight vectors refuse — exact enumeration needs one branch set per axis.
        var firstBinder = EpistemicFragility("First Tree", 12d, 20d, 28d, "SOK-Frag");
        var secondBinder = new CompositeResponse(new[]
        {
            new WeightedResponseFunction(Fragility("Alt A", 14d), 0.25d),
            new WeightedResponseFunction(Fragility("Alt B", 22d), 0.5d),
            new WeightedResponseFunction(Fragility("Alt C", 26d), 0.25d),
        })
        {
            Name = "Second Tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            CompositeCombinationType = CompositeCombinationType.EpistemicMixture,
            EpistemicVariable = "SOK-Frag",
        };
        var mismatched = new RiskAnalysis(new[] { Component("Dam A", firstBinder), Component("Dam B", secondBinder) });
        mismatched.LogicTreeEnumerationRealizations = 2;
        Assert.IsTrue(mismatched.Validate().ValidationMessages.Any(m =>
            m.StartsWith("Error:", StringComparison.Ordinal) && m.Contains("differing weight vectors") && m.Contains("exact enumeration")));
    }

    /// <summary>
    /// Verifies the size guardrails: a combination count above 4,096 warns, an enumerated
    /// realization count above 1,000,000 refuses, and a single-realization enumeration refuses.
    /// </summary>
    [TestMethod]
    public void Test_Validate_GuardrailLimits()
    {
        // Thirteen two-branch axes cross 8,192 combinations — the warning band.
        var components = new SystemComponent[13];
        for (int i = 0; i < 13; i++)
        {
            var pair = new CompositeResponse(new[]
            {
                new WeightedResponseFunction(Fragility($"Reach {i} Low", 14d + i), 0.5d),
                new WeightedResponseFunction(Fragility($"Reach {i} High", 20d + i), 0.5d),
            })
            {
                Name = $"Reach {i} Tree",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                CompositeCombinationType = CompositeCombinationType.EpistemicMixture,
            };
            components[i] = Component($"Reach {i}", pair);
        }
        var wide = new RiskAnalysis(components);
        wide.LogicTreeEnumerationRealizations = 2;
        var wideMessages = wide.Validate().ValidationMessages;
        Assert.IsTrue(wideMessages.Any(m => m.StartsWith("Warning:", StringComparison.Ordinal) && m.Contains("branch combinations")));

        // A three-branch axis at 400,000 per combination exceeds the realization limit.
        var deep = new RiskAnalysis(new[] { Component("Dam", EpistemicFragility("Tree", 12d, 20d, 28d)) });
        deep.LogicTreeEnumerationRealizations = 400_000;
        Assert.IsTrue(deep.Validate().ValidationMessages.Any(m =>
            m.StartsWith("Error:", StringComparison.Ordinal) && m.Contains("realizations (combinations")));

        // A degenerate single-branch axis at one per combination is a single realization.
        var single = new CompositeResponse(new[] { new WeightedResponseFunction(Fragility("Only", 20d), 1d) })
        {
            Name = "Single Tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            CompositeCombinationType = CompositeCombinationType.EpistemicMixture,
        };
        var degenerate = new RiskAnalysis(new[] { Component("Dam", single) });
        degenerate.LogicTreeEnumerationRealizations = 1;
        Assert.IsTrue(degenerate.Validate().ValidationMessages.Any(m =>
            m.StartsWith("Error:", StringComparison.Ordinal) && m.Contains("single realization")));
    }

    /// <summary>
    /// Verifies the exact enumeration of a deterministic two-axis tree (an epistemic hazard
    /// pair at 0.5/0.5 crossed with an epistemic fragility triple at 0.3/0.4/0.3, both unbound)
    /// at one realization per combination: the run computes exactly six realizations, each
    /// reproducing its decoded combination's single-combination mean-only value; the published
    /// weight vector is exactly the map's branch-weight products; and the weighted ensemble
    /// mean is exactly the weight-blended enumeration.
    /// </summary>
    [TestMethod]
    public async Task Test_Run_DeterministicTree_ExactEnumeration()
    {
        // Arrange — the six single-combination mean-only reference values.
        double[] hazardShifts = { 0d, 5d };
        double[] fragilityTops = { 12d, 20d, 28d };
        var references = new double[2, 3];
        for (int h = 0; h < 2; h++)
        {
            for (int f = 0; f < 3; f++)
            {
                var reference = new SystemComponent { Name = "Dam" };
                reference.HazardFunction = StageFrequency($"Hazard {h}", hazardShifts[h]);
                reference.AddFailureMode(new FailureMode(null, null, Fragility($"Frag {f}", fragilityTops[f]), Consequence("Failure Loss", 300d)));
                reference.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Loss", 60d)));
                references[h, f] = await MeanFailureProbability(reference);
            }
        }

        // Arrange — the two-axis logic tree, both composites unbound (id-keyed axes).
        var hazardTree = new CompositeHazard(new[]
        {
            new WeightedHazardFunction(StageFrequency("Hazard 0", 0d), 0.5d),
            new WeightedHazardFunction(StageFrequency("Hazard 1", 5d), 0.5d),
        })
        {
            Name = "Hazard Tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            CompositeCombinationType = CompositeCombinationType.EpistemicMixture,
        };
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = hazardTree;
        component.AddFailureMode(new FailureMode(null, null,
            EpistemicFragility("Fragility Tree", 12d, 20d, 28d), Consequence("Failure Loss", 300d)));
        component.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Loss", 60d)));

        // Act
        var analysis = await RunEnumerated(1, component);
        var map = analysis.LogicTreeEnumeration!;

        // Assert — the design: two unbound axes, six combinations, six realizations.
        Assert.IsNotNull(map);
        Assert.AreEqual(2, map.Axes.Count);
        Assert.IsTrue(map.Axes.All(a => !a.IsSharedVariable && a.FunctionId != null));
        Assert.AreEqual(6, map.CombinationCount);
        Assert.AreEqual(1, map.RealizationsPerCombination);
        Assert.AreEqual(6, map.RealizationCount);
        Assert.AreEqual(6, analysis.RiskResults!.Count);
        int hazardAxis = map.Axes[0].Name == "Hazard Tree" ? 0 : 1;
        int fragilityAxis = 1 - hazardAxis;
        Assert.AreEqual("Hazard Tree", map.Axes[hazardAxis].Name);
        Assert.AreEqual("Fragility Tree", map.Axes[fragilityAxis].Name);

        // Every realization reproduces its decoded combination's reference value exactly, the
        // published weight is the map's exact product, and the weighted mean is the enumeration.
        double[] hazardWeights = { 0.5d, 0.5d };
        double[] fragilityWeights = { 0.3d, 0.4d, 0.3d };
        var storedWeights = analysis.RiskResults.RealizationWeights!;
        Assert.IsNotNull(storedWeights);
        double weightedSum = 0d;
        double weightTotal = 0d;
        double exact = 0d;
        for (int i = 0; i < 6; i++)
        {
            int c = map.CombinationOf(i);
            int h = map.BranchIndexOf(c, hazardAxis);
            int f = map.BranchIndexOf(c, fragilityAxis);
            double value = analysis.RiskResults[i]!.ComponentResults[0].Fail.TotalProbability;
            Assert.AreEqual(references[h, f], value, 1e-12, $"Realization {i} must reproduce combination ({h},{f}) exactly.");
            Assert.AreEqual(map.RealizationWeight(i), storedWeights[i], 0d, "The published weights are the map's exact products.");
            Assert.AreEqual(hazardWeights[h] * fragilityWeights[f], storedWeights[i], 1e-15, "The weight is the branch-weight product.");
            weightedSum += storedWeights[i] * value;
            weightTotal += storedWeights[i];
            exact += hazardWeights[h] * fragilityWeights[f] * references[h, f];
        }
        Assert.AreEqual(1d, weightTotal, 1e-12, "The enumeration weights integrate to one.");
        Assert.AreEqual(exact, weightedSum / weightTotal, 1e-12, "The weighted mean is the exact enumeration.");
    }

    /// <summary>
    /// Verifies a shared-variable axis forces every binder: two components bound to one
    /// variable enumerate as one three-branch axis, and within every combination block both
    /// components hold their corresponding branch value in each of the block's realizations.
    /// </summary>
    [TestMethod]
    public async Task Test_Run_SharedVariableAxis_ForcesAllBinders()
    {
        // Arrange — branch references for both components.
        double[] firstTops = { 12d, 20d, 28d };
        double[] secondTops = { 14d, 22d, 26d };
        var firstReferences = new double[3];
        var secondReferences = new double[3];
        for (int b = 0; b < 3; b++)
        {
            firstReferences[b] = await MeanFailureProbability(Component("Dam A", Fragility("Branch", firstTops[b])));
            secondReferences[b] = await MeanFailureProbability(Component("Dam B", Fragility("Branch", secondTops[b])));
        }

        // Act — one bound axis, three combinations, two realizations per block.
        var analysis = await RunEnumerated(2,
            Component("Dam A", EpistemicFragility("First Tree", firstTops[0], firstTops[1], firstTops[2], "SOK-Frag")),
            Component("Dam B", EpistemicFragility("Second Tree", secondTops[0], secondTops[1], secondTops[2], "SOK-Frag")));
        var map = analysis.LogicTreeEnumeration!;

        // Assert — the axis shape and the block-forced values on both binders.
        Assert.AreEqual(1, map.Axes.Count);
        Assert.IsTrue(map.Axes[0].IsSharedVariable);
        Assert.AreEqual("SOK-Frag", map.Axes[0].Name);
        Assert.AreEqual(3, map.CombinationCount);
        Assert.AreEqual(6, map.RealizationCount);
        var first = FailureProbabilities(analysis, 0, 6);
        var second = FailureProbabilities(analysis, 1, 6);
        for (int i = 0; i < 6; i++)
        {
            int branch = map.BranchIndexOf(map.CombinationOf(i), 0);
            Assert.AreEqual(firstReferences[branch], first[i], 1e-12, $"Realization {i}: the first binder holds branch {branch}.");
            Assert.AreEqual(secondReferences[branch], second[i], 1e-12, $"Realization {i}: the second binder holds the same branch.");
        }
    }

    /// <summary>
    /// Verifies an unbound epistemic composite nested inside an aleatory composite is
    /// discovered and forced through the parent's own recursion: the enumeration holds the
    /// nested selection constant per block, reproducing reference models whose nested composite
    /// is replaced by each branch directly.
    /// </summary>
    [TestMethod]
    public async Task Test_Run_NestedUnboundComposite_IsForced()
    {
        // Arrange — reference values: the aleatory mixture with the nested tree replaced by one branch.
        double[] tops = { 14d, 24d };
        var references = new double[2];
        for (int b = 0; b < 2; b++)
        {
            var reference = new CompositeResponse(new[]
            {
                new WeightedResponseFunction(Fragility("Nested Branch", tops[b]), 0.5d),
                new WeightedResponseFunction(Fragility("Plain", 20d), 0.5d),
            })
            {
                Name = "Outer Mixture",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                CompositeCombinationType = CompositeCombinationType.Mixture,
            };
            references[b] = await MeanFailureProbability(Component("Dam", reference));
        }

        // Arrange — the nested unbound epistemic pair inside the aleatory outer mixture.
        var nested = new CompositeResponse(new[]
        {
            new WeightedResponseFunction(Fragility("Nested Low", tops[0]), 0.5d),
            new WeightedResponseFunction(Fragility("Nested High", tops[1]), 0.5d),
        })
        {
            Name = "Nested Tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            CompositeCombinationType = CompositeCombinationType.EpistemicMixture,
        };
        var outer = new CompositeResponse(new[]
        {
            new WeightedResponseFunction(nested, 0.5d),
            new WeightedResponseFunction(Fragility("Plain", 20d), 0.5d),
        })
        {
            Name = "Outer Mixture",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            CompositeCombinationType = CompositeCombinationType.Mixture,
        };

        // Act — one id-keyed axis from the nested composite, three realizations per block.
        var analysis = await RunEnumerated(3, Component("Dam", outer));
        var map = analysis.LogicTreeEnumeration!;

        // Assert — the nested selection is constant per block and reproduces the references.
        Assert.AreEqual(1, map.Axes.Count);
        Assert.IsFalse(map.Axes[0].IsSharedVariable);
        Assert.AreEqual("Nested Tree", map.Axes[0].Name);
        Assert.AreEqual(nested.Id, map.Axes[0].FunctionId);
        Assert.AreEqual(6, map.RealizationCount);
        var values = FailureProbabilities(analysis, 0, 6);
        for (int i = 0; i < 6; i++)
        {
            int branch = map.BranchIndexOf(map.CombinationOf(i), 0);
            Assert.AreEqual(references[branch], values[i], 1e-12, $"Realization {i}: the nested composite holds branch {branch}.");
        }
    }

    /// <summary>Builds a minimal event-tree response whose one chance node references the given source.</summary>
    private static EventTreeResponse TreeWithSource(IResponseFunction source)
    {
        var tree = new EventTree();
        tree.Add(tree.Root.Id, new ChanceNode("Breach", new ProbabilitySource(source)));
        tree.Add(tree.Root.Id, new RemainderNode("Survival"));
        return new EventTreeResponse(new[] { 0d, 30d }, tree)
        {
            Name = "Tree Response",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }

    /// <summary>
    /// Verifies the tree-source boundary: an epistemic composite referenced through a tree
    /// probability source is sampled by the tree's own isolated setup clones, which the axis
    /// discovery cannot see — so enumeration refuses it loudly rather than publishing a
    /// silently part-sampled "exact" result, the mean-only gate catches the same composite
    /// (the tree's mean clone would otherwise silently blend), and the full-uncertainty
    /// sampled configuration remains valid.
    /// </summary>
    [TestMethod]
    public async Task Test_TreeNestedComposite_RefusedLoudly()
    {
        // Arrange — an unbound epistemic pair as the tree's probability source.
        var nested = new CompositeResponse(new[]
        {
            new WeightedResponseFunction(Fragility("Nested Low", 14d), 0.4d),
            new WeightedResponseFunction(Fragility("Nested High", 24d), 0.6d),
        })
        {
            Name = "Nested Tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            CompositeCombinationType = CompositeCombinationType.EpistemicMixture,
        };
        var analysis = new RiskAnalysis(new[] { Component("Dam", TreeWithSource(nested)) });
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.LogicTreeEnumerationRealizations = 3;

        // Assert — enumeration refuses at validation and at run start.
        Assert.IsTrue(analysis.Validate().ValidationMessages.Any(m =>
            m.StartsWith("Error:", StringComparison.Ordinal) && m.Contains("tree probability source")));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => analysis.RunAsync());
        Assert.IsFalse(analysis.IsEstimated);

        // Assert — the mean-only gate catches the tree-carried composite (the blend refusal).
        analysis.LogicTreeEnumerationRealizations = null;
        analysis.Options.EstimateMeanRiskOnly = true;
        var (meanValid, meanMessages) = analysis.Validate();
        Assert.IsFalse(meanValid);
        Assert.IsTrue(meanMessages.Any(m =>
            m.StartsWith("Error:", StringComparison.Ordinal) && m.Contains("analytic blend")));

        // Assert — the sampled full-uncertainty configuration stays valid (each setup clone
        // selects independently, the established sampled reading).
        analysis.Options.EstimateMeanRiskOnly = false;
        Assert.IsTrue(analysis.Validate().IsValid);
    }

    /// <summary>
    /// Verifies the chain-carried boundary: an epistemic composite transform inside a tree
    /// probability source's hazard-transform chain samples in the tree's isolated setup clones
    /// exactly like a tree-carried response composite, so enumeration and the mean-only blend
    /// gate both refuse it loudly, while an ordinary deterministic chain stays accepted.
    /// </summary>
    [TestMethod]
    public async Task Test_ChainCarriedEpistemicTransform_RefusedLoudly()
    {
        // Arrange — an epistemic transform pair inside a transformed tabular source.
        var epistemicChain = new CompositeTransform(new[]
        {
            new WeightedTransformFunction(DurationMap("Short", 0.4d), 0.5d),
            new WeightedTransformFunction(DurationMap("Long", 0.6d), 0.5d),
        })
        {
            Name = "Chain",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            TransformedHazard = "Duration",
            TransformedHazardUnit = "hr",
            CompositeFunctionType = CompositeFunctionType.EpistemicMixture,
        };
        var tree = new EventTree();
        tree.Add(tree.Root.Id, new ChanceNode("Breach", new ProbabilitySource(
            DurationTable(), new ITransformFunction[] { epistemicChain })));
        tree.Add(tree.Root.Id, new RemainderNode("Survival"));
        var response = new EventTreeResponse(new[] { 0d, 30d }, tree)
        {
            Name = "Tree Response",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
        var analysis = new RiskAnalysis(new[] { Component("Dam", response) });
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.LogicTreeEnumerationRealizations = 3;

        // Assert — enumeration refuses at validation and at run start.
        Assert.IsTrue(analysis.Validate().ValidationMessages.Any(m =>
            m.StartsWith("Error:", StringComparison.Ordinal) && m.Contains("tree probability source")));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => analysis.RunAsync());

        // Assert — the mean-only gate catches the same chain-carried composite.
        analysis.LogicTreeEnumerationRealizations = null;
        analysis.Options.EstimateMeanRiskOnly = true;
        Assert.IsTrue(analysis.Validate().ValidationMessages.Any(m =>
            m.StartsWith("Error:", StringComparison.Ordinal) && m.Contains("analytic blend")));

        // Assert — an ordinary deterministic chain in the same seat stays accepted.
        ((ChanceNode)response.EventTree.Nodes.First(n => n.Name == "Breach")).ProbabilitySource =
            new ProbabilitySource(DurationTable(), new ITransformFunction[] { DurationMap("Plain", 0.5d) });
        Assert.IsTrue(analysis.Validate().IsValid, string.Join(" | ",
            analysis.Validate().ValidationMessages));
    }

    /// <summary>
    /// Verifies the external-transfer boundary: an epistemic composite reachable only through an
    /// external fault-tree transfer target's basic event is tree-carried — the transfer expands
    /// into the owner's isolated plan — so enumeration and the mean-only blend gate both refuse
    /// it loudly.
    /// </summary>
    [TestMethod]
    public async Task Test_TransferCarriedEpistemicComposite_RefusedLoudly()
    {
        // Arrange — the epistemic composite sits behind a basic event of an EXTERNAL fault tree
        // reached only through a transfer node in the component's own fault tree.
        var nested = new CompositeResponse(new[]
        {
            new WeightedResponseFunction(Fragility("Nested Low", 14d), 0.4d),
            new WeightedResponseFunction(Fragility("Nested High", 24d), 0.6d),
        })
        {
            Name = "Nested Transfer",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            CompositeCombinationType = CompositeCombinationType.EpistemicMixture,
        };
        var externalTree = new FaultTree();
        externalTree.Add(externalTree.Root.Id, new FaultTreeBasicEventNode("Carrier",
            new ProbabilitySource(nested)));
        var external = new FaultTreeResponse(new[] { 0d, 30d }, externalTree)
        {
            Name = "External Subsystem",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
        var ownerTree = new FaultTree();
        ownerTree.Add(ownerTree.Root.Id, new FaultTreeTransferNode("Subsystem",
            new TreeNodeReference(external.Id, externalTree.Root.Id, external.Name, "Top event"),
            external));
        var owner = new FaultTreeResponse(new[] { 0d, 30d }, ownerTree)
        {
            Name = "Owner Tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
        var analysis = new RiskAnalysis(new[] { Component("Dam", owner) });
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.LogicTreeEnumerationRealizations = 3;

        // Assert — enumeration refuses at validation and at run start.
        Assert.IsTrue(analysis.Validate().ValidationMessages.Any(m =>
            m.StartsWith("Error:", StringComparison.Ordinal) && m.Contains("tree probability source")));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => analysis.RunAsync());

        // Assert — the mean-only gate catches the same transfer-carried composite.
        analysis.LogicTreeEnumerationRealizations = null;
        analysis.Options.EstimateMeanRiskOnly = true;
        Assert.IsTrue(analysis.Validate().ValidationMessages.Any(m =>
            m.StartsWith("Error:", StringComparison.Ordinal) && m.Contains("analytic blend")));
    }

    /// <summary>Builds a deterministic Stage-to-Duration linear map.</summary>
    private static LinearTransform DurationMap(string name, double beta)
    {
        return new LinearTransform
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            TransformedHazard = "Duration",
            TransformedHazardUnit = "hr",
            Minimum = -100d,
            Maximum = 100d,
            Alpha = 0d,
            Beta = beta,
            IsUncertain = false,
        };
    }

    /// <summary>Builds a deterministic probability table on the transformed Duration axis.</summary>
    private static UncertainOrderedPairedData DurationTable()
    {
        return new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Deterministic(0d)),
                new UncertainOrdinate(18d, new Deterministic(1d)),
            }, true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic);
    }

    /// <summary>
    /// Verifies the post-run sensitivity re-derivation reproduces the enumerated design: the
    /// tornado's re-seed enters the enumeration's forcing scope, so the bound composite's
    /// re-derived selector column is bitwise the map's forcing percentiles (a seed-derived
    /// random column could not be), the variable's knowledge column exists, and its association
    /// with the branch-monotone outputs is negative.
    /// </summary>
    [TestMethod]
    public async Task Test_Run_SensitivityRederivation_MatchesEnumeratedRun()
    {
        // Arrange — fragility tops rise with the branch ordinal, so failure probability falls.
        var composite = EpistemicFragility("Tree", 12d, 20d, 28d, "SOK-Frag");
        var analysis = await RunEnumerated(2, Component("Dam", composite));
        var map = analysis.LogicTreeEnumeration!;

        // Act — the tornado over the stored enumerated ensemble (its re-seed leaves the author
        // composite holding the re-derived design, the documented sampler side effect).
        var results = analysis.MeasureSensitivity(RiskMeasure.Mean, RiskType.Fail, SensitivityMeasure.SpearmanCorrelation)!;

        // Assert — the variable's single column carries a negative association, and the author
        // composite's re-derived selector column is bitwise the enumeration's forcing design.
        var entry = results.Entries.Single(e => e.Label == "Epistemic Variable - SOK-Frag");
        Assert.IsTrue(entry.Value < 0d, "Failure probability falls with the branch ordinal.");
        for (int i = 0; i < map.RealizationCount; i++)
        {
            double forced = map.Axes[0].Branches[map.BranchIndexOf(map.CombinationOf(i), 0)].ForcedPercentile;
            Assert.AreEqual(forced, composite.SampledPercentile(i, 0), 0d,
                $"Realization {i}: the re-derived selector must be the forcing percentile.");
        }
    }

    /// <summary>
    /// Verifies the published-map lifecycle: an enumerated run publishes the map and stamps the
    /// ensemble weights; clearing the mode and re-running publishes a sampled run with neither;
    /// and a failed run clears the previously published map.
    /// </summary>
    [TestMethod]
    public async Task Test_Run_MapLifecycle()
    {
        // Arrange — an enumerated run.
        var component = Component("Dam", EpistemicFragility("Tree", 12d, 20d, 28d));
        var analysis = await RunEnumerated(2, component);
        Assert.IsNotNull(analysis.LogicTreeEnumeration);
        Assert.IsNotNull(analysis.RiskResults!.RealizationWeights);

        // Act — a sampled re-run of the same model publishes no map and no weights.
        analysis.LogicTreeEnumerationRealizations = null;
        analysis.Options.Realizations = 100;
        await analysis.RunAsync();
        Assert.IsTrue(analysis.IsEstimated);
        Assert.IsNull(analysis.LogicTreeEnumeration);
        Assert.IsNull(analysis.RiskResults!.RealizationWeights);
        Assert.AreEqual(100, analysis.RiskResults.Count);

        // Act — a failed enumerated run clears the published state, the map included.
        analysis.LogicTreeEnumerationRealizations = 400_000;
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => analysis.RunAsync());
        Assert.IsNull(analysis.LogicTreeEnumeration);
        Assert.IsFalse(analysis.IsEstimated);
    }
}
