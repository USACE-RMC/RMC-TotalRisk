using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using Numerics.Sampling;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Transforms;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Verification.Analyses.Risk;

/// <summary>
/// Epistemic-mixture (logic-tree) verification: the exact conditioned-interleaving identity
/// between an epistemic ensemble and its fractile-pinned branch-conditional runs, the
/// independent-model statistical mixture identity, the Jensen-gap doctrine numbers (blended
/// 0.023 versus weighted 0.262) reproduced through a composite transform, the shared-variable
/// selection pinned against an independently re-implemented column derivation, and the sampled
/// mode's convergence to exact branch-combination enumeration — the seed of the future exact
/// logic-tree enumerator.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Oracles and tolerances:</b> the conditioned-interleaving identity is bitwise — a fractile
/// pin overwrites only the selector column, so realization <c>i</c> of the unpinned ensemble
/// and realization <c>i</c> of the run pinned into its selected branch integrate identical
/// sampled chains. The independent-model identity is statistical (branch models are seeded from
/// their own content), asserted at four combined standard errors of the two Monte Carlo means.
/// The doctrine pin is exact at boundary-aligned Latin hypercube counts, where branch
/// allocation is exactly N·ω and every branch chain is deterministic. The shared-variable
/// oracle re-implements the column derivation from cryptographic and sampling primitives alone
/// (SHA-256 over the variable name, the little-endian seed fold, the positive-seed map, and the
/// Latin hypercube block), never calling the library's seed kernel. The enumeration oracle sums
/// weight-product mean-only runs over every branch combination; the sampled ensemble converges
/// at four standard errors.
/// </para>
/// </remarks>
[TestClass]
public class EpistemicMixtureVerification
{
    #region Fixtures

    /// <summary>Builds the deterministic stage-frequency hazard (exceedance 0.999 → 0 ft up to 0.001 → 30 ft).</summary>
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

    /// <summary>Builds an uncertain fragility (triangular ordinates around the given anchors).</summary>
    private static TabularResponse UncertainFragility(string name, double lowMode, double highMode)
    {
        return new TabularResponse
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(10d, new Triangular(0d, lowMode, Math.Min(1d, 2d * lowMode))),
                    new UncertainOrdinate(25d, new Triangular(Math.Max(0d, highMode - 0.2d), highMode, Math.Min(1d, highMode + 0.1d))),
                },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Triangular),
        };
    }

    /// <summary>Builds an epistemic fragility composite over the given children at 0.3/0.4/0.3.</summary>
    private static CompositeResponse EpistemicComposite(string name, IResponseFunction a, IResponseFunction b, IResponseFunction c, string? variable = null)
    {
        return new CompositeResponse(new[]
        {
            new WeightedResponseFunction(a, 0.3d),
            new WeightedResponseFunction(b, 0.4d),
            new WeightedResponseFunction(c, 0.3d),
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
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(30d, new Deterministic(top)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds a single-mode component around the given fragility.</summary>
    private static SystemComponent Component(string name, IResponseFunction response)
    {
        var component = new SystemComponent { Name = name };
        component.HazardFunction = StageFrequency();
        component.AddFailureMode(new FailureMode(null, null, response, Consequence("Failure Loss", 1000d)));
        component.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Loss", 100d)));
        return component;
    }

    /// <summary>Runs a full-uncertainty analysis at the given size.</summary>
    private static async Task<RiskAnalysis> RunFull(int realizations, params SystemComponent[] components)
    {
        var analysis = new RiskAnalysis(components);
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.Realizations = realizations;
        await analysis.RunAsync();
        Assert.IsTrue(analysis.IsEstimated);
        return analysis;
    }

    /// <summary>Reads the per-realization component failure probabilities of a completed run.</summary>
    private static double[] FailureProbabilities(RiskAnalysis analysis, int componentIndex, int realizations)
    {
        var values = new double[realizations];
        for (int i = 0; i < realizations; i++)
        {
            values[i] = analysis.RiskResults![i]!.ComponentResults[componentIndex].Fail.TotalProbability;
        }
        return values;
    }

    #endregion

    /// <summary>
    /// The exact conditioned-interleaving identity: a fractile pin overwrites only the branch
    /// selector, so the unpinned epistemic ensemble is the exact per-index interleaving of the
    /// three branch-conditioned ensembles — realization <c>i</c> of the unpinned run is bitwise
    /// one of the three pinned runs' realization <c>i</c>, the branch counts are exactly N·ω at
    /// the boundary-aligned Latin hypercube count, and each branch appears (the epistemic
    /// spread is real). This is simultaneously the epistemic-conditioning composition proof:
    /// "risk conditional on one model alternative" partitions the unconditional ensemble.
    /// </summary>
    [TestMethod]
    public async Task Test_ConditionedInterleaving_ExactPartition()
    {
        const int N = 1000;
        CompositeResponse Tree() => EpistemicComposite("Tree",
            UncertainFragility("Steep", 0.10d, 0.90d),
            UncertainFragility("Middle", 0.05d, 0.60d),
            UncertainFragility("Shallow", 0.02d, 0.30d));

        // The unpinned ensemble.
        var unpinned = await RunFull(N, Component("Dam", Tree()));
        var unpinnedValues = FailureProbabilities(unpinned, 0, N);

        // The three branch-conditioned ensembles (pins select branches 0/1/2 at the cumulative
        // 0.3/0.7 boundaries; pins are runtime-only, so the model content and every child
        // stream are identical).
        double[] pinPercentiles = { 0.15d, 0.5d, 0.85d };
        var pinnedValues = new double[3][];
        for (int b = 0; b < 3; b++)
        {
            var tree = Tree();
            var analysis = new RiskAnalysis(new[] { Component("Dam", tree) });
            analysis.Options.EstimateMeanRiskOnly = false;
            analysis.Options.Realizations = N;
            analysis.FractilePins = new[] { new FractilePin(tree.Id, pinPercentiles[b]) };
            await analysis.RunAsync();
            Assert.IsTrue(analysis.IsEstimated);
            pinnedValues[b] = FailureProbabilities(analysis, 0, N);
        }

        // Every unpinned realization is bitwise one pinned run's same-index realization.
        var counts = new int[3];
        for (int i = 0; i < N; i++)
        {
            int branch = -1;
            for (int b = 0; b < 3; b++)
            {
                if (unpinnedValues[i] == pinnedValues[b][i]) { branch = b; break; }
            }
            Assert.IsTrue(branch >= 0, $"Realization {i} must reproduce one branch-conditioned realization bitwise.");
            counts[branch]++;
        }
        CollectionAssert.AreEqual(new[] { 300, 400, 300 }, counts,
            "Latin hypercube stratification allocates branches exactly N·ω at boundary-aligned weights.");
    }

    /// <summary>
    /// The independent-model mixture identity: the epistemic ensemble's mean failure
    /// probability equals the weight-blended means of three standalone single-branch models
    /// within four combined Monte Carlo standard errors. The standalone models carry the same
    /// fragility content seeded from their own walks, so this is a genuine cross-model
    /// statistical identity, not a seed replay.
    /// </summary>
    [TestMethod]
    public async Task Test_IndependentBranchModels_StatisticalMixtureIdentity()
    {
        const int N = 4096;
        var epistemic = await RunFull(N, Component("Dam", EpistemicComposite("Tree",
            UncertainFragility("Steep", 0.10d, 0.90d),
            UncertainFragility("Middle", 0.05d, 0.60d),
            UncertainFragility("Shallow", 0.02d, 0.30d))));
        var epistemicValues = FailureProbabilities(epistemic, 0, N);

        double[] weights = { 0.3d, 0.4d, 0.3d };
        var branchMeans = new double[3];
        var branchVariances = new double[3];
        (double lowMode, double highMode)[] anchors = { (0.10d, 0.90d), (0.05d, 0.60d), (0.02d, 0.30d) };
        for (int b = 0; b < 3; b++)
        {
            var reference = await RunFull(N, Component("Dam",
                UncertainFragility($"Branch {b}", anchors[b].lowMode, anchors[b].highMode)));
            var values = FailureProbabilities(reference, 0, N);
            branchMeans[b] = values.Average();
            branchVariances[b] = values.Select(v => Math.Pow(v - branchMeans[b], 2d)).Sum() / (N - 1);
        }

        double expected = weights.Zip(branchMeans, (w, m) => w * m).Sum();
        double observed = epistemicValues.Average();
        double observedMean = observed;
        double observedVariance = epistemicValues.Select(v => Math.Pow(v - observedMean, 2d)).Sum() / (N - 1);

        // SE of the difference: the epistemic mean's own SE plus the weighted branch means'.
        double variance = (observedVariance / N) + weights.Zip(branchVariances, (w, v) => w * w * v / N).Sum();
        double tolerance = 4d * Math.Sqrt(variance);
        Assert.AreEqual(expected, observed, tolerance,
            $"The epistemic ensemble mean must reproduce the weighted branch means within 4·SE = {tolerance:E3}.");
    }

    /// <summary>
    /// The Jensen-gap doctrine pin: three candidate rating curves weighted 0.3/0.4/0.3 landing
    /// stages 100/103/106 ft at the 1% flow, evaluated through the Normal(105, 1) fragility.
    /// The Average (blended) composite answers Φ(−2) ≈ 0.023; the epistemic composite's
    /// ensemble mean answers 0.3·Φ(−5) + 0.4·Φ(−2) + 0.3·Φ(1) ≈ 0.262 — exactly, at the
    /// boundary-aligned count, an elevenfold difference from the same weights.
    /// </summary>
    [TestMethod]
    public void Test_JensenDoctrine_BlendedVersusEpistemic()
    {
        const double flow = 1000d;
        TabularTransform Rating(string name, double stageAtFlow) => new TabularTransform
        {
            Name = name,
            SpecifiedHazard = "Flow",
            HazardUnit = "cfs",
            TransformedHazard = "Stage",
            TransformedHazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(2000d, new Deterministic(2d * stageAtFlow)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
        CompositeTransform Composite(CompositeFunctionType type) => new CompositeTransform(new[]
        {
            new WeightedTransformFunction(Rating("Rating A", 100d), 0.3d),
            new WeightedTransformFunction(Rating("Rating B", 103d), 0.4d),
            new WeightedTransformFunction(Rating("Rating C", 106d), 0.3d),
        })
        {
            Name = "Rating Tree",
            SpecifiedHazard = "Flow",
            HazardUnit = "cfs",
            TransformedHazard = "Stage",
            TransformedHazardUnit = "ft",
            CompositeFunctionType = type,
        };
        double Fragility(double stage) => Normal.StandardCDF((stage - 105d) / 1d);

        // The blended reading: one consensus stage of 103 ft, then the fragility — Φ(−2).
        double blended = Fragility(Composite(CompositeFunctionType.Average).SampleFunction().Function(flow));
        Assert.AreEqual(Normal.StandardCDF(-2d), blended, 1e-12);

        // The epistemic reading: one rating curve per realization, fragility per branch, then
        // the credence-weighted mean — exact at the boundary-aligned count.
        const int N = 1000;
        var epistemic = Composite(CompositeFunctionType.EpistemicMixture);
        epistemic.SetupSampler(N, 24680, SamplingScheme.LatinHypercube);
        double mean = 0d;
        for (int i = 0; i < N; i++)
        {
            mean += Fragility(epistemic.SampleFunction(i).Function(flow));
        }
        mean /= N;
        double exact = (0.3d * Normal.StandardCDF(-5d)) + (0.4d * Normal.StandardCDF(-2d)) + (0.3d * Normal.StandardCDF(1d));
        Assert.AreEqual(exact, mean, 1e-12, "The epistemic mean is the credence-weighted risk, exactly.");

        // The doctrine's published numbers and the elevenfold gap.
        Assert.AreEqual(0.023d, blended, 5e-4);
        Assert.AreEqual(0.262d, mean, 5e-4);
        Assert.IsTrue(mean / blended > 11d, "The Jensen gap is elevenfold on this fixture.");
    }

    /// <summary>
    /// The shared-variable selection oracle: the run's published branch selections — recovered
    /// per component from the deterministic branch values — match, at every realization, the
    /// selection an independently re-implemented column derivation produces (SHA-256 over the
    /// variable name, the little-endian seed fold, the positive-seed map, and the Latin
    /// hypercube block — no library seed kernel in the oracle); and both binders match each
    /// other. The unbound variant's selections diverge, the anti-oracle.
    /// </summary>
    [TestMethod]
    public async Task Test_SharedVariable_MatchesIndependentDerivation()
    {
        const int N = 512;
        var shared = await RunFull(N,
            Component("Dam A", EpistemicComposite("First Tree", Fragility("A1", 12d), Fragility("A2", 20d), Fragility("A3", 28d), "SOK-Frag")),
            Component("Dam B", EpistemicComposite("Second Tree", Fragility("B1", 14d), Fragility("B2", 22d), Fragility("B3", 26d), "SOK-Frag")));

        int[] BranchSequence(RiskAnalysis analysis, int componentIndex)
        {
            var values = FailureProbabilities(analysis, componentIndex, N);
            var distinct = values.Distinct().OrderByDescending(v => v).ToArray();
            Assert.AreEqual(3, distinct.Length, "Deterministic branches must yield exactly three distinct values.");
            return values.Select(v => Array.IndexOf(distinct, v)).ToArray();
        }

        // The independent column derivation.
        byte[] nameHash = SHA256.HashData(Encoding.UTF8.GetBytes("SOK-Frag"));
        var head = new byte[8];
        BitConverter.TryWriteBytes(head.AsSpan(0, 4), shared.Options.PRNGSeed);
        BitConverter.TryWriteBytes(head.AsSpan(4, 4), 0);
        var payload = new byte[8 + nameHash.Length];
        head.CopyTo(payload, 0);
        nameHash.CopyTo(payload, 8);
        int folded = BitConverter.ToInt32(SHA256.HashData(payload), 0);
        int positive = (int)((uint)folded % int.MaxValue) + 1;
        var column = LatinHypercube.Random(N, 1, positive);

        var firstBranches = BranchSequence(shared, 0);
        var secondBranches = BranchSequence(shared, 1);
        for (int i = 0; i < N; i++)
        {
            int expected = column[i, 0] <= 0.3d ? 0 : column[i, 0] <= 0.7d ? 1 : 2;
            Assert.AreEqual(expected, firstBranches[i], $"Realization {i}: the first binder must select from the shared column.");
            Assert.AreEqual(expected, secondBranches[i], $"Realization {i}: the second binder must select from the shared column.");
        }

        // The anti-oracle: without the variable the two content-distinct composites select
        // from their own content-seeded columns and must disagree somewhere.
        var unbound = await RunFull(N,
            Component("Dam A", EpistemicComposite("First Tree", Fragility("A1", 12d), Fragility("A2", 20d), Fragility("A3", 28d))),
            Component("Dam B", EpistemicComposite("Second Tree", Fragility("B1", 14d), Fragility("B2", 22d), Fragility("B3", 26d))));
        var unboundFirst = BranchSequence(unbound, 0);
        var unboundSecond = BranchSequence(unbound, 1);
        Assert.IsTrue(Enumerable.Range(0, N).Any(i => unboundFirst[i] != unboundSecond[i]),
            "Unbound composites draw independent selectors and cannot align everywhere.");
    }

    /// <summary>
    /// The exact-enumeration convergence oracle — the seed of the future exact logic-tree
    /// enumerator: a two-variable tree (an epistemic hazard pair at 0.5/0.5 crossed with an
    /// epistemic fragility triple at 0.3/0.4/0.3) is enumerated exactly as the weight-product
    /// sum of six deterministic single-combination mean-only runs; the sampled epistemic
    /// ensemble reproduces it within four standard errors, and every realization's value is
    /// exactly one enumerated combination's.
    /// </summary>
    [TestMethod]
    public async Task Test_ExactEnumeration_Convergence()
    {
        const int N = 4096;
        double[] hazardShifts = { 0d, 5d };
        double[] fragilityTops = { 12d, 20d, 28d };
        double[] hazardWeights = { 0.5d, 0.5d };
        double[] fragilityWeights = { 0.3d, 0.4d, 0.3d };

        TabularHazard ShiftedHazard(string name, double shift) => new TabularHazard
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

        // The exact enumeration: six deterministic mean-only runs, weight-product blended.
        double exact = 0d;
        var combinationValues = new List<double>();
        for (int h = 0; h < 2; h++)
        {
            for (int f = 0; f < 3; f++)
            {
                var component = new SystemComponent { Name = "Dam" };
                component.HazardFunction = ShiftedHazard($"Hazard {h}", hazardShifts[h]);
                component.AddFailureMode(new FailureMode(null, null, Fragility($"Frag {f}", fragilityTops[f]), Consequence("Failure Loss", 1000d)));
                component.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Loss", 100d)));
                var analysis = new RiskAnalysis(new[] { component });
                analysis.Options.EstimateMeanRiskOnly = true;
                await analysis.RunAsync();
                Assert.IsTrue(analysis.IsEstimated);
                double value = analysis.MeanRiskResults!.Components[0].Curves.Fail.TotalProbability;
                combinationValues.Add(value);
                exact += hazardWeights[h] * fragilityWeights[f] * value;
            }
        }

        // The sampled logic tree: both selectors are independent epistemic composites.
        var hazardTree = new CompositeHazard(new[]
        {
            new WeightedHazardFunction(ShiftedHazard("Hazard 0", 0d), 0.5d),
            new WeightedHazardFunction(ShiftedHazard("Hazard 1", 5d), 0.5d),
        })
        {
            Name = "Hazard Tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            CompositeCombinationType = CompositeCombinationType.EpistemicMixture,
        };
        var sampledComponent = new SystemComponent { Name = "Dam" };
        sampledComponent.HazardFunction = hazardTree;
        sampledComponent.AddFailureMode(new FailureMode(null, null,
            EpistemicComposite("Fragility Tree", Fragility("Frag 0", 12d), Fragility("Frag 1", 20d), Fragility("Frag 2", 28d)),
            Consequence("Failure Loss", 1000d)));
        sampledComponent.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Loss", 100d)));

        // The ensemble discipline is pinned to the mean-only discipline (1e-8, minimum depth 2)
        // so per-realization integrals are comparable to the mean-only enumeration runs at the
        // exact-match tolerance — the documented ensemble/mean discipline split (defaults 1e-4/0
        // for ensembles) would otherwise separate identical curves by integration tolerance.
        var sampled = new RiskAnalysis(new[] { sampledComponent });
        sampled.Options.UseDefaults = false;
        sampled.Options.EstimateMeanRiskOnly = false;
        sampled.Options.Realizations = N;
        sampled.Options.EnsembleTolerance = 1e-8;
        sampled.Options.EnsembleMinDepth = 2;
        await sampled.RunAsync();
        Assert.IsTrue(sampled.IsEstimated);
        var values = FailureProbabilities(sampled, 0, N);

        // Every realization is exactly one enumerated combination.
        foreach (double value in values)
        {
            Assert.IsTrue(combinationValues.Any(c => Math.Abs(c - value) <= 1e-12),
                "Each realization must reproduce one enumerated branch combination exactly.");
        }

        // Convergence to the exact enumeration at four standard errors.
        double mean = values.Average();
        double variance = values.Select(v => Math.Pow(v - mean, 2d)).Sum() / (N - 1);
        double tolerance = 4d * Math.Sqrt(variance / N);
        Assert.AreEqual(exact, mean, tolerance,
            $"The sampled logic tree must converge to the exact enumeration within 4·SE = {tolerance:E3}.");
    }
}
