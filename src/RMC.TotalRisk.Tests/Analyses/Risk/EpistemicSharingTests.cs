using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;
using RMC.TotalRisk.Core.Support;

namespace RMC.TotalRisk.Tests.Analyses.Risk;

/// <summary>
/// Unit tests for the engine face of the epistemic-mixture mode: the mean-only validation
/// gates, the exact deterministic-branch mixture identity behind a full run, the run-scope
/// shared-variable alignment across components with its declared column derivation, the
/// walk-shape invariance of the mode switch, fractile-pin branch conditioning, and the
/// one-column-per-variable sensitivity dedup.
/// </summary>
[TestClass]
public class EpistemicSharingTests
{
    #region Fixtures

    /// <summary>Builds the shared deterministic stage-frequency hazard.</summary>
    private static TabularHazard StageFrequency()
    {
        return new TabularHazard
        {
            Name = "Stage Frequency",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            NoUncertaintyFunction = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0.999d, new Deterministic(0d)),
                    new UncertainOrdinate(0.5d, new Deterministic(10d)),
                    new UncertainOrdinate(0.001d, new Deterministic(30d)),
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

    /// <summary>Runs a full-uncertainty analysis at 100 realizations.</summary>
    private static async Task<RiskAnalysis> RunFull(params SystemComponent[] components)
    {
        var analysis = new RiskAnalysis(components);
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.Realizations = 100;
        await analysis.RunAsync();
        Assert.IsTrue(analysis.IsEstimated);
        return analysis;
    }

    #endregion

    /// <summary>
    /// Verifies the mean-only gates: an epistemic composite on a walked cluster is an Error (the
    /// blend would be the wrong Jensen-gap answer), an epistemic consequence composite is a
    /// Warning (the blend keeps the exact mean), and the full-uncertainty configuration is
    /// valid.
    /// </summary>
    [TestMethod]
    public void Test_Validate_MeanOnly_EpistemicGates()
    {
        // A walked-cluster epistemic composite under mean-only is an Error.
        var walked = new RiskAnalysis(new[] { Component("Dam", EpistemicFragility("Tree", 12d, 20d, 28d)) });
        walked.Options.EstimateMeanRiskOnly = true;
        var (walkedValid, walkedMessages) = walked.Validate();
        Assert.IsFalse(walkedValid);
        Assert.IsTrue(walkedMessages.Any(m => m.StartsWith("Error:", StringComparison.Ordinal) && m.Contains("epistemic-mixture", StringComparison.OrdinalIgnoreCase)));

        // An epistemic consequence composite under mean-only is a Warning only.
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
        var consequenceOnly = new RiskAnalysis(new[] { Component("Dam", Fragility("Plain", 20d), consequenceTree) });
        consequenceOnly.Options.EstimateMeanRiskOnly = true;
        var (consequenceValid, consequenceMessages) = consequenceOnly.Validate();
        Assert.IsTrue(consequenceValid);
        Assert.IsTrue(consequenceMessages.Any(m => m.StartsWith("Warning:", StringComparison.Ordinal) && m.Contains("epistemic spread is absent")));

        // The full-uncertainty configuration carries neither gate.
        walked.Options.EstimateMeanRiskOnly = false;
        var (fullValid, fullMessages) = walked.Validate();
        Assert.IsTrue(fullValid);
        Assert.IsFalse(fullMessages.Any(m => m.Contains("epistemic", StringComparison.OrdinalIgnoreCase) && m.StartsWith("Error:", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Verifies the exact deterministic-branch mixture identity behind a full run: every
    /// realization's failure probability equals one branch's exactly, the LHS allocation is
    /// exactly N·ω (30/40/30 at 100), the ensemble mean is therefore exactly the weighted sum
    /// of the per-branch runs — the doctrine's "weight the risks" number, produced by the
    /// engine — and the public re-seed reproduces the run clone's branch selections (equal
    /// content, equal seeds), tying each computed value to its recorded selection.
    /// </summary>
    [TestMethod]
    public async Task Test_Run_EpistemicMixture_DeterministicBranchIdentity()
    {
        // Arrange — the epistemic model and the three single-branch reference runs. The run
        // computes on an isolated clone, so the author composite is read after re-deriving the
        // identical streams through the public component walk (equal content, equal seeds).
        var composite = EpistemicFragility("Tree", 12d, 20d, 28d);
        var component = Component("Dam", composite);
        var analysis = await RunFull(component);
        double[] branchProbabilities = new double[3];
        double[] tops = { 12d, 20d, 28d };
        for (int b = 0; b < 3; b++)
        {
            var reference = await RunFull(Component("Dam", Fragility("Branch", tops[b])));
            branchProbabilities[b] = reference.RiskResults![0]!.ComponentResults[0].Fail.TotalProbability;
        }

        var components = new List<SystemComponent>(analysis.Components);
        SystemComponent.AssignOccurrenceIndices(components);
        int seed = SeedHelpers.HashCombine(analysis.Options.PRNGSeed, component.CanonicalHash(), component.OccurrenceIndex);
        component.SetupSamplers(100, seed, analysis.Options.SamplingScheme);

        // Assert — every realization is exactly one branch, allocated 30/40/30, and driven by
        // the recorded selection.
        var counts = new int[3];
        double sum = 0d;
        for (int i = 0; i < 100; i++)
        {
            double value = analysis.RiskResults![i]!.ComponentResults[0].Fail.TotalProbability;
            int branch = Array.FindIndex(branchProbabilities, p => Math.Abs(p - value) <= 1e-12);
            Assert.IsTrue(branch >= 0, $"Realization {i} must reproduce one branch's failure probability exactly.");
            Assert.AreEqual(composite.SelectedBranchIndex(i), branch, "The recorded selection must drive the computed value.");
            counts[branch]++;
            sum += value;
        }
        CollectionAssert.AreEqual(new[] { 30, 40, 30 }, counts);
        double expectedMean = (0.3d * branchProbabilities[0]) + (0.4d * branchProbabilities[1]) + (0.3d * branchProbabilities[2]);
        Assert.AreEqual(expectedMean, sum / 100d, 1e-12, "The ensemble mean is the weighted sum of the branch risks.");
    }

    /// <summary>
    /// Verifies run-scope sharing: two components binding one variable select identical branch
    /// sequences, the shared column is exactly the declared run derivation (the analysis PRNG
    /// seed and the variable name), and the sampler walk shape is unchanged by the mode (the
    /// captured seed map has the same ordinal count as the aleatory variant).
    /// </summary>
    [TestMethod]
    public async Task Test_Run_SharedVariable_AlignsAcrossComponents()
    {
        // Arrange — two components with different branch content bound to one variable. Lower
        // fragility tops fail more, so each component's three distinct per-realization failure
        // probabilities rank descending by branch index — the published results reveal each
        // realization's selected branch without touching the run's isolated clones.
        var first = EpistemicFragility("First Tree", 12d, 20d, 28d, "SOK-Frag");
        var second = EpistemicFragility("Second Tree", 14d, 22d, 26d, "SOK-Frag");
        var analysis = await RunFull(Component("Dam A", first), Component("Dam B", second));

        int[] BranchSequence(int componentIndex)
        {
            var values = new double[100];
            for (int i = 0; i < 100; i++)
            {
                values[i] = analysis.RiskResults![i]!.ComponentResults[componentIndex].Fail.TotalProbability;
            }
            var distinct = values.Distinct().OrderByDescending(v => v).ToArray();
            Assert.AreEqual(3, distinct.Length, "Deterministic branches must yield exactly three distinct values.");
            return values.Select(v => Array.IndexOf(distinct, v)).ToArray();
        }

        // Assert — the published selections are identical across the two components, and they
        // are exactly the selections the declared run-scope column produces (the analysis PRNG
        // seed and the variable name alone, through the cumulative 0.3/0.7 boundaries).
        var firstBranches = BranchSequence(0);
        var secondBranches = BranchSequence(1);
        var column = EpistemicSharingScope.BuildColumns(
            new[] { "SOK-Frag" }, analysis.Options.PRNGSeed, 100, analysis.Options.SamplingScheme)["SOK-Frag"];
        for (int i = 0; i < 100; i++)
        {
            Assert.AreEqual(firstBranches[i], secondBranches[i], $"Realization {i} must select the same branch in both binders.");
            int expected = column[i] <= 0.3d ? 0 : column[i] <= 0.7d ? 1 : 2;
            Assert.AreEqual(expected, firstBranches[i], $"Realization {i} must select from the shared run-scope column.");
        }

        // The walk shape is unchanged by the epistemic mode: same ordinal counts as the
        // aleatory variant of the same model (the selector is a matrix, not a walk position).
        var aleatoryFirst = EpistemicFragility("First Tree", 12d, 20d, 28d);
        aleatoryFirst.CompositeCombinationType = CompositeCombinationType.Mixture;
        var aleatorySecond = EpistemicFragility("Second Tree", 14d, 22d, 26d);
        aleatorySecond.CompositeCombinationType = CompositeCombinationType.Mixture;
        var aleatory = await RunFull(Component("Dam A", aleatoryFirst), Component("Dam B", aleatorySecond));
        for (int c = 0; c < 2; c++)
        {
            Assert.AreEqual(aleatory.CapturedSamplerSeeds!.ComponentSeeds[c].Length,
                analysis.CapturedSamplerSeeds!.ComponentSeeds[c].Length,
                "The epistemic mode must add no walk ordinal.");
        }
    }

    /// <summary>
    /// Verifies fractile-pin branch conditioning: pinning an epistemic composite holds its
    /// selector, so every realization lives in the branch the pinned percentile selects — the
    /// "risk conditional on one model alternative" cross-tab.
    /// </summary>
    [TestMethod]
    public async Task Test_Run_FractilePin_HoldsBranch()
    {
        // Arrange — pin the composite at 0.95, which selects the last branch (cumulative 0.7),
        // and run the single-branch reference model for the value it must reproduce.
        var composite = EpistemicFragility("Tree", 12d, 20d, 28d);
        var analysis = new RiskAnalysis(new[] { Component("Dam", composite) });
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.Realizations = 100;
        analysis.FractilePins = new[] { new FractilePin(composite.Id, 0.95d) };
        await analysis.RunAsync();
        Assert.IsTrue(analysis.IsEstimated);
        var reference = await RunFull(Component("Dam", Fragility("Branch", 28d)));
        double expected = reference.RiskResults![0]!.ComponentResults[0].Fail.TotalProbability;

        // Assert — one conditioned branch everywhere: every realization reproduces the last
        // branch's failure probability exactly.
        for (int i = 0; i < 100; i++)
        {
            Assert.AreEqual(expected, analysis.RiskResults![i]!.ComponentResults[0].Fail.TotalProbability, 1e-12,
                "The pinned percentile must hold the branch choice in every realization.");
        }
    }

    /// <summary>
    /// Verifies the sensitivity dedup: two binders of one shared variable contribute exactly
    /// one knowledge column, labeled by the variable — one variable is one knowledge quantity.
    /// </summary>
    [TestMethod]
    public async Task Test_Sensitivity_SharedVariable_OneColumn()
    {
        // Arrange
        var first = EpistemicFragility("First Tree", 12d, 20d, 28d, "SOK-Frag");
        var second = EpistemicFragility("Second Tree", 14d, 22d, 26d, "SOK-Frag");
        var analysis = await RunFull(Component("Dam A", first), Component("Dam B", second));

        // Act — a system-scope tornado over the recorded design.
        var results = analysis.MeasureSensitivity(RiskMeasure.Mean, RiskType.Fail, SensitivityMeasure.SpearmanCorrelation)!;

        // Assert — exactly one column for the variable, labeled by it.
        var labels = results.Entries.Select(e => e.Label).Where(l => l.Contains("SOK-Frag", StringComparison.Ordinal)).ToList();
        Assert.AreEqual(1, labels.Count, "Two binders of one variable are one knowledge quantity.");
        Assert.AreEqual("Epistemic Variable - SOK-Frag", labels[0]);
    }
}
