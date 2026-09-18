using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses.CostBenefit;
using RMC.TotalRisk.Analyses.CostBenefit.Support;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Verification.Analyses.CostBenefit;

/// <summary>
/// Decision-strategy catalog evidence: the shared-state regret machinery over genuinely
/// enumerated ensembles with the epistemic-alternative refusal, the classical epistemic rule
/// picks, the exact tail averages, the stochastic-dominance verdicts, the expected-utility
/// closed forms with the partition-to-CVaR pin, the chance-constraint parity, the designed
/// decision-summary disagreement, study author-inertness with run-to-run reproducibility, and
/// the strategy validation sweep.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// </remarks>
public partial class CostBenefitVerification
{
    #region Strategy Fixtures

    /// <summary>Builds a flat deterministic response over the fixture's stage range.</summary>
    /// <param name="name">The function name.</param>
    /// <param name="probability">The flat system response probability.</param>
    /// <returns>The response.</returns>
    private static TabularResponse FlatBranchResponse(string name, double probability)
    {
        return new TabularResponse
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(40d, new Deterministic(probability)),
                    new UncertainOrdinate(280d, new Deterministic(probability)),
                },
                true, SortOrder.Ascending, false, SortOrder.None,
                UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>
    /// Builds a flat one-component system whose fragility is a shared-variable epistemic
    /// mixture over three deterministic branches at weights 0.2/0.5/0.3, over the family's
    /// 1000-dollar failure consequence and 100-dollar background.
    /// </summary>
    /// <param name="name">The component name.</param>
    /// <param name="branchProbabilities">The three branch failure probabilities.</param>
    /// <returns>The analysis.</returns>
    private static RiskAnalysis BuildEnumeratedSystem(string name, double[] branchProbabilities)
    {
        var composite = new CompositeResponse(new[]
        {
            new WeightedResponseFunction(FlatBranchResponse($"{name} low", branchProbabilities[0]), 0.2d),
            new WeightedResponseFunction(FlatBranchResponse($"{name} mid", branchProbabilities[1]), 0.5d),
            new WeightedResponseFunction(FlatBranchResponse($"{name} high", branchProbabilities[2]), 0.3d),
        })
        {
            Name = $"{name} fragility tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            CompositeCombinationType = CompositeCombinationType.EpistemicMixture,
            EpistemicVariable = "θ",
        };
        var component = new SystemComponent { Name = name };
        component.HazardFunction = StageFrequency();
        component.AddFailureMode(new FailureMode(null, null, composite,
            FlatConsequence("Failure damages", 1000d)));
        component.AddFailureMode(new FailureMode(null, null, new NonFailResponse { Name = "Background" },
            FlatConsequence("Background damages", 100d)));
        return new RiskAnalysis(new[] { component })
        {
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
        };
    }

    /// <summary>
    /// Builds a flat one-component system with an uncertain (triangular) flat fragility over
    /// the 1000-dollar failure consequence and 100-dollar background, so full-uncertainty
    /// realizations vary.
    /// </summary>
    /// <param name="name">The component name.</param>
    /// <param name="mode">The fragility's triangular mode (±0.1 support, clamped to [0, 1]).</param>
    /// <returns>The analysis.</returns>
    private static RiskAnalysis BuildUncertainSystem(string name, double mode)
    {
        var response = new TabularResponse
        {
            Name = $"{name} fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(40d,
                        new Triangular(Math.Max(0d, mode - 0.1d), mode, Math.Min(1d, mode + 0.1d))),
                    new UncertainOrdinate(280d,
                        new Triangular(Math.Max(0d, mode - 0.1d), mode, Math.Min(1d, mode + 0.1d))),
                },
                true, SortOrder.Ascending, false, SortOrder.None,
                UnivariateDistributionType.Triangular),
        };
        var component = new SystemComponent { Name = name };
        component.HazardFunction = StageFrequency();
        component.AddFailureMode(new FailureMode(null, null, response,
            FlatConsequence("Failure damages", 1000d)));
        component.AddFailureMode(new FailureMode(null, null, new NonFailResponse { Name = "Background" },
            FlatConsequence("Background damages", 100d)));
        return new RiskAnalysis(new[] { component })
        {
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
        };
    }

    /// <summary>
    /// Builds a flat one-component system at an arbitrary failure probability and failure
    /// consequence over the 100-dollar background.
    /// </summary>
    /// <param name="failureProbability">The flat system response probability.</param>
    /// <param name="failureConsequence">The flat failure damages.</param>
    /// <returns>The analysis.</returns>
    private static RiskAnalysis BuildScaledFlatSystem(double failureProbability, double failureConsequence)
    {
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = StageFrequency();
        component.AddFailureMode(new FailureMode(null, null,
            FlatBranchResponse("Flat response", failureProbability),
            FlatConsequence("Failure damages", failureConsequence)));
        component.AddFailureMode(new FailureMode(null, null, new NonFailResponse { Name = "Background" },
            FlatConsequence("Background damages", 100d)));
        return new RiskAnalysis(new[] { component })
        {
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
        };
    }

    /// <summary>
    /// Builds an exact atom curve from point masses: each mass sits between duplicated
    /// consequence knots (a flat log-log segment), so the mass-pair extraction is exact.
    /// </summary>
    /// <param name="masses">The point masses, largest consequence first.</param>
    /// <returns>The curve.</returns>
    private static Curve AtomCurve(params (double Consequence, double Mass)[] masses)
    {
        var consequences = new List<double>();
        var probabilities = new List<double>();
        double cumulative = 0d;
        for (int i = 0; i < masses.Length; i++)
        {
            consequences.Add(masses[i].Consequence);
            probabilities.Add(cumulative);
            cumulative += masses[i].Mass;
            consequences.Add(masses[i].Consequence);
            probabilities.Add(cumulative);
        }
        return new Curve
        {
            LECConsequences = consequences.ToArray(),
            LECProbabilities = probabilities.ToArray(),
            TotalProbability = cumulative,
        };
    }

    /// <summary>Builds a hand realization summary whose Total-stream mean is the given value.</summary>
    /// <param name="totalMean">The Total-stream mean.</param>
    /// <returns>The summary.</returns>
    private static SystemRiskResults HandRealization(double totalMean)
    {
        var summary = new SystemRiskResults();
        summary.Total.Mean = totalMean;
        return summary;
    }

    /// <summary>Runs one system at full uncertainty (100 realizations).</summary>
    /// <param name="system">The system.</param>
    private static void RunFullUncertainty(RiskAnalysis system)
    {
        system.Options.EstimateMeanRiskOnly = false;
        system.Options.Realizations = 100;
        system.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(system.IsEstimated);
        Assert.AreEqual(100, system.RiskResults!.Count, "The full ensemble must publish.");
    }

    /// <summary>Finds the first ranking of one strategy, or null.</summary>
    /// <param name="results">The study results.</param>
    /// <param name="strategy">The strategy family.</param>
    /// <returns>The ranking, or null.</returns>
    private static StrategyRanking? FindRanking(CostBenefitResults results, DecisionStrategy strategy)
    {
        for (int i = 0; i < results.StrategyRankings.Count; i++)
        {
            if (results.StrategyRankings[i].Strategy == strategy) return results.StrategyRankings[i];
        }
        return null;
    }

    #endregion

    /// <summary>
    /// The shared-state Savage regret machinery over genuinely enumerated ensembles, with the
    /// live-study refusal pinned. Two flat systems share one epistemic fragility variable (three
    /// deterministic branches at exact weights 0.2/0.5/0.3) and are enumerated at four
    /// realizations per combination (K = 3, M = 4, N = 12), so every realization's Total mean is
    /// the closed form 100 + 900·p of its branch. The regret machinery — the block means through
    /// the engine's scope-and-measure extraction, the regret matrices, the aggregates, and the
    /// ranking picks — is driven at the engine seam and asserted bit-exactly against an
    /// independent oracle that reads the published realizations directly and re-derives every
    /// quantity with its own sequential loops in mirrored order. The repaired branches are
    /// deliberately unsorted against the baseline's (best where the baseline is worst), so the
    /// shared-state pairing matters: the Tier-2 quantile regret — which compares sorted
    /// weighted marginals and forgets the pairing — is computed alongside and pinned as a
    /// different number from the shared-state maximum regret. The two regret objects must
    /// never be conflated. The live
    /// study over these systems is refused by design: its life-cycle trajectories are mean-only
    /// quantifications, which cannot select an epistemic branch, so validation errors and the
    /// run throws — the machinery is therefore verified here at the seam, over exactly the
    /// published state a future epistemic-capable study would read.
    /// </summary>
    [TestMethod]
    public void Test_SavageRegret_EnumeratedSeamAndStudyRefusal()
    {
        // Arrange — enumerate both systems (K = 3, M = 4, N = 12) at the mean-grade ensemble
        // discipline so per-realization integrals sit on the closed forms.
        double[] baselineBranches = { 0.10d, 0.20d, 0.40d };
        double[] repairedBranches = { 0.30d, 0.10d, 0.05d };
        RiskAnalysis baseline = BuildEnumeratedSystem("Existing", baselineBranches);
        RiskAnalysis repaired = BuildEnumeratedSystem("Repaired", repairedBranches);
        foreach (RiskAnalysis system in new[] { baseline, repaired })
        {
            system.Options.UseDefaults = false;
            system.Options.EstimateMeanRiskOnly = false;
            system.Options.Realizations = 100;
            system.Options.EnsembleTolerance = 1e-8;
            system.Options.EnsembleMinDepth = 2;
            system.LogicTreeEnumerationRealizations = 4;
            system.RunAsync().GetAwaiter().GetResult();
            Assert.IsTrue(system.IsEstimated);
        }
        LogicTreeEnumerationMap map = baseline.LogicTreeEnumeration!;
        Assert.AreEqual(3, map.CombinationCount);
        Assert.AreEqual(4, map.RealizationsPerCombination);
        Assert.AreEqual(12, map.RealizationCount);

        // The exact weights are the normalized declared branch weights, and every realization's
        // Total mean is its branch's closed form at the flat-quadrature grade.
        double weightSum = 0.2d + 0.5d + 0.3d;
        var declaredWeights = new[] { 0.2d / weightSum, 0.5d / weightSum, 0.3d / weightSum };
        var systems = new[] { baseline, repaired };
        var branches = new[] { baselineBranches, repairedBranches };
        for (int c = 0; c < 3; c++)
        {
            Assert.AreEqual(declaredWeights[c], map.CombinationWeights[c],
                Math.Abs(declaredWeights[c]) * 1e-15,
                "The combination weights are the normalized declared branch weights.");
            for (int a = 0; a < 2; a++)
            {
                for (int r = 4 * c; r < 4 * (c + 1); r++)
                {
                    double closedForm = 100d + 900d * branches[a][c];
                    Assert.AreEqual(closedForm,
                        systems[a].RiskResults![r]!.Total.Mean, closedForm * 1e-9,
                        "A branch realization's Total mean is the closed form 100 + 900·p.");
                }
            }
        }

        // Act — the machinery side: block means through the engine's scope-and-measure
        // extraction (the wiring's sequential sum-over-count convention), the regret
        // computation, and the ranking picks.
        var names = new[] { "Existing condition", "Gate repair" };
        var stateLabels = new string[3];
        var stateWeights = new double[3];
        for (int c = 0; c < 3; c++)
        {
            stateLabels[c] = $"θ = branch {map.BranchIndexOf(c, 0)}";
            stateWeights[c] = map.CombinationWeights[c];
        }
        var blockMeans = new double[2][];
        var blockErrors = new double[2][];
        for (int a = 0; a < 2; a++)
        {
            blockMeans[a] = new double[3];
            blockErrors[a] = new double[3];
            for (int c = 0; c < 3; c++)
            {
                int surviving = 0;
                double sum = 0d;
                double squared = 0d;
                for (int r = 4 * c; r < 4 * (c + 1); r++)
                {
                    SystemRiskResults summary = systems[a].RiskResults![r]!;
                    SummaryRiskResults? stream = RiskAnalysis.SelectScope(summary, -1, -1, RiskType.Total, 0);
                    double value = stream != null ? RiskAnalysis.ExtractMeasure(stream, RiskMeasure.Mean) : double.NaN;
                    if (double.IsNaN(value)) continue;
                    sum += value;
                    surviving++;
                }
                double mean = sum / surviving;
                for (int r = 4 * c; r < 4 * (c + 1); r++)
                {
                    double deviation = systems[a].RiskResults![r]!.Total.Mean - mean;
                    squared += deviation * deviation;
                }
                blockMeans[a][c] = mean;
                blockErrors[a][c] = Math.Sqrt(squared / (surviving - 1)) / Math.Sqrt(4d);
            }
        }
        var diagnostics = new List<ComputationDiagnostic>();
        RegretEngine.RegretComputation computation = RegretEngine.Compute("Mean of Total type 0",
            ObjectiveDirection.Minimize, names, stateLabels, stateWeights, blockMeans, blockErrors,
            diagnostics)!;
        StrategyRanking minimax = DecisionStrategyEngine.Rank(DecisionStrategy.MinimaxRegret, 3,
            DecisionStrategyEngine.LayerSharedState, DecisionStrategyEngine.DisciplineBlockNoise,
            "Mean of Total type 0", string.Empty, ObjectiveDirection.Minimize, names,
            computation.MaxRegrets, new[] { true, true });
        StrategyRanking expected = DecisionStrategyEngine.Rank(DecisionStrategy.ExpectedRegret, 3,
            DecisionStrategyEngine.LayerSharedState, DecisionStrategyEngine.DisciplineBlockNoise,
            "Mean of Total type 0", string.Empty, ObjectiveDirection.Minimize, names,
            computation.ExpectedRegrets, new[] { true, true });

        // Assert — the independent oracle reads the published realizations directly and
        // re-derives every quantity with its own sequential loops in mirrored order.
        Assert.AreEqual(0, diagnostics.Count, "Deterministic branches drop no state.");
        for (int a = 0; a < 2; a++)
        {
            double oracleMax = 0d;
            double oracleExpected = 0d;
            for (int c = 0; c < 3; c++)
            {
                double oracleSum = 0d;
                for (int r = 4 * c; r < 4 * (c + 1); r++)
                {
                    oracleSum += systems[a].RiskResults![r]!.Total.Mean;
                }
                double oracleMean = oracleSum / 4;
                Assert.AreEqual(oracleMean, computation.Values[a][c], 0d,
                    "The block mean must equal the oracle's mirrored sum bit-exactly.");
                Assert.AreEqual(0d, computation.StandardErrors[a][c], 0d,
                    "Identical realizations within a deterministic block carry zero block noise.");

                double otherSum = 0d;
                for (int r = 4 * c; r < 4 * (c + 1); r++)
                {
                    otherSum += systems[1 - a].RiskResults![r]!.Total.Mean;
                }
                double best = Math.Min(oracleMean, otherSum / 4);
                double regret = oracleMean - best;
                Assert.AreEqual(regret, computation.Regrets[a][c], 0d);
                if (regret > oracleMax) oracleMax = regret;
                oracleExpected += stateWeights[c] * regret;
            }
            Assert.AreEqual(oracleMax, computation.MaxRegrets[a], 0d);
            Assert.AreEqual(oracleExpected, computation.ExpectedRegrets[a], 0d);
        }
        Assert.AreEqual(1, minimax.RecommendedIndex,
            "The repair's worst paired regret (180) beats the baseline's (315).");
        Assert.AreEqual(1, expected.RecommendedIndex);
        CollectionAssert.AreEqual(new[] { 1, 2 }, computation.WinCounts,
            "The baseline wins the low state; the repair wins the middle and high states.");

        // The Tier-2 quantile regret over the same stored ensembles is a different object: the
        // baseline's maximum regret over the weighted band quantiles is not the shared-state
        // maximum regret (the weighted quantiles interpolate between branch atoms).
        var levels = new[] { 0.05d, 0.95d, 0.5d };
        var quantiles = new double[2][];
        for (int a = 0; a < 2; a++)
        {
            var values = new double[12];
            var weights = new double[12];
            int used = EpistemicCriteriaEngine.CriterionSample(systems[a].RiskResults!.Realizations,
                systems[a].RiskResults!.RealizationWeights, RiskType.Total, 0, RiskMeasure.Mean,
                values, weights);
            Assert.AreEqual(12, used);
            quantiles[a] = EpistemicCriteriaEngine.WeightedPercentiles(values, weights, used, levels);
        }
        double quantileRegret = 0d;
        for (int q = 0; q < levels.Length; q++)
        {
            double regretAtLevel = quantiles[0][q] - Math.Min(quantiles[0][q], quantiles[1][q]);
            if (regretAtLevel > quantileRegret) quantileRegret = regretAtLevel;
        }
        Assert.AreNotEqual(computation.MaxRegrets[0], quantileRegret,
            "Quantile regret and shared-state regret are different objects and must not collapse.");

        // The live-study refusal: validation errors and the run throws before any epoch.
        var study = new CostBenefitAnalysis(new CostBenefitOptions(30, 0.05d,
            monetization: new ConsequenceMonetization()));
        study.Alternatives.Add(new RiskReductionAlternative("Existing condition", baseline));
        study.Alternatives.Add(new RiskReductionAlternative("Gate repair", repaired,
            new CostStream(new[] { new CapitalCostEntry(0, 1000d) })));
        study.Baseline = study.Alternatives[0];
        (bool isValid, List<string> messages) = study.Validate();
        Assert.IsFalse(isValid);
        bool refused = false;
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i].StartsWith("Error: Alternative 'Existing condition' carries an epistemic-mixture composite",
                StringComparison.Ordinal))
            {
                refused = true;
            }
        }
        Assert.IsTrue(refused, "The epistemic alternative must be refused by name at validation.");
        var thrown = Assert.ThrowsException<InvalidOperationException>(
            () => study.RunAsync().GetAwaiter().GetResult());
        StringAssert.Contains(thrown.Message, "carries an epistemic-mixture composite");
    }

    /// <summary>
    /// The classical epistemic rule picks on hand ensembles: Laplace, Wald maximin, maximax,
    /// Hurwicz at 0, ½, and 1, and mean plus k standard deviations, each pinned against
    /// independent arithmetic — and the Hurwicz endpoints reproducing the extremes bit-exactly.
    /// The first alternative is wide (values 10–40), the second tight (24–27), so the
    /// optimistic rules pick the wide one and the conservative rules the tight one.
    /// </summary>
    [TestMethod]
    public void Test_EpistemicCriteria_ClassicalRulePicks()
    {
        // Arrange — two hand ensembles with weights {1, 2, 3, 2}.
        var wide = new SystemRiskResults?[]
        {
            HandRealization(10d), HandRealization(30d), HandRealization(20d), HandRealization(40d),
        };
        var tight = new SystemRiskResults?[]
        {
            HandRealization(25d), HandRealization(26d), HandRealization(24d), HandRealization(27d),
        };
        var weights = new[] { 1d, 2d, 3d, 2d };
        var names = new[] { "Wide", "Tight" };
        var eligible = new[] { true, true };
        var ensembles = new[] { wide, tight };
        var means = new double[2];
        var worst = new double[2];
        var best = new double[2];
        var dispersion = new double[2];
        for (int a = 0; a < 2; a++)
        {
            var values = new double[4];
            var sampleWeights = new double[4];
            int used = EpistemicCriteriaEngine.CriterionSample(ensembles[a], weights, RiskType.Total,
                0, RiskMeasure.Mean, values, sampleWeights);
            Assert.AreEqual(4, used);
            means[a] = EpistemicCriteriaEngine.WeightedMean(values, sampleWeights, used);
            worst[a] = EpistemicCriteriaEngine.WeightedExtreme(values, sampleWeights, used,
                worst: true, ObjectiveDirection.Minimize);
            best[a] = EpistemicCriteriaEngine.WeightedExtreme(values, sampleWeights, used,
                worst: false, ObjectiveDirection.Minimize);
            dispersion[a] = means[a]
                + Math.Sqrt(EpistemicCriteriaEngine.WeightedVariance(values, sampleWeights, used));
        }

        // Assert — the independent arithmetic: Σwv/Σw means, the raw extremes, and the picks.
        Assert.AreEqual((10d + 60d + 60d + 80d) / 8d, means[0], 26.25d * 1e-14,
            "The wide weighted mean is 210/8.");
        Assert.AreEqual((25d + 52d + 72d + 54d) / 8d, means[1], 25.375d * 1e-14,
            "The tight weighted mean is 203/8.");
        Assert.AreEqual(40d, worst[0], 0d);
        Assert.AreEqual(27d, worst[1], 0d);
        Assert.AreEqual(10d, best[0], 0d);
        Assert.AreEqual(24d, best[1], 0d);

        StrategyRanking laplace = DecisionStrategyEngine.Rank(DecisionStrategy.Laplace, 2,
            DecisionStrategyEngine.LayerEpistemic, DecisionStrategyEngine.DisciplineEpistemic,
            "Mean of Total type 0", string.Empty, ObjectiveDirection.Minimize, names, means, eligible);
        StrategyRanking maximin = DecisionStrategyEngine.Rank(DecisionStrategy.WaldMaximin, 2,
            DecisionStrategyEngine.LayerEpistemic, DecisionStrategyEngine.DisciplineEpistemic,
            "Mean of Total type 0", string.Empty, ObjectiveDirection.Minimize, names, worst, eligible);
        StrategyRanking maximax = DecisionStrategyEngine.Rank(DecisionStrategy.Maximax, 2,
            DecisionStrategyEngine.LayerEpistemic, DecisionStrategyEngine.DisciplineEpistemic,
            "Mean of Total type 0", string.Empty, ObjectiveDirection.Minimize, names, best, eligible);
        StrategyRanking dispersionRanking = DecisionStrategyEngine.Rank(DecisionStrategy.MeanPlusDispersion, 2,
            DecisionStrategyEngine.LayerEpistemic, DecisionStrategyEngine.DisciplineEpistemic,
            "Mean of Total type 0", "k = 1", ObjectiveDirection.Minimize, names, dispersion, eligible);
        Assert.AreEqual(1, laplace.RecommendedIndex, "203/8 < 210/8.");
        Assert.AreEqual(1, maximin.RecommendedIndex, "The tight worst case 27 beats 40.");
        Assert.AreEqual(0, maximax.RecommendedIndex, "The wide best case 10 beats 24.");
        Assert.AreEqual(1, dispersionRanking.RecommendedIndex,
            "The wide spread dominates its mean advantage under mean + k·σ.");

        // Hurwicz: the endpoints reproduce the extremes bit-exactly, and the half blend picks
        // the wide alternative (25 < 25.5) by the independent blend arithmetic.
        for (int a = 0; a < 2; a++)
        {
            Assert.AreEqual(best[a], EpistemicCriteriaEngine.HurwiczBlend(best[a], worst[a], 1d), 0d);
            Assert.AreEqual(worst[a], EpistemicCriteriaEngine.HurwiczBlend(best[a], worst[a], 0d), 0d);
        }
        var half = new double[2];
        for (int a = 0; a < 2; a++)
        {
            half[a] = EpistemicCriteriaEngine.HurwiczBlend(best[a], worst[a], 0.5d);
            Assert.AreEqual(0.5d * best[a] + 0.5d * worst[a], half[a], 0d);
        }
        StrategyRanking hurwicz = DecisionStrategyEngine.Rank(DecisionStrategy.Hurwicz, 2,
            DecisionStrategyEngine.LayerEpistemic, DecisionStrategyEngine.DisciplineEpistemic,
            "Mean of Total type 0", "α = 0.5", ObjectiveDirection.Minimize, names, half, eligible);
        Assert.AreEqual(0, hurwicz.RecommendedIndex, "0.5·10 + 0.5·40 = 25 < 25.5.");
    }

    /// <summary>
    /// The epistemic tail average's exact arithmetic: the boundary realization's partial
    /// weight completing exactly the tail share (hand fraction (0.1·100 + 0.1·80)/0.2 = 90),
    /// a tail boundary landing exactly on a weight edge (88), the favorable tail under
    /// Maximize, index tie-breaking, and the single-realization and equal-value degenerates —
    /// all exact, no tolerance.
    /// </summary>
    [TestMethod]
    public void Test_EpistemicTailAverage_ExactDegenerates()
    {
        // Arrange — five realizations, total weight one.
        var values = new[] { 100d, 80d, 60d, 40d, 20d };
        var weights = new[] { 0.1d, 0.15d, 0.3d, 0.25d, 0.2d };

        // Act / Assert — the partial split: target 0.2 takes 100 at 0.1 and 80 at 0.1 of 0.15.
        Assert.AreEqual((0.1d * 100d + 0.1d * 80d) / 0.2d,
            EpistemicCriteriaEngine.TailAverage(values, weights, 5, 0.2d, ObjectiveDirection.Minimize), 0d);
        // The boundary landing exactly on a weight edge: target 0.25 consumes 0.1 and 0.15 whole.
        Assert.AreEqual((0.1d * 100d + 0.15d * 80d) / 0.25d,
            EpistemicCriteriaEngine.TailAverage(values, weights, 5, 0.25d, ObjectiveDirection.Minimize), 0d);
        // The favorable tail under Maximize is the small-value side: target 0.2 takes 20 at 0.2.
        Assert.AreEqual(20d,
            EpistemicCriteriaEngine.TailAverage(values, weights, 5, 0.2d, ObjectiveDirection.Maximize), 0d);
        // Value ties break by realization index.
        Assert.AreEqual(50d, EpistemicCriteriaEngine.TailAverage(new[] { 50d, 50d, 10d },
            new[] { 0.25d, 0.5d, 0.25d }, 3, 0.25d, ObjectiveDirection.Minimize), 0d);
        // Degenerates: a single realization and an equal-value ensemble, at parameters whose
        // target weight is exact (0.25 · 4 = 1) so the identities hold with no rounding.
        Assert.AreEqual(7d, EpistemicCriteriaEngine.TailAverage(new[] { 7d }, new[] { 4d }, 1,
            0.25d, ObjectiveDirection.Minimize), 0d);
        Assert.AreEqual(3d, EpistemicCriteriaEngine.TailAverage(new[] { 3d, 3d, 3d },
            new[] { 1d, 2d, 1d }, 3, 0.25d, ObjectiveDirection.Minimize), 0d);
    }

    /// <summary>
    /// The stochastic-dominance verdicts on constructed loss-exceedance pairs with analytic
    /// arguments: clean first-order dominance (a uniform doubling of consequences), the
    /// second-order-only verdict for a concentrated loss against a spread of slightly larger
    /// mean (the exceedance curves cross between knots, so the interior crossing abscissa is
    /// exercised), and a non-comparable pair whose means and tails disagree. The quantile tail
    /// integral behind the stop-loss transform is cross-checked against an independent
    /// closed-form segment integration, and the epistemic weighted-sample variant covers the
    /// pointwise, certain-versus-spread, and non-comparable cases.
    /// </summary>
    [TestMethod]
    public void Test_StochasticDominance_AnalyticVerdicts()
    {
        // Arrange — the aleatory pairs.
        var smaller = new Curve
        {
            LECConsequences = new[] { 10d, 1d, 0d },
            LECProbabilities = new[] { 0.001d, 0.1d, 0.1d },
            TotalProbability = 0.1d,
        };
        var doubled = new Curve
        {
            LECConsequences = new[] { 20d, 2d, 0d },
            LECProbabilities = new[] { 0.001d, 0.1d, 0.1d },
            TotalProbability = 0.1d,
        };
        var concentrated = new Curve
        {
            LECConsequences = new[] { 50.0000005d, 50d, 0d },
            LECProbabilities = new[] { 1e-12, 0.2d, 0.2d },
            TotalProbability = 0.2d,
        };
        var worseSpread = new Curve
        {
            LECConsequences = new[] { 92.0000009d, 92d, 10d, 0d },
            LECProbabilities = new[] { 1e-12, 0.1d, 0.2d, 0.2d },
            TotalProbability = 0.2d,
        };
        var crossingSpread = new Curve
        {
            LECConsequences = new[] { 60.0000006d, 60d, 2d, 0d },
            LECProbabilities = new[] { 1e-12, 0.1d, 0.2d, 0.2d },
            TotalProbability = 0.2d,
        };

        // Act / Assert — first order both ways, and the identity on a twin.
        Assert.AreEqual(DominanceVerdict.FirstDominatesFirstOrder,
            StochasticDominanceEngine.CompareLossExceedanceCurves(smaller, doubled));
        Assert.AreEqual(DominanceVerdict.SecondDominatesFirstOrder,
            StochasticDominanceEngine.CompareLossExceedanceCurves(doubled, smaller));
        Assert.AreEqual(DominanceVerdict.Identical,
            StochasticDominanceEngine.CompareLossExceedanceCurves(smaller, new Curve
            {
                LECConsequences = new[] { 10d, 1d, 0d },
                LECProbabilities = new[] { 0.001d, 0.1d, 0.1d },
                TotalProbability = 0.1d,
            }));

        // Second order only: the concentrated 0.2·50 loss against 0.1·92 + 0.1·10 (mean 10.2
        // against 10) — exceedance crosses between the 10 and 50 knots, every stop-loss favors
        // concentration.
        Assert.AreEqual(DominanceVerdict.FirstDominatesSecondOrder,
            StochasticDominanceEngine.CompareLossExceedanceCurves(concentrated, worseSpread));

        // Non-comparable: the 0.1·60 + 0.1·2 spread carries the clearly better stored mean
        // (about 7.4 against 10 — the log-log segment inflates a down-sweep's mean, so the
        // fixture leaves ample margin) but the worse tail above 50, so both the exceedance
        // and stop-loss comparisons cross.
        Assert.AreEqual(DominanceVerdict.None,
            StochasticDominanceEngine.CompareLossExceedanceCurves(concentrated, crossingSpread));

        // The independent cross-check of the quantile integral the stop-loss reads: the
        // concentrated curve's [1e-12, 0.2] integral re-derived from the closed-form power
        // segment ∫c(p)dp = (c₂p₂ − c₁p₁)/(s + 1) plus the tiny leading clamp slab.
        double x1 = Math.Log10(50.0000005d);
        double x2 = Math.Log10(50d);
        double y1 = Math.Log10(1e-12);
        double y2 = Math.Log10(0.2d);
        double slope = (x2 - x1) / (y2 - y1);
        double independentSegment = (50d * 0.2d - 50.0000005d * 1e-12) / (slope + 1d);
        double independentSlab = 50.0000005d * (1e-12 - 1e-16);
        Assert.AreEqual(independentSlab + independentSegment,
            concentrated.QuantileTailIntegral(0d, 0.2d),
            (independentSlab + independentSegment) * 1e-12,
            "The quantile tail integral must match the independent closed-form segment sum.");

        // The epistemic weighted-sample variant: pointwise first order, the certain loss
        // against a spread of worse mean (second order only), and a non-comparable pair.
        Assert.AreEqual(DominanceVerdict.FirstDominatesFirstOrder,
            StochasticDominanceEngine.CompareWeightedSamples(
                new[] { 1d, 3d }, new[] { 1d, 1d }, 2,
                new[] { 2d, 4d }, new[] { 1d, 1d }, 2, ObjectiveDirection.Minimize));
        Assert.AreEqual(DominanceVerdict.FirstDominatesSecondOrder,
            StochasticDominanceEngine.CompareWeightedSamples(
                new[] { 5d, 5d }, new[] { 1d, 1d }, 2,
                new[] { 2d, 9d }, new[] { 1d, 1d }, 2, ObjectiveDirection.Minimize));
        Assert.AreEqual(DominanceVerdict.None,
            StochasticDominanceEngine.CompareWeightedSamples(
                new[] { 0d, 10d }, new[] { 1d, 1d }, 2,
                new[] { 4d, 7d }, new[] { 1d, 1d }, 2, ObjectiveDirection.Minimize));
    }

    /// <summary>
    /// Expected utility and the partition pin. The exponential certainty equivalent over a
    /// two-point loss reproduces (1/θ)·ln(0.8 + 0.2·e^{100θ}) at 1e-12 relative, the power
    /// twin reproduces its closed form, and an expected-value tie is broken toward the
    /// concentrated loss exactly as risk aversion demands. On a live repair study declaring
    /// both seats, the partitioned low-probability region's conditional mean must equal the
    /// conditional value-at-risk of the retained year-zero curve re-measured at the region
    /// boundary bit-exactly — the two read one quantile-integral authority — and the
    /// certainty-equivalent ranking publishes finite values.
    /// </summary>
    [TestMethod]
    public void Test_ExpectedUtilityAndPartition_ClosedFormsAndCVaRPin()
    {
        // Arrange — the two-point loss: mass 0.2 at 100, the rest no loss.
        Curve twoPoint = AtomCurve((100d, 0.2d));
        double theta = 0.03d;

        // Act / Assert — the exponential and power closed forms.
        double expectedCara = Math.Log(0.8d + 0.2d * Math.Exp(100d * theta)) / theta;
        Assert.AreEqual(expectedCara,
            LecPartitionEngine.CertaintyEquivalent(twoPoint, UtilityFunctionForm.ExponentialCara, theta),
            Math.Abs(expectedCara) * 1e-12);
        double gamma = 1.5d;
        double expectedCrra = Math.Pow(0.2d * Math.Pow(100d, 1d + gamma), 1d / (1d + gamma));
        Assert.AreEqual(expectedCrra,
            LecPartitionEngine.CertaintyEquivalent(twoPoint, UtilityFunctionForm.PowerCrra, gamma),
            Math.Abs(expectedCrra) * 1e-12);

        // The expected-value tie broken by expected utility: 0.2·100 against 0.1·190 + 0.1·10
        // share the mean 20, and risk aversion must prefer the concentrated loss exactly.
        Curve spread = AtomCurve((190d, 0.1d), (10d, 0.1d));
        double concentratedMean = twoPoint.QuantileTailIntegral(0d, 0.2d);
        double spreadMean = spread.QuantileTailIntegral(0d, 0.2d);
        Assert.AreEqual(concentratedMean, spreadMean, 20d * 1e-12, "The means tie by construction.");
        double concentratedEquivalent = LecPartitionEngine.CertaintyEquivalent(twoPoint,
            UtilityFunctionForm.ExponentialCara, theta);
        double spreadEquivalent = LecPartitionEngine.CertaintyEquivalent(spread,
            UtilityFunctionForm.ExponentialCara, theta);
        double independentSpread = Math.Log(0.8d + 0.1d * Math.Exp(190d * theta)
            + 0.1d * Math.Exp(10d * theta)) / theta;
        Assert.AreEqual(independentSpread, spreadEquivalent, Math.Abs(independentSpread) * 1e-12);
        Assert.IsTrue(concentratedEquivalent < spreadEquivalent,
            "Risk aversion must break the expected-value tie toward the concentrated loss.");

        // The live study: partition and utility declared; the low-probability region's mean is
        // the retained curve's conditional value-at-risk at the region boundary, bit for bit.
        (CostBenefitAnalysis study, _, _, _) = RunRepairStudy(new CostBenefitOptions(30, 0.05d,
            alphaLevels: new[] { 0.01d }, monetization: new ConsequenceMonetization(),
            utility: new UtilityDeclaration(UtilityFunctionForm.ExponentialCara, 0.001d),
            pmrmPartition: new PmrmPartition(new[] { 0.1d, 0.01d })), 0,
            new CostStream(new[] { new CapitalCostEntry(0, 1000d) }));
        CostBenefitResults results = study.Results!;
        StrategyRanking? tailRegion = null;
        for (int i = 0; i < results.StrategyRankings.Count; i++)
        {
            StrategyRanking ranking = results.StrategyRankings[i];
            if (ranking.Strategy == DecisionStrategy.PartitionedConditionalMean
                && ranking.CriterionLabel.Contains("(0, 0.01]", StringComparison.Ordinal))
            {
                tailRegion = ranking;
            }
        }
        Assert.IsNotNull(tailRegion, "The low-probability high-consequence region must publish.");
        for (int i = 0; i < results.Trajectories.Count; i++)
        {
            Curve clone = results.Trajectories[i].Epochs[0].Realization!.Curves.Total.Clone();
            clone.ComputeRiskMeasures(double.NaN, 0.01d);
            Assert.AreEqual(clone.ConditionalValueAtRisk, tailRegion.CriterionValues[i], 0d,
                "The (0, b] region mean is the re-measured CVaR at b — one integral authority.");
        }
        StrategyRanking? certainty = FindRanking(results, DecisionStrategy.CertaintyEquivalent);
        Assert.IsNotNull(certainty);
        for (int i = 0; i < certainty.CriterionValues.Count; i++)
        {
            Assert.IsFalse(double.IsNaN(certainty.CriterionValues[i]),
                "The live certainty equivalents must be finite.");
        }
    }

    /// <summary>
    /// Chance-constraint parity: the study's raw threshold-exceedance fraction is bit-equal to
    /// the tolerable-risk confidence evaluated on the same stored weighted ensemble — the two
    /// surfaces share the evaluation arithmetic, and publishing the raw fraction leaves no
    /// complementation rounding between them. Both alternatives run full uncertainty with a
    /// configured criterion, post-hoc weights re-weight the stored ensembles, and the study's
    /// declared constraint mirrors the criterion's measure, stream, type, and threshold.
    /// </summary>
    [TestMethod]
    public void Test_ChanceConstraintParity_BitEqualToPublishedConfidence()
    {
        // Arrange — two uncertain systems with a configured tolerable-risk criterion.
        RiskAnalysis baseline = BuildUncertainSystem("Existing", 0.30d);
        RiskAnalysis repaired = BuildUncertainSystem("Repaired", 0.15d);
        double threshold = 300d;
        var systems = new[] { baseline, repaired };
        foreach (RiskAnalysis system in systems)
        {
            system.Options.TolerableRiskCriteria.Add(
                new TolerableRiskCriterion(RiskMeasure.Mean, RiskType.Total, 0, threshold));
            RunFullUncertainty(system);
            var weights = new double[system.RiskResults!.Count];
            for (int i = 0; i < weights.Length; i++)
            {
                weights[i] = 1d + i % 3;
            }
            system.RiskResults.SetRealizationWeights(weights);
        }

        // Act — the study with the mirrored constraint.
        var constraint = new CostBenefitConstraint(
            CostBenefitMetric.ForRiskMeasure(RiskMeasure.Mean, RiskType.Total),
            Numerics.Mathematics.Optimization.ConstraintType.LesserThanOrEqualTo, threshold);
        var study = new CostBenefitAnalysis(new CostBenefitOptions(30, 0.05d,
            monetization: new ConsequenceMonetization(), constraints: new[] { constraint }));
        study.Alternatives.Add(new RiskReductionAlternative("Existing condition", baseline));
        study.Alternatives.Add(new RiskReductionAlternative("Gate repair", repaired,
            new CostStream(new[] { new CapitalCostEntry(0, 1000d) })));
        study.Baseline = study.Alternatives[0];
        study.RunAsync().GetAwaiter().GetResult();
        CostBenefitResults results = study.Results!;

        // Assert — one chance entry; its raw fractions are bit-equal to the post-hoc weighted
        // confidence evaluation on each stored ensemble, and the ≤ satisfaction is its exact
        // complement with verdicts at the declared confidence.
        Assert.AreEqual(1, results.ChanceConstraints.Count);
        ChanceConstraintEntry entry = results.ChanceConstraints[0];
        for (int i = 0; i < systems.Length; i++)
        {
            IReadOnlyList<TolerableRiskConfidence> confidence = systems[i].ComputeTolerableRiskConfidence()!;
            Assert.AreEqual(confidence[0].ExceedanceProbability, entry.ExceedanceFractions[i], 0d,
                "The raw exceedance fraction is the parity surface — bit-equal by construction.");
            Assert.AreEqual(1d - entry.ExceedanceFractions[i], entry.SatisfactionProbabilities[i], 0d);
            Assert.IsTrue(entry.ExceedanceFractions[i] > 0d && entry.ExceedanceFractions[i] < 1d,
                "The designed threshold sits inside both ensembles' spread.");
            Assert.AreEqual(entry.SatisfactionProbabilities[i] >= 0.9d, entry.Verdicts[0][i]);
        }
        StrategyRanking? chance = FindRanking(results, DecisionStrategy.ChanceConstrainedSelection);
        Assert.IsNotNull(chance, "The chance-constrained selection publishes per confidence level.");
    }

    /// <summary>
    /// The designed decision-summary disagreement: four alternatives built so the expected
    /// value, the conditional value-at-risk, and the total expected annual cost provably
    /// recommend three different alternatives, plus a risk-raising alternative the do-no-harm
    /// screen excludes from every recommendation. The expected values are the closed forms
    /// C·p + 100·(1 − p) (tail fix 104.5 beats mean fix 109 beats baseline 136), the tail
    /// measure orders by failure consequence at the 0.01 exceedance level (400 beats ~550
    /// beats ~1000), and the flat operating costs make the total expected annual costs
    /// 136 / 149.5 / 149 — the baseline wins. The cross-tabulation and margins are pinned
    /// row-by-row.
    /// </summary>
    [TestMethod]
    public void Test_DecisionSummary_DesignedDisagreement()
    {
        // Arrange — the four designed alternatives.
        var study = new CostBenefitAnalysis(new CostBenefitOptions(30, 0.05d,
            alphaLevels: new[] { 0.01d }, monetization: new ConsequenceMonetization()));
        var baseline = new RiskReductionAlternative("Existing condition",
            BuildScaledFlatSystem(0.04d, 1000d));
        var tailFix = new RiskReductionAlternative("Tail fix",
            BuildScaledFlatSystem(0.005d, 1000d),
            new CostStream(null, new[] { new RecurringCostSegment(0, 45d) }));
        var meanFix = new RiskReductionAlternative("Mean fix",
            BuildScaledFlatSystem(0.03d, 400d),
            new CostStream(null, new[] { new RecurringCostSegment(0, 40d) }));
        var harmful = new RiskReductionAlternative("Deferred maintenance",
            BuildScaledFlatSystem(0.2d, 1000d));
        study.Alternatives.Add(baseline);
        study.Alternatives.Add(tailFix);
        study.Alternatives.Add(meanFix);
        study.Alternatives.Add(harmful);
        study.Baseline = baseline;

        // Act
        study.RunAsync().GetAwaiter().GetResult();
        CostBenefitResults results = study.Results!;

        // Assert — the three rules disagree by design, and the harming row is excluded.
        StrategyRanking expectedValue = FindRanking(results, DecisionStrategy.ExpectedValue)!;
        StrategyRanking tail = FindRanking(results, DecisionStrategy.ConditionalValueAtRisk)!;
        StrategyRanking teac = FindRanking(results, DecisionStrategy.TotalExpectedAnnualCost)!;
        Assert.AreEqual("Tail fix", expectedValue.RecommendedAlternative,
            "0.005·1000 + 0.995·100 = 104.5 is the smallest mean.");
        Assert.AreEqual("Mean fix", tail.RecommendedAlternative,
            "The 400-dollar failure consequence bounds the 0.01-tail conditional mean.");
        Assert.AreEqual("Existing condition", teac.RecommendedAlternative,
            "136 beats 149 and 149.5 once the flat operating costs are added.");
        Assert.IsTrue(results.Alternatives[3].FailsDoNoHarm);
        Assert.IsTrue(results.Summary!.FailsDoNoHarm[3]);
        Assert.AreEqual(0, results.Summary.RecommendationCounts[3],
            "The excluded alternative collects no recommendations.");
        for (int i = 0; i < results.StrategyRankings.Count; i++)
        {
            Assert.IsTrue(results.StrategyRankings[i].IsExcludedFromRecommendation[3]);
        }

        // The cross-tabulation reconciles row-by-row: one entry per ranking, the entry echoes
        // its ranking's recommendation, and the margins count the non-withheld rows.
        Assert.AreEqual(results.StrategyRankings.Count, results.Summary.Entries.Count);
        var recomputed = new int[4];
        for (int i = 0; i < results.Summary.Entries.Count; i++)
        {
            DecisionSummaryEntry entry = results.Summary.Entries[i];
            StrategyRanking ranking = results.StrategyRankings[i];
            Assert.AreEqual(ranking.Strategy, entry.Strategy);
            Assert.AreEqual(ranking.RecommendedAlternative, entry.RecommendedAlternative);
            Assert.AreEqual(ranking.RecommendationWithheld, entry.RecommendationWithheld);
            if (!entry.RecommendationWithheld)
            {
                Assert.AreEqual(ranking.CriterionValues[ranking.RecommendedIndex],
                    entry.RecommendedValue, 0d);
                recomputed[ranking.RecommendedIndex]++;
            }
        }
        for (int i = 0; i < 4; i++)
        {
            Assert.AreEqual(recomputed[i], results.Summary.RecommendationCounts[i]);
        }
    }

    /// <summary>
    /// Author inertness and run-to-run reproducibility for the strategy layer over stored
    /// full-uncertainty ensembles. Both alternatives carry published ensembles, so Tier 2 runs
    /// live: the epistemic bands, the classical rankings, the quantile regret, the epistemic
    /// dominance screen, and the chance-constrained selections all publish. The authors'
    /// published ensemble payloads and component hashes are byte-identical after the study, a
    /// second study over the same alternatives publishes bit-identical strategy blocks, and
    /// the deferred shared-state block is pinned absent with its named diagnostic — the
    /// current contract.
    /// </summary>
    [TestMethod]
    public void Test_Study_AuthorInertnessAndReproducibility()
    {
        // Arrange — two uncertain systems with published full-uncertainty ensembles.
        RiskAnalysis baseline = BuildUncertainSystem("Existing", 0.30d);
        RiskAnalysis repaired = BuildUncertainSystem("Repaired", 0.15d);
        RunFullUncertainty(baseline);
        RunFullUncertainty(repaired);
        string baselineBefore = baseline.RiskResults!.ToJson();
        string repairedBefore = repaired.RiskResults!.ToJson();
        string baselineHash = Convert.ToHexString(baseline.Components[0].CanonicalHash());
        string repairedHash = Convert.ToHexString(repaired.Components[0].CanonicalHash());

        CostBenefitResults Run()
        {
            var study = new CostBenefitAnalysis(new CostBenefitOptions(30, 0.05d,
                monetization: new ConsequenceMonetization()));
            study.Alternatives.Add(new RiskReductionAlternative("Existing condition", baseline));
            study.Alternatives.Add(new RiskReductionAlternative("Gate repair", repaired,
                new CostStream(new[] { new CapitalCostEntry(0, 1000d) })));
            study.Baseline = study.Alternatives[0];
            study.RunAsync().GetAwaiter().GetResult();
            return study.Results!;
        }

        // Act — two independent studies over the same authored alternatives.
        CostBenefitResults first = Run();
        CostBenefitResults second = Run();

        // Assert — Tier 2 ran live: two criteria (the benefit-stream mean and the default
        // objective vector's standard-deviation axis) over two alternatives.
        Assert.AreEqual(4, first.EpistemicMeasures.Count);
        Assert.IsNotNull(FindRanking(first, DecisionStrategy.Laplace));
        Assert.IsNotNull(FindRanking(first, DecisionStrategy.WaldMaximin));
        Assert.IsNotNull(FindRanking(first, DecisionStrategy.QuantileRegret));
        Assert.IsNotNull(FindRanking(first, DecisionStrategy.ChanceConstrainedSelection));
        bool epistemicDominance = false;
        for (int i = 0; i < first.Dominance.Count; i++)
        {
            if (first.Dominance[i].Layer == "Epistemic") epistemicDominance = true;
        }
        Assert.IsTrue(epistemicDominance, "The epistemic dominance screen publishes.");

        // The deferred shared-state block: absent, with its named diagnostic — the pinned
        // current contract.
        Assert.AreEqual(0, first.RegretMatrices.Count);
        bool tierThreeNamed = false;
        for (int i = 0; i < first.Diagnostics.Count; i++)
        {
            if (first.Diagnostics[i].Code == "TRC2008") tierThreeNamed = true;
        }
        Assert.IsTrue(tierThreeNamed);

        // Author inertness: the published payloads and hashes are byte-identical.
        Assert.AreEqual(baselineBefore, baseline.RiskResults!.ToJson());
        Assert.AreEqual(repairedBefore, repaired.RiskResults!.ToJson());
        Assert.AreEqual(baselineHash, Convert.ToHexString(baseline.Components[0].CanonicalHash()));
        Assert.AreEqual(repairedHash, Convert.ToHexString(repaired.Components[0].CanonicalHash()));

        // Run-to-run reproducibility: every strategy block is bit-identical (bit-level double
        // comparison so NaN slots — the zero-cost baseline's benefit-cost ratio — compare as
        // themselves).
        static void AssertBits(double expectedValue, double actualValue)
        {
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(expectedValue),
                BitConverter.DoubleToInt64Bits(actualValue));
        }
        Assert.AreEqual(first.StrategyRankings.Count, second.StrategyRankings.Count);
        for (int i = 0; i < first.StrategyRankings.Count; i++)
        {
            StrategyRanking a = first.StrategyRankings[i];
            StrategyRanking b = second.StrategyRankings[i];
            Assert.AreEqual(a.Strategy, b.Strategy);
            Assert.AreEqual(a.CriterionLabel, b.CriterionLabel);
            Assert.AreEqual(a.RecommendedIndex, b.RecommendedIndex);
            for (int j = 0; j < a.CriterionValues.Count; j++)
            {
                AssertBits(a.CriterionValues[j], b.CriterionValues[j]);
                Assert.AreEqual(a.Ranks[j], b.Ranks[j]);
            }
        }
        Assert.AreEqual(first.EpistemicMeasures.Count, second.EpistemicMeasures.Count);
        for (int i = 0; i < first.EpistemicMeasures.Count; i++)
        {
            AssertBits(first.EpistemicMeasures[i].WeightedMean,
                second.EpistemicMeasures[i].WeightedMean);
            AssertBits(first.EpistemicMeasures[i].ConditionalValueAtRisk,
                second.EpistemicMeasures[i].ConditionalValueAtRisk);
            AssertBits(first.EpistemicMeasures[i].Variance,
                second.EpistemicMeasures[i].Variance);
        }
        Assert.AreEqual(first.Dominance.Count, second.Dominance.Count);
        for (int i = 0; i < first.Dominance.Count; i++)
        {
            Assert.AreEqual(first.Dominance[i].Verdict, second.Dominance[i].Verdict);
        }
        for (int i = 0; i < first.Summary!.RecommendationCounts.Count; i++)
        {
            Assert.AreEqual(first.Summary.RecommendationCounts[i],
                second.Summary!.RecommendationCounts[i]);
        }
        AssertBits(first.Alternatives[1].NetPresentValue, second.Alternatives[1].NetPresentValue);
    }

    /// <summary>
    /// The strategy validation and diagnostic sweep, asserted verbatim-prefix: the
    /// epistemic-alternative refusal, the mixed-mode refusal, the two tier-precondition
    /// advisories, the reliability-mode skips naming each consequence-dependent strategy and
    /// declared criterion, the missing-ensemble and missing-map block diagnostics, and the
    /// explicit no-objective selection skips.
    /// </summary>
    [TestMethod]
    public void Test_Validation_StrategyGatesAndDiagnostics()
    {
        // The epistemic-alternative refusal, verbatim-prefix.
        var epistemicStudy = new CostBenefitAnalysis(new CostBenefitOptions(30, 0.05d));
        var ordinaryBaseline = new RiskReductionAlternative("Existing condition",
            BuildScaledFlatSystem(0.04d, 1000d));
        epistemicStudy.Alternatives.Add(ordinaryBaseline);
        epistemicStudy.Alternatives.Add(new RiskReductionAlternative("Epistemic repair",
            BuildEnumeratedSystem("Repaired", new[] { 0.05d, 0.1d, 0.2d })));
        epistemicStudy.Baseline = ordinaryBaseline;
        (bool epistemicValid, List<string> epistemicMessages) = epistemicStudy.Validate();
        Assert.IsFalse(epistemicValid);
        AssertPrefix(epistemicMessages,
            "Error: Alternative 'Epistemic repair' carries an epistemic-mixture composite");

        // The mixed-mode refusal and the tier-precondition advisories, verbatim-prefix.
        var mixedStudy = new CostBenefitAnalysis(new CostBenefitOptions(30, 0.05d));
        RiskAnalysis reliabilitySystem = BuildScaledFlatSystem(0.03d, 1000d);
        reliabilitySystem.Options.Mode = RiskAnalysisMode.Reliability;
        var mixedBaseline = new RiskReductionAlternative("Existing condition",
            BuildScaledFlatSystem(0.04d, 1000d));
        mixedStudy.Alternatives.Add(mixedBaseline);
        mixedStudy.Alternatives.Add(new RiskReductionAlternative("Reliability repair", reliabilitySystem));
        mixedStudy.Baseline = mixedBaseline;
        (_, List<string> mixedMessages) = mixedStudy.Validate();
        AssertPrefix(mixedMessages,
            "Error: Alternatives mix risk-analysis modes");
        AssertPrefix(mixedMessages,
            "Warning: Alternative 'Existing condition' carries no stored full-uncertainty ensemble");
        AssertPrefix(mixedMessages,
            "Warning: Alternative 'Existing condition' was not enumerated by the exact logic tree");

        // The reliability-mode run: every consequence-dependent strategy and declared
        // criterion is named once, and only the probability-side rankings survive.
        RiskAnalysis reliabilityBaseline = BuildScaledFlatSystem(0.04d, 1000d);
        RiskAnalysis reliabilityRepair = BuildScaledFlatSystem(0.005d, 1000d);
        reliabilityBaseline.Options.Mode = RiskAnalysisMode.Reliability;
        reliabilityRepair.Options.Mode = RiskAnalysisMode.Reliability;
        var reliabilityStudy = new CostBenefitAnalysis(new CostBenefitOptions(30, 0.05d,
            objectives: new[]
            {
                new ObjectiveDeclaration("Mean risk",
                    CostBenefitMetric.ForRiskMeasure(RiskMeasure.Mean, RiskType.Total),
                    ObjectiveDirection.Minimize),
            }));
        var reliabilityRow = new RiskReductionAlternative("Existing condition", reliabilityBaseline);
        reliabilityStudy.Alternatives.Add(reliabilityRow);
        reliabilityStudy.Alternatives.Add(new RiskReductionAlternative("Gate repair", reliabilityRepair,
            new CostStream(new[] { new CapitalCostEntry(0, 1000d) })));
        reliabilityStudy.Baseline = reliabilityRow;
        reliabilityStudy.RunAsync().GetAwaiter().GetResult();
        CostBenefitResults reliabilityResults = reliabilityStudy.Results!;
        AssertDiagnostic(reliabilityResults.Diagnostics, "TRC2005",
            "The strategy 'ExpectedValue' is consequence-dependent");
        AssertDiagnostic(reliabilityResults.Diagnostics, "TRC2005",
            "The strategy 'TotalExpectedAnnualCost' is consequence-dependent");
        AssertDiagnostic(reliabilityResults.Diagnostics, "TRC2005",
            "The aleatory stochastic-dominance screen is consequence-dependent");
        AssertDiagnostic(reliabilityResults.Diagnostics, "TRC2005",
            "skipped as an epistemic decision criterion under reliability mode");
        Assert.IsNotNull(FindRanking(reliabilityResults, DecisionStrategy.AnnualizedFailureProbability));
        Assert.IsNull(FindRanking(reliabilityResults, DecisionStrategy.ExpectedValue));

        // The ordinary mean-only run: the missing-ensemble and missing-map block diagnostics.
        var gatingStudy = new CostBenefitAnalysis(new CostBenefitOptions(30, 0.05d,
            monetization: new ConsequenceMonetization()));
        var gatingBaseline = new RiskReductionAlternative("Existing condition",
            BuildScaledFlatSystem(0.04d, 1000d));
        gatingStudy.Alternatives.Add(gatingBaseline);
        gatingStudy.Alternatives.Add(new RiskReductionAlternative("Gate repair",
            BuildScaledFlatSystem(0.005d, 1000d)));
        gatingStudy.Baseline = gatingBaseline;
        gatingStudy.RunAsync().GetAwaiter().GetResult();
        AssertDiagnostic(gatingStudy.Results!.Diagnostics, "TRC2006",
            "carries no stored full-uncertainty ensemble");
        AssertDiagnostic(gatingStudy.Results!.Diagnostics, "TRC2008",
            "the baseline carries no logic-tree enumeration map");

        // The explicit empty objective vector: the constrained selection skips with its named
        // notice (the chance selection sits behind the Tier-2 ensemble gate here).
        var noObjectiveStudy = new CostBenefitAnalysis(new CostBenefitOptions(30, 0.05d,
            objectives: Array.Empty<ObjectiveDeclaration>()));
        var noObjectiveBaseline = new RiskReductionAlternative("Existing condition",
            BuildScaledFlatSystem(0.04d, 1000d));
        noObjectiveStudy.Alternatives.Add(noObjectiveBaseline);
        noObjectiveStudy.Alternatives.Add(new RiskReductionAlternative("Gate repair",
            BuildScaledFlatSystem(0.005d, 1000d)));
        noObjectiveStudy.Baseline = noObjectiveBaseline;
        noObjectiveStudy.RunAsync().GetAwaiter().GetResult();
        AssertDiagnostic(noObjectiveStudy.Results!.Diagnostics, "TRC2007",
            "The constrained-selection strategy is skipped: no objective vector is declared");
        Assert.IsNull(FindRanking(noObjectiveStudy.Results!, DecisionStrategy.ConstrainedSelection));
    }

    /// <summary>Asserts one message starts with the prefix.</summary>
    /// <param name="messages">The validation messages.</param>
    /// <param name="prefix">The required prefix.</param>
    private static void AssertPrefix(List<string> messages, string prefix)
    {
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i].StartsWith(prefix, StringComparison.Ordinal)) return;
        }
        Assert.Fail($"No validation message starts with '{prefix}'.");
    }

    /// <summary>Asserts one diagnostic with the code carries the fragment.</summary>
    /// <param name="diagnostics">The computation diagnostics.</param>
    /// <param name="code">The required code.</param>
    /// <param name="fragment">The required message fragment.</param>
    private static void AssertDiagnostic(IReadOnlyList<ComputationDiagnostic> diagnostics,
        string code, string fragment)
    {
        for (int i = 0; i < diagnostics.Count; i++)
        {
            if (diagnostics[i].Code == code
                && diagnostics[i].Message.Contains(fragment, StringComparison.Ordinal))
            {
                return;
            }
        }
        Assert.Fail($"No {code} diagnostic carries '{fragment}'.");
    }
}
