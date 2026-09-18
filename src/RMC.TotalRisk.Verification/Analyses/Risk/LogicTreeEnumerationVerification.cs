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

namespace RMC.TotalRisk.Verification.Analyses.Risk;

/// <summary>
/// Exact logic-tree enumeration verification: the closed-form analytic tree whose enumerated
/// per-realization values, exact branch-weight products, weighted mean, and weighted
/// tolerable-risk exceedance fractions are all reproduced against manual single-combination
/// runs and hand-summed weights; the roles-reversed convergence of the sampled epistemic mode
/// to the enumerator; shared-variable forcing across components and through nested composites;
/// the post-hoc weighted re-band bit-equal to the enumerated run's published bands; and the
/// small-weight branch resolved exactly at a fraction of the sampled mode's budget.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Oracles and tolerances:</b> the analytic tree is deterministic per branch combination, so
/// the enumerated run's realizations must equal manual single-combination mean-only runs at
/// 1e-12 with the ensemble integration discipline pinned to the mean-only discipline (1e-8,
/// minimum depth 2 — the documented ensemble/mean split would otherwise separate identical
/// curves by integration tolerance), and the weighted reductions must equal hand-computed
/// weight-product sums at 1e-12. The roles-reversed identity is statistical: the sampled
/// epistemic ensemble's mean converges to the enumerator's exact value at four standard errors
/// of the sampled mean. The re-band identity is byte-exact serialized JSON, the retention
/// synergy's comparison surface. The small-weight demonstration pins the enumerator's exact
/// per-branch allocation and weight mass against the sampled mode's stratified N·ω allocation,
/// with the branch-conditional mean compared to an independent single-branch model at four
/// combined standard errors.
/// </para>
/// </remarks>
[TestClass]
public class LogicTreeEnumerationVerification
{
    #region Fixtures

    /// <summary>Builds the deterministic stage-frequency hazard, shifted by the given offset.</summary>
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

    /// <summary>Builds an epistemic fragility composite over three children at 0.3/0.4/0.3.</summary>
    private static CompositeResponse EpistemicTriple(string name, IResponseFunction a, IResponseFunction b, IResponseFunction c, string? variable = null)
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

    /// <summary>Builds a single-mode component around the given fragility.</summary>
    private static SystemComponent Component(string name, IResponseFunction response)
    {
        var component = new SystemComponent { Name = name };
        component.HazardFunction = StageFrequency();
        component.AddFailureMode(new FailureMode(null, null, response, Consequence("Failure Loss", 300d)));
        component.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Loss", 60d)));
        return component;
    }

    /// <summary>Builds the two-axis analytic-tree component: an unbound epistemic hazard pair crossed with an unbound epistemic fragility triple.</summary>
    private static SystemComponent AnalyticTreeComponent()
    {
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
            EpistemicTriple("Fragility Tree", Fragility("Frag 0", 12d), Fragility("Frag 1", 20d), Fragility("Frag 2", 28d)),
            Consequence("Failure Loss", 300d)));
        component.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Loss", 60d)));
        return component;
    }

    /// <summary>Computes the six analytic-tree single-combination mean-only failure probabilities.</summary>
    private static async Task<double[,]> AnalyticTreeReferences()
    {
        double[] hazardShifts = { 0d, 5d };
        double[] fragilityTops = { 12d, 20d, 28d };
        var references = new double[2, 3];
        for (int h = 0; h < 2; h++)
        {
            for (int f = 0; f < 3; f++)
            {
                var component = new SystemComponent { Name = "Dam" };
                component.HazardFunction = StageFrequency($"Hazard {h}", hazardShifts[h]);
                component.AddFailureMode(new FailureMode(null, null, Fragility($"Frag {f}", fragilityTops[f]), Consequence("Failure Loss", 300d)));
                component.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Loss", 60d)));
                references[h, f] = await MeanFailureProbability(component);
            }
        }
        return references;
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

    /// <summary>
    /// Builds an enumeration analysis with the ensemble integration discipline pinned to the
    /// mean-only discipline (1e-8, minimum depth 2), so per-realization integrals are exactly
    /// comparable to single-combination mean-only reference runs.
    /// </summary>
    private static RiskAnalysis EnumerationAnalysis(int perCombination, params SystemComponent[] components)
    {
        var analysis = new RiskAnalysis(components);
        analysis.Options.UseDefaults = false;
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.EnsembleTolerance = 1e-8;
        analysis.Options.EnsembleMinDepth = 2;
        analysis.LogicTreeEnumerationRealizations = perCombination;
        return analysis;
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
    /// The closed-form analytic tree: a two-axis deterministic logic tree (hazard pair at
    /// 0.5/0.5 crossed with a fragility triple at 0.3/0.4/0.3) enumerated at one realization
    /// per combination. Every realization must equal its decoded combination's manual mean-only
    /// run at 1e-12; the published weight vector must be the exact branch-weight products; the
    /// weighted mean must equal the hand-blended enumeration at 1e-12; and the weighted
    /// tolerable-risk exceedance fractions at thresholds placed between the six atoms must
    /// equal the exact weight sums above each threshold — the exact-weighted-fractile statement
    /// in its convention-free form (the weighted CDF is exact at every level).
    /// </summary>
    [TestMethod]
    public async Task Test_AnalyticTree_ExactWeightedFractiles()
    {
        // Arrange — the six manual single-combination mean-only references.
        var references = await AnalyticTreeReferences();
        double[] hazardWeights = { 0.5d, 0.5d };
        double[] fragilityWeights = { 0.3d, 0.4d, 0.3d };
        var atoms = new List<double>();
        for (int h = 0; h < 2; h++)
        {
            for (int f = 0; f < 3; f++) atoms.Add(references[h, f]);
        }
        var sortedAtoms = atoms.Distinct().OrderBy(v => v).ToArray();
        Assert.IsTrue(sortedAtoms.Length >= 4, "The analytic tree must produce distinct branch-combination values.");

        // Arrange — exceedance criteria between consecutive atoms (the exact weighted CDF probes).
        var analysis = EnumerationAnalysis(1, AnalyticTreeComponent());
        var thresholds = new double[sortedAtoms.Length - 1];
        for (int t = 0; t < thresholds.Length; t++)
        {
            thresholds[t] = 0.5d * (sortedAtoms[t] + sortedAtoms[t + 1]);
            analysis.Options.TolerableRiskCriteria.Add(
                new TolerableRiskCriterion(RiskMeasure.TotalProbability, RiskType.Fail, 0, thresholds[t]));
        }

        // Act
        await analysis.RunAsync();
        Assert.IsTrue(analysis.IsEstimated);
        var map = analysis.LogicTreeEnumeration!;
        var weights = analysis.RiskResults!.RealizationWeights!;

        // Assert — the design and the exact per-realization enumeration.
        Assert.AreEqual(6, map.CombinationCount);
        Assert.AreEqual(6, map.RealizationCount);
        int hazardAxis = map.Axes[0].Name == "Hazard Tree" ? 0 : 1;
        int fragilityAxis = 1 - hazardAxis;
        var values = FailureProbabilities(analysis, 0, 6);
        double weightedSum = 0d;
        double weightTotal = 0d;
        double exact = 0d;
        for (int i = 0; i < 6; i++)
        {
            int c = map.CombinationOf(i);
            int h = map.BranchIndexOf(c, hazardAxis);
            int f = map.BranchIndexOf(c, fragilityAxis);
            Assert.AreEqual(references[h, f], values[i], 1e-12,
                $"Realization {i} must equal the manual combination ({h},{f}) run exactly.");
            Assert.AreEqual(map.RealizationWeight(i), weights[i], 0d);
            Assert.AreEqual(hazardWeights[h] * fragilityWeights[f], weights[i], 1e-15,
                "The published weight is the exact branch-weight product.");
            weightedSum += weights[i] * values[i];
            weightTotal += weights[i];
            exact += hazardWeights[h] * fragilityWeights[f] * references[h, f];
        }
        Assert.AreEqual(1d, weightTotal, 1e-12, "The enumeration weights integrate to one.");
        Assert.AreEqual(exact, weightedSum / weightTotal, 1e-12,
            "The weighted mean is the exact weight-blended enumeration.");

        // Assert — every weighted exceedance fraction is the exact weight mass above its
        // threshold: zero Monte Carlo noise on the branch axis, at every CDF level probed.
        var confidence = analysis.RiskResults.Summary!.TolerableRiskConfidence!;
        Assert.AreEqual(thresholds.Length, confidence.Count);
        for (int t = 0; t < thresholds.Length; t++)
        {
            double expected = 0d;
            for (int i = 0; i < 6; i++)
            {
                if (values[i] > thresholds[t]) expected += weights[i];
            }
            expected /= weightTotal;
            Assert.AreEqual(expected, confidence[t].ExceedanceProbability, 1e-12,
                $"Threshold {t}: the exceedance fraction is the exact weight mass above it.");
        }
    }

    /// <summary>
    /// The roles-reversed convergence identity: the enumerator is the exact oracle and the
    /// sampled epistemic mode is the estimator. The enumerated weighted mean must equal the
    /// manual weight-product blend at 1e-12 (the construction the epistemic-mixture family's
    /// enumeration oracle performs by hand), and an independently constructed sampled logic
    /// tree at N = 4,096 must converge to the enumerator's exact mean within four standard
    /// errors of the sampled mean.
    /// </summary>
    [TestMethod]
    public async Task Test_SampledMode_ConvergesToEnumerator()
    {
        // Arrange — the exact enumerator run and the manual blend.
        var references = await AnalyticTreeReferences();
        double[] hazardWeights = { 0.5d, 0.5d };
        double[] fragilityWeights = { 0.3d, 0.4d, 0.3d };
        double manualBlend = 0d;
        for (int h = 0; h < 2; h++)
        {
            for (int f = 0; f < 3; f++) manualBlend += hazardWeights[h] * fragilityWeights[f] * references[h, f];
        }
        var enumerated = EnumerationAnalysis(1, AnalyticTreeComponent());
        await enumerated.RunAsync();
        var map = enumerated.LogicTreeEnumeration!;
        var weights = enumerated.RiskResults!.RealizationWeights!;
        var enumeratedValues = FailureProbabilities(enumerated, 0, map.RealizationCount);
        double enumeratedMean = 0d;
        double weightTotal = 0d;
        for (int i = 0; i < map.RealizationCount; i++)
        {
            enumeratedMean += weights[i] * enumeratedValues[i];
            weightTotal += weights[i];
        }
        enumeratedMean /= weightTotal;
        Assert.AreEqual(manualBlend, enumeratedMean, 1e-12,
            "The enumerator reproduces the manual weight-product blend exactly.");

        // Act — the independently constructed sampled logic tree (its own content walks).
        const int N = 4096;
        var sampled = new RiskAnalysis(new[] { AnalyticTreeComponent() });
        sampled.Options.UseDefaults = false;
        sampled.Options.EstimateMeanRiskOnly = false;
        sampled.Options.Realizations = N;
        sampled.Options.EnsembleTolerance = 1e-8;
        sampled.Options.EnsembleMinDepth = 2;
        await sampled.RunAsync();
        var sampledValues = FailureProbabilities(sampled, 0, N);

        // Assert — every sampled realization is one enumerated atom, and the sampled mean
        // converges to the enumerator at four standard errors.
        foreach (double value in sampledValues)
        {
            Assert.IsTrue(enumeratedValues.Any(v => Math.Abs(v - value) <= 1e-12),
                "Each sampled realization must live on one enumerated branch combination.");
        }
        double sampledMean = sampledValues.Average();
        double variance = sampledValues.Select(v => Math.Pow(v - sampledMean, 2d)).Sum() / (N - 1);
        double tolerance = 4d * Math.Sqrt(variance / N);
        Assert.AreEqual(enumeratedMean, sampledMean, tolerance,
            $"The sampled logic tree must converge to the enumerator within 4·SE = {tolerance:E3}.");
    }

    /// <summary>
    /// Shared-variable forcing across the system: one variable bound by a root composite on one
    /// component and by a composite nested inside an aleatory mixture on another enumerates as
    /// a single three-branch axis, and within every combination block both components hold
    /// their corresponding manual single-branch values exactly — state-of-knowledge correlation
    /// carried into the exact enumeration, nested binders included.
    /// </summary>
    [TestMethod]
    public async Task Test_SharedVariableAxis_ForcesAllBindersIncludingNested()
    {
        // Arrange — manual references. Dam A: the bound root triple's branches. Dam B: the
        // outer aleatory mixture with its nested bound triple replaced by each branch.
        double[] rootTops = { 12d, 20d, 28d };
        double[] nestedTops = { 14d, 22d, 26d };
        var rootReferences = new double[3];
        var nestedReferences = new double[3];
        for (int b = 0; b < 3; b++)
        {
            rootReferences[b] = await MeanFailureProbability(Component("Dam A", Fragility("Branch", rootTops[b])));
            var outer = new CompositeResponse(new[]
            {
                new WeightedResponseFunction(Fragility("Nested Branch", nestedTops[b]), 0.5d),
                new WeightedResponseFunction(Fragility("Plain", 20d), 0.5d),
            })
            {
                Name = "Outer Mixture",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                CompositeCombinationType = CompositeCombinationType.Mixture,
            };
            nestedReferences[b] = await MeanFailureProbability(Component("Dam B", outer));
        }

        // Arrange — the bound model: a root binder and a nested binder of one variable.
        var rootBinder = EpistemicTriple("Root Tree",
            Fragility("Root 0", rootTops[0]), Fragility("Root 1", rootTops[1]), Fragility("Root 2", rootTops[2]), "SOK-Frag");
        var nestedBinder = EpistemicTriple("Nested Tree",
            Fragility("Nested 0", nestedTops[0]), Fragility("Nested 1", nestedTops[1]), Fragility("Nested 2", nestedTops[2]), "SOK-Frag");
        var outerMixture = new CompositeResponse(new[]
        {
            new WeightedResponseFunction(nestedBinder, 0.5d),
            new WeightedResponseFunction(Fragility("Plain", 20d), 0.5d),
        })
        {
            Name = "Outer Mixture",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            CompositeCombinationType = CompositeCombinationType.Mixture,
        };

        // Act — one shared axis, three combinations, four realizations per block.
        var analysis = EnumerationAnalysis(4, Component("Dam A", rootBinder), Component("Dam B", outerMixture));
        await analysis.RunAsync();
        var map = analysis.LogicTreeEnumeration!;

        // Assert — the axis shape, the exact per-realization weights, and both binders forced.
        Assert.AreEqual(1, map.Axes.Count);
        Assert.IsTrue(map.Axes[0].IsSharedVariable);
        Assert.AreEqual("SOK-Frag", map.Axes[0].Name);
        Assert.AreEqual(3, map.CombinationCount);
        Assert.AreEqual(12, map.RealizationCount);
        double[] branchWeights = { 0.3d, 0.4d, 0.3d };
        var storedWeights = analysis.RiskResults!.RealizationWeights!;
        var first = FailureProbabilities(analysis, 0, 12);
        var second = FailureProbabilities(analysis, 1, 12);
        for (int i = 0; i < 12; i++)
        {
            int branch = map.BranchIndexOf(map.CombinationOf(i), 0);
            Assert.AreEqual(rootReferences[branch], first[i], 1e-12,
                $"Realization {i}: the root binder holds branch {branch}.");
            Assert.AreEqual(nestedReferences[branch], second[i], 1e-12,
                $"Realization {i}: the nested binder holds the same branch.");
            Assert.AreEqual(branchWeights[branch] / 4d, storedWeights[i], 1e-15,
                "The realization weight is the branch weight over the block size.");
        }
    }

    /// <summary>
    /// The retention composition on the enumerated vector: re-banding a retained enumerated
    /// ensemble under the run's own published weights reproduces the published percentile
    /// bands byte-for-byte — the weighted assembly is a pure reduction over the retained state,
    /// so the enumeration's exact weights compose with post-hoc re-banding with no
    /// re-simulation and no drift.
    /// </summary>
    [TestMethod]
    public async Task Test_PostHocReband_BitEqualToPublished()
    {
        // Arrange — a retained enumerated run over the analytic tree at four per combination.
        var analysis = EnumerationAnalysis(4, AnalyticTreeComponent());
        analysis.RetainRealizations = true;
        await analysis.RunAsync();
        Assert.IsTrue(analysis.IsEstimated);
        var weights = analysis.RiskResults!.RealizationWeights!.ToArray();
        Assert.AreEqual(24, analysis.RetainedRealizations!.Count);

        // Act — the post-hoc re-band under the run's own exact weights.
        var bands = analysis.ReassemblePercentileBands(weights);

        // Assert — byte-equal to the published bands (manifests stamped onto the fresh objects).
        bands.Lower!.Manifest = analysis.LowerRiskResults!.Manifest;
        bands.Upper!.Manifest = analysis.UpperRiskResults!.Manifest;
        bands.Median!.Manifest = analysis.MedianRiskResults!.Manifest;
        bands.Mean!.Manifest = analysis.MeanRiskResults!.Manifest;
        Assert.AreEqual(analysis.LowerRiskResults.ToJson(), bands.Lower.ToJson(),
            "The post-hoc weighted re-band must equal the enumerated run's published lower band.");
        Assert.AreEqual(analysis.UpperRiskResults.ToJson(), bands.Upper.ToJson());
        Assert.AreEqual(analysis.MedianRiskResults!.ToJson(), bands.Median.ToJson());
        Assert.AreEqual(analysis.MeanRiskResults.ToJson(), bands.Mean.ToJson());
    }

    /// <summary>
    /// The small-weight branch cost demonstration: a 0.02-weight branch with continuous
    /// knowledge uncertainty receives exactly the configured per-combination allocation and
    /// exactly its weight mass from the enumerator (64 conditional realizations from a
    /// 128-realization run), while the sampled mode's stratified selector allocates it exactly
    /// N·ω (20 of 1,000) — so the enumerator resolves the rare branch's conditional
    /// distribution with more than three times the sample at an eighth of the budget. The
    /// enumerated branch-conditional mean is verified against an independent single-branch
    /// model at four combined standard errors.
    /// </summary>
    [TestMethod]
    public async Task Test_SmallWeightBranch_ExactResolutionAtLowCost()
    {
        // Arrange — the rare/common epistemic pair with uncertain branch chains.
        CompositeResponse BuildPair(string suffix) => new CompositeResponse(new[]
        {
            new WeightedResponseFunction(UncertainFragility($"Rare {suffix}", 0.3d, 0.9d), 0.02d),
            new WeightedResponseFunction(UncertainFragility($"Common {suffix}", 0.05d, 0.4d), 0.98d),
        })
        {
            Name = $"Interpretation Pair {suffix}",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            CompositeCombinationType = CompositeCombinationType.EpistemicMixture,
        };

        // Act — the enumerator at 64 per combination (N = 128).
        const int PerCombination = 64;
        var enumerated = new RiskAnalysis(new[] { Component("Dam", BuildPair("E")) });
        enumerated.Options.EstimateMeanRiskOnly = false;
        enumerated.LogicTreeEnumerationRealizations = PerCombination;
        await enumerated.RunAsync();
        var map = enumerated.LogicTreeEnumeration!;
        Assert.AreEqual(2, map.CombinationCount);
        Assert.AreEqual(128, map.RealizationCount);
        int rareCombination = map.Axes[0].Branches[map.BranchIndexOf(0, 0)].Weight <= 0.02d ? 0 : 1;

        // Assert — the exact weight mass and the exact conditional allocation.
        var storedWeights = enumerated.RiskResults!.RealizationWeights!;
        double rareMass = 0d;
        double totalMass = 0d;
        int rareCount = 0;
        var rareValues = new List<double>(PerCombination);
        var enumeratedValues = FailureProbabilities(enumerated, 0, 128);
        for (int i = 0; i < 128; i++)
        {
            totalMass += storedWeights[i];
            if (map.CombinationOf(i) == rareCombination)
            {
                rareMass += storedWeights[i];
                rareCount++;
                rareValues.Add(enumeratedValues[i]);
            }
        }
        Assert.AreEqual(PerCombination, rareCount, "The rare branch holds exactly one block.");
        Assert.AreEqual(0.02d, rareMass / totalMass, 1e-12, "The rare branch carries exactly its credence.");

        // Assert — the branch-conditional mean against an independent single-branch model.
        var direct = new RiskAnalysis(new[] { Component("Dam", UncertainFragility("Rare D", 0.3d, 0.9d)) });
        direct.Options.EstimateMeanRiskOnly = false;
        direct.Options.Realizations = 128;
        await direct.RunAsync();
        var directValues = FailureProbabilities(direct, 0, 128);
        double rareMean = rareValues.Average();
        double directMean = directValues.Average();
        double rareVariance = rareValues.Select(v => Math.Pow(v - rareMean, 2d)).Sum() / (rareValues.Count - 1);
        double directVariance = directValues.Select(v => Math.Pow(v - directMean, 2d)).Sum() / (directValues.Length - 1);
        double tolerance = 4d * Math.Sqrt((rareVariance / rareValues.Count) + (directVariance / directValues.Length));
        Assert.AreEqual(directMean, rareMean, tolerance,
            $"The enumerated rare-branch conditional mean must match the independent branch model within 4·SE = {tolerance:E3}.");

        // Assert — the sampled mode's stratified allocation gives the rare branch exactly N·ω,
        // recovered from the author component's bit-exact public re-derivation (unbound
        // composites re-derive the run's own streams).
        const int SampledN = 1000;
        var sampledPair = BuildPair("S");
        var sampledComponent = Component("Dam", sampledPair);
        var sampled = new RiskAnalysis(new[] { sampledComponent });
        sampled.Options.EstimateMeanRiskOnly = false;
        sampled.Options.Realizations = SampledN;
        await sampled.RunAsync();
        Assert.IsTrue(sampled.IsEstimated);
        var components = new List<SystemComponent>(sampled.Components);
        SystemComponent.AssignOccurrenceIndices(components);
        int seed = SeedHelpers.HashCombine(sampled.Options.PRNGSeed, sampledComponent.CanonicalHash(), sampledComponent.OccurrenceIndex);
        sampledComponent.SetupSamplers(SampledN, seed, sampled.Options.SamplingScheme);
        int sampledRareCount = 0;
        for (int i = 0; i < SampledN; i++)
        {
            if (sampledPair.SelectedBranchIndex(i) == 0) sampledRareCount++;
        }
        Assert.AreEqual((int)(SampledN * 0.02d), sampledRareCount,
            "The sampled selector allocates the rare branch exactly N·ω of the budget.");
    }
}
