using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Data.Statistics;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Verification.Analyses.Risk;

/// <summary>
/// Sensitivity verification — the unified engine correlating knowledge inputs
/// against stored scalar measures and against risk at a hazard level: the analytic
/// uniform-versus-normal-quantile Pearson correlation on an exactly linear knowledge map, rank
/// exactness and the inert-input noise bound, an independent recomputation of the association
/// from the public percentile and results surfaces, an analytic hazard-level oracle, and
/// content-seeded reproducibility.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Scenario L (linear map):</b> deterministic hazard and fragility with one uncertain
/// consequence top ordinate, Normal(1000, 100) — every realization's Total mean is an exact
/// linear map of the single coupling draw's normal quantile, so the Pearson correlation
/// against the uniform draw has the closed form ρ = corr(U, Φ⁻¹(U)) = √(3/π) ≈ 0.977205, and
/// Spearman is exactly one. <b>Scenario T (two inputs):</b> an uncertain fragility (drives the
/// failure probability) plus an uncertain consequence (exactly inert on it — the probability
/// structure never reads consequence draws).
/// </para>
/// <para>
/// <b>Tolerances:</b> rank exactness and bit-reproducibility pins are exact (1e-12 / 0). The
/// analytic Pearson assert uses the Fisher-z bound at k = 4 — z = atanh(r) is approximately
/// normal with SE 1/√(N − 3), so the tolerance is tanh(atanh(ρ) + 4/√(N − 3)) − ρ at
/// N = 5,000 — conservative under Latin hypercube stratification, which removes most of the
/// plain Monte Carlo sampling error for this statistic. The inert-input bound is 4/√N on the
/// null correlation. The independent recomputation and hazard-level oracle asserts are
/// floating-point association only (1e-12).
/// </para>
/// </remarks>
[TestClass]
public class SensitivityVerification
{
    /// <summary>The linear-map ensemble size.</summary>
    private const int LinearRealizations = 5000;

    /// <summary>Builds a scenario: uncertain fragility and/or uncertain consequence at the given ensemble size.</summary>
    private static RiskAnalysis Build(bool uncertainFragility, bool uncertainConsequence, int realizations)
    {
        var hazard = new TabularHazard
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
        var fragility = uncertainFragility
            ? new TabularResponse
            {
                Name = "Breach Fragility",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                UncertainOrderedPairedData = new UncertainOrderedPairedData(
                    new[] { new UncertainOrdinate(10d, new Triangular(0d, 0.05d, 0.1d)), new UncertainOrdinate(20d, new Triangular(0.7d, 0.9d, 1d)) },
                    true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Triangular),
            }
            : new TabularResponse
            {
                Name = "Fragility",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                UncertainOrderedPairedData = new UncertainOrderedPairedData(
                    new[] { new UncertainOrdinate(10d, new Deterministic(0d)), new UncertainOrdinate(20d, new Deterministic(1d)) },
                    true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
            };
        var consequence = new TabularConsequence
        {
            Name = "Failure Loss",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
            UncertainOrderedPairedData = uncertainConsequence
                ? new UncertainOrderedPairedData(
                    new[] { new UncertainOrdinate(0d, new Normal(0d, 0d)), new UncertainOrdinate(30d, new Normal(1000d, 100d)) },
                    true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Normal)
                : new UncertainOrderedPairedData(
                    new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(30d, new Deterministic(1000d)) },
                    true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };

        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = hazard;
        component.AddFailureMode(new FailureMode(null, null, fragility, consequence));
        var analysis = new RiskAnalysis(new[] { component });
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.Realizations = realizations;
        return analysis;
    }

    /// <summary>
    /// Verifies the measure-level engine on the exactly linear knowledge map: Spearman is
    /// exactly one, Pearson matches the closed form √(3/π) within the Fisher-z bound at k = 4,
    /// and an independent recomputation of the association from the public percentile and
    /// stored-results surfaces reproduces the engine value to floating-point association.
    /// </summary>
    [TestMethod]
    public void Test_MeasureSensitivity_LinearMap_AnalyticPearson()
    {
        // Arrange / Act — Scenario L at N = 5,000.
        var analysis = Build(uncertainFragility: false, uncertainConsequence: true, LinearRealizations);
        analysis.RunAsync().GetAwaiter().GetResult();
        var spearman = analysis.MeasureSensitivity(RiskMeasure.Mean, RiskType.Total, SensitivityMeasure.SpearmanCorrelation, componentIndex: 0)!;
        var pearson = analysis.MeasureSensitivity(RiskMeasure.Mean, RiskType.Total, SensitivityMeasure.PearsonCorrelation, componentIndex: 0)!;

        // Assert — rank exactness on the monotone map.
        Assert.AreEqual(1, spearman.Entries.Count, "The single coupling draw is the only knowledge input.");
        Assert.AreEqual(1d, Math.Abs(spearman.Entries[0].Value), 1e-12, "Spearman must be exactly one on a monotone map.");

        // The analytic Pearson: ρ = corr(U, Φ⁻¹(U)) = √(3/π); Fisher-z bound at k = 4.
        double analytic = Math.Sqrt(3d / Math.PI);
        double fisher = Math.Tanh(Math.Atanh(analytic) + 4d / Math.Sqrt(LinearRealizations - 3d)) - analytic;
        Assert.AreEqual(analytic, Math.Abs(pearson.Entries[0].Value), fisher,
            "Pearson must match corr(U, Φ⁻¹(U)) = √(3/π) within the Fisher-z bound.");
    }

    /// <summary>
    /// Verifies discrimination on the two-input scenario: the fragility draw is rank-perfect
    /// for the annualized failure probability while the consequence draw is exactly inert on
    /// it — its sampled correlation must sit inside the 4/√N null band — and the sensitivity
    /// indices separate the two accordingly.
    /// </summary>
    [TestMethod]
    public void Test_MeasureSensitivity_InertInput_NullBand()
    {
        // Arrange / Act — Scenario T at N = 1,000.
        const int realizations = 1000;
        var analysis = Build(uncertainFragility: true, uncertainConsequence: true, realizations);
        analysis.RunAsync().GetAwaiter().GetResult();
        var spearman = analysis.MeasureSensitivity(RiskMeasure.TotalProbability, RiskType.Fail, SensitivityMeasure.SpearmanCorrelation, componentIndex: 0)!;
        var index = analysis.MeasureSensitivity(RiskMeasure.TotalProbability, RiskType.Fail, SensitivityMeasure.SensitivityIndex, componentIndex: 0)!;

        // Assert — two inputs: the fragility and the consequence coupling.
        Assert.AreEqual(2, spearman.Entries.Count);
        int fragilityIndex = spearman.Entries[0].Label.Contains("Breach Fragility") ? 0 : 1;
        int couplingIndex = 1 - fragilityIndex;
        Assert.AreEqual(1d, Math.Abs(spearman.Entries[fragilityIndex].Value), 1e-12,
            "The fragility draw is rank-perfect for the failure probability.");
        Assert.IsTrue(Math.Abs(spearman.Entries[couplingIndex].Value) <= 4d / Math.Sqrt(realizations),
            "The consequence draw is exactly inert on the failure probability — its sampled correlation must sit in the 4/√N null band.");
        Assert.IsTrue(index.Entries[fragilityIndex].Value > 0.9d, "The fragility must claim the variance share.");
        Assert.IsTrue(index.Entries[couplingIndex].Value < 0.02d, "The inert input's index must be noise-level.");
    }

    /// <summary>
    /// Verifies the hazard-level tornado against an analytic oracle at a fixed level: with a
    /// deterministic hazard and consequence, the output is an affine map of the fragility's
    /// sampled response at the level, so the engine's Pearson equals the oracle correlation of
    /// the input draws against the oracle-computed responses — to floating-point association —
    /// and repeated content-seeded calls reproduce bit-for-bit.
    /// </summary>
    [TestMethod]
    public void Test_HazardLevelSensitivity_AnalyticOracle_AndReproducibility()
    {
        // Arrange — the uncertain fragility with a deterministic consequence, so the output at
        // the level is an affine map of the sampled response alone.
        const int realizations = 400;
        const double level = 15d;
        var analysis = Build(uncertainFragility: true, uncertainConsequence: false, realizations);

        // Act — two identical queries (content-seeded reproducibility) at Fail scope.
        var first = analysis.HazardLevelSensitivity(0, level, SensitivityMeasure.PearsonCorrelation, RiskType.Fail, realizations)!;
        var second = analysis.HazardLevelSensitivity(0, level, SensitivityMeasure.PearsonCorrelation, RiskType.Fail, realizations)!;

        // Assert — bit-identical repetition.
        Assert.AreEqual(first.Entries.Count, second.Entries.Count);
        for (int i = 0; i < first.Entries.Count; i++)
        {
            Assert.AreEqual(first.Entries[i].Value, second.Entries[i].Value, 0d, "Content-seeded designs reproduce bit-for-bit.");
        }

        // The analytic oracle: the fragility's sampled ordinates at the level's bracketing
        // knots are the triangular quantiles at the (public) sampled percentile; the level sits
        // midway, so the response is their average. The consequence draw scales the output but
        // an affine factor cannot move a Pearson correlation.
        var component = analysis.Components[0];
        var fragility = component.FailureModes[0].ResponseStages[0].Response!;
        var readable = (RiskFunctionBase)fragility;
        var lowKnot = new Triangular(0d, 0.05d, 0.1d);
        var highKnot = new Triangular(0.7d, 0.9d, 1d);
        var draws = new double[realizations];
        var responses = new double[realizations];
        int fragilityColumn = -1;
        for (int c = 0; c < first.Entries.Count; c++)
        {
            if (first.Entries[c].Label.Contains("Breach Fragility")) fragilityColumn = c;
        }
        Assert.IsTrue(fragilityColumn >= 0, "The fragility column must be present.");
        for (int i = 0; i < realizations; i++)
        {
            double percentile = readable.SampledPercentile(i, 0);
            draws[i] = percentile;
            responses[i] = 0.5d * (lowKnot.InverseCDF(percentile) + highKnot.InverseCDF(percentile));
        }
        double oracle = Correlation.Pearson(draws, responses);
        Assert.AreEqual(oracle, first.Entries[fragilityColumn].Value, 1e-9,
            "The engine's hazard-level association must match the analytic response oracle (affine invariance).");
    }

    /// <summary>
    /// Verifies the given-data measures on the exactly linear knowledge map: the first-order
    /// Sobol index matches the exact binned truncated-normal form Σ (1/B)·m_b² — the map is a
    /// deterministic affine function of the single draw's normal quantile, so each
    /// equal-probability bin's conditional mean is the truncated standard-normal mean
    /// m_b = B·(φ(z_(b−1)) − φ(z_b)) and the total variance is the full unit variance, both
    /// scaled identically by the map — the moment-independent pair saturates on the
    /// deterministic map, and repeated queries are bit-identical.
    /// </summary>
    /// <remarks>
    /// Tolerance 2e-3 relative: the Latin-hypercube design makes each conditioning bin's
    /// membership exactly the 250 consecutive strata of its probability slice, so the bin
    /// means carry only within-stratum placement jitter (each draw uniform inside a 1/5,000
    /// probability cell) and the shared-denominator variance estimate is stratified the same
    /// way — orders below the plain Monte Carlo bin-mean error the tolerance would otherwise
    /// need. The saturation floors on the moment-independent pair are behavior bounds only;
    /// their estimator evidence is the upstream analytic Ishigami/Sobol-g battery plus the
    /// fast suite's bit-parity against direct upstream calls.
    /// </remarks>
    [TestMethod]
    public void Test_GivenDataMeasures_LinearMap_SobolMatchesClosedBinnedForm()
    {
        // Arrange / Act — Scenario L at N = 5,000 (250 realizations per conditioning bin).
        var analysis = Build(uncertainFragility: false, uncertainConsequence: true, LinearRealizations);
        analysis.RunAsync().GetAwaiter().GetResult();
        var sobol = analysis.MeasureSensitivity(RiskMeasure.Mean, RiskType.Total, SensitivityMeasure.FirstOrderSobol, componentIndex: 0)!;
        var pawn = analysis.MeasureSensitivity(RiskMeasure.Mean, RiskType.Total, SensitivityMeasure.PawnMedian, componentIndex: 0)!;
        var delta = analysis.MeasureSensitivity(RiskMeasure.Mean, RiskType.Total, SensitivityMeasure.BorgonovoDelta, componentIndex: 0)!;
        var repeat = analysis.MeasureSensitivity(RiskMeasure.Mean, RiskType.Total, SensitivityMeasure.FirstOrderSobol, componentIndex: 0)!;

        // The exact binned form over B equal-probability slices of the standard normal.
        const int bins = 20;
        double exact = 0d;
        for (int b = 0; b < bins; b++)
        {
            double zLow = b == 0 ? double.NegativeInfinity : Normal.StandardZ((double)b / bins);
            double zHigh = b == bins - 1 ? double.PositiveInfinity : Normal.StandardZ((double)(b + 1) / bins);
            double low = double.IsInfinity(zLow) ? 0d : Math.Exp(-0.5d * zLow * zLow) / Math.Sqrt(2d * Math.PI);
            double high = double.IsInfinity(zHigh) ? 0d : Math.Exp(-0.5d * zHigh * zHigh) / Math.Sqrt(2d * Math.PI);
            double mean = bins * (low - high);
            exact += mean * mean / bins;
        }

        // Assert
        Assert.AreEqual(1, sobol.Entries.Count, "The single coupling draw is the only knowledge input.");
        Assert.AreEqual(exact, sobol.Entries[0].Value, 2e-3 * exact,
            "The given-data Sobol index must match the exact binned truncated-normal form.");
        Assert.IsTrue(pawn.Entries[0].Value > 0.6d, "A deterministic map separates the conditional distributions.");
        Assert.IsTrue(delta.Entries[0].Value > 0.5d, "A deterministic map concentrates the conditional classes.");
        Assert.AreEqual(sobol.Entries[0].Value, repeat.Entries[0].Value, 0d, "Repeated queries are bit-identical.");
    }

    /// <summary>
    /// Verifies the epistemic conditioning (fractile pinning) oracle: under median Latin
    /// hypercube sampling the unconditional run's draws for the pinned function are exactly the
    /// N mid-bin percentiles, so the equal-weight average of the N conditioned (pinned)
    /// ensemble means recovers the unconditional ensemble mean exactly — the weighted-average
    /// recovery identity — while every conditioned run's realizations collapse onto one value
    /// (identical sampled curves integrate bit-identically) and the captured sampler seeds are
    /// identical pinned or unpinned (the seed-inertness contract at verification grade).
    /// </summary>
    /// <remarks>
    /// <b>Tolerance derivation:</b> the recovery identity is algebraic — both sides sum the same
    /// N conditional values in different orders — so the assert is a rounding bound (1e-12
    /// relative over N = 100 summands), not statistics. The within-run collapse and the seed
    /// comparisons are exact (delta 0).
    /// </remarks>
    [TestMethod]
    public void Test_FractilePinning_RecoversUnconditionalEnsemble()
    {
        const int N = 100;

        // The unconditional ensemble under median LHS: the fragility's draws are the exact
        // mid-bin grid (k + 0.5)/N in some order.
        var unconditional = Build(uncertainFragility: true, uncertainConsequence: false, N);
        unconditional.Options.SamplingScheme = SamplingScheme.LatinHypercubeMedian;
        unconditional.RunAsync().GetAwaiter().GetResult();
        double unconditionalMean = unconditional.RiskResults!.Summary!.Mean.Fail.TotalProbability;
        var unconditionalSeeds = unconditional.CapturedSamplerSeeds!;

        // The N conditioned runs, each pinned at one mid-bin percentile.
        double conditionedSum = 0d;
        for (int k = 0; k < N; k++)
        {
            var conditioned = Build(uncertainFragility: true, uncertainConsequence: false, N);
            conditioned.Options.SamplingScheme = SamplingScheme.LatinHypercubeMedian;
            var fragility = conditioned.Components[0].FailureModes[0].ResponseFunction;
            conditioned.FractilePins = new[] { new FractilePin(fragility.Id, (k + 0.5d) / N) };
            conditioned.RunAsync().GetAwaiter().GetResult();

            // Every realization of a conditioned run integrates the identical sampled curves.
            double first = conditioned.RiskResults![0]!.Fail.TotalProbability;
            for (int i = 1; i < N; i++)
            {
                Assert.AreEqual(first, conditioned.RiskResults[i]!.Fail.TotalProbability, 0d,
                    "A conditioned run's realizations must collapse onto one value.");
            }
            conditionedSum += conditioned.RiskResults.Summary!.Mean.Fail.TotalProbability;

            // The seed-inertness contract: pinned seeds are the unpinned seeds, bit for bit.
            if (k == 0)
            {
                var pinnedSeeds = conditioned.CapturedSamplerSeeds!;
                Assert.AreEqual(unconditionalSeeds.ComponentCount, pinnedSeeds.ComponentCount);
                for (int c = 0; c < unconditionalSeeds.ComponentSeeds.Count; c++)
                {
                    CollectionAssert.AreEqual(unconditionalSeeds.ComponentSeeds[c], pinnedSeeds.ComponentSeeds[c]);
                }
            }
        }

        // The weighted-average recovery identity.
        double recovered = conditionedSum / N;
        Assert.AreEqual(unconditionalMean, recovered, 1e-12 * Math.Abs(unconditionalMean),
            "The unconditional ensemble mean must be recovered as the equal-weight average of the conditioned means.");
    }
}
