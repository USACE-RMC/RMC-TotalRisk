using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Results.Risk;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Verification.Analyses.Risk;

/// <summary>
/// Scalar-measure confidence-interval verification — the ensemble summary that
/// gives the risk-measure catalog percentile intervals: verified on an analytically solvable
/// knowledge structure where every per-realization mean is an exact monotone (linear) map of a
/// single stratified knowledge draw, so the ensemble quantiles have closed-form targets; plus
/// the storage contract — the stored summary reproduces bit-for-bit from a JSON round-trip and
/// across repeated runs.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Scenario:</b> a deterministic stage-frequency curve and fragility with a single
/// background-free failure mode whose consequence curve is linear from zero with one uncertain
/// top ordinate, Normal(1000, 100) at stage 30. The sampled consequence curve scales linearly
/// with the single draw, and the risk integral is linear in the consequence surface, so every
/// realization's Total mean is exactly M₀ · s_i / 1000 with s_i = 1000 + 100·Φ⁻¹(p_i) — the
/// deterministic companion run supplies M₀ exactly.
/// </para>
/// <para>
/// <b>Tolerances:</b> under Latin hypercube sampling the consequence dimension's draws are
/// stratified — exactly one per probability bin of width 1/N — so a sorted order statistic
/// sits within 1/N of its plotting position and the sample quantile at level q deviates from
/// the analytic quantile by at most the map's variation over ±2/N in probability (one bin each
/// for the draw offset and the percentile interpolation). The asserts use exactly that
/// self-derived bound, computed from the analytic map in-test. The ensemble mean uses the
/// conservative plain Monte Carlo bound 4·σ/√N (LHS variance is far smaller for this linear
/// statistic — the LHS variance-reduction family measured a ×10⁴ reduction), with σ = M₀ · 0.1.
/// </para>
/// </remarks>
[TestClass]
public class ScalarUncertaintyVerification
{
    /// <summary>The ensemble size (stratified draws make the quantile bound 2/N-tight).</summary>
    private const int Realizations = 2000;

    /// <summary>Builds the scenario analysis; a deterministic consequence scale builds the M₀ companion.</summary>
    private static RiskAnalysis Build(bool uncertain)
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
        var fragility = new TabularResponse
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
            UncertainOrderedPairedData = uncertain
                ? new UncertainOrderedPairedData(
                    new[]
                    {
                        new UncertainOrdinate(0d, new Normal(0d, 0d)),
                        new UncertainOrdinate(30d, new Normal(1000d, 100d)),
                    },
                    true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Normal)
                : new UncertainOrderedPairedData(
                    new[]
                    {
                        new UncertainOrdinate(0d, new Deterministic(0d)),
                        new UncertainOrdinate(30d, new Deterministic(1000d)),
                    },
                    true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };

        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = hazard;
        component.AddFailureMode(new FailureMode(null, null, fragility, consequence));
        var analysis = new RiskAnalysis(new[] { component });
        if (uncertain)
        {
            analysis.Options.EstimateMeanRiskOnly = false;
            analysis.Options.Realizations = Realizations;
        }
        return analysis;
    }

    /// <summary>The analytic quantile map: the Total-mean quantile at probability level q.</summary>
    private static double AnalyticQuantile(double baseline, double q)
    {
        return baseline * (1000d + 100d * Normal.StandardZ(q)) / 1000d;
    }

    /// <summary>
    /// Verifies the scalar intervals against the analytic monotone-map targets: the Lower,
    /// Median, and Upper Total-mean slots at the 90% width match the closed-form quantiles
    /// within the stratified-draw bound, and the mean slot matches M₀ within 4·σ/√N.
    /// </summary>
    [TestMethod]
    public void Test_ScalarIntervals_VsAnalyticQuantiles()
    {
        // Arrange — the deterministic companion supplies M₀ exactly.
        var companion = Build(uncertain: false);
        companion.RunAsync().GetAwaiter().GetResult();
        double baseline = companion.RiskResults![0]!.Total.Mean;
        Assert.IsTrue(baseline > 0d, "The companion baseline must be positive.");

        // Act
        var analysis = Build(uncertain: true);
        analysis.RunAsync().GetAwaiter().GetResult();
        var summary = analysis.RiskResults!.Summary!;
        Assert.IsNotNull(summary);

        // Assert — each percentile slot vs the analytic quantile, at the self-derived
        // stratified bound: the map's variation over ±2/N around the level.
        foreach ((double level, double observed) in new[]
        {
            (0.05d, summary.Lower.Total.Mean),
            (0.5d, summary.Median.Total.Mean),
            (0.95d, summary.Upper.Total.Mean),
        })
        {
            double target = AnalyticQuantile(baseline, level);
            double bound = Math.Abs(AnalyticQuantile(baseline, level + 2d / Realizations)
                                  - AnalyticQuantile(baseline, level - 2d / Realizations));
            Assert.AreEqual(target, observed, bound,
                $"The {level:P0} Total-mean quantile must match the analytic map within the stratified bound.");
        }

        // The ensemble mean: 4·σ/√N with σ = M₀·0.1 (the conservative plain-MC bound).
        double meanTolerance = 4d * baseline * 0.1d / Math.Sqrt(Realizations);
        Assert.AreEqual(baseline, summary.Mean.Total.Mean, meanTolerance,
            "The ensemble-mean slot must match the deterministic baseline within 4·SE.");

        // The APF is knowledge-free on this scenario — a near-degenerate interval. It is not
        // bit-degenerate: the adaptive refinement follows the consequence draw, so each
        // realization records a slightly different point set and its recorded per-abscissa
        // mass differs at the ~1e-12 relative scale; 1e-9 bounds that placement noise.
        Assert.AreEqual(summary.Lower.Fail.TotalProbability, summary.Upper.Fail.TotalProbability,
            1e-9 * Math.Max(1e-12, summary.Upper.Fail.TotalProbability),
            "A knowledge-free failure probability must produce a (mass-noise) degenerate interval.");

        // The convergence indicator agrees with the interval evidence.
        var indicator = summary.Convergence.Indicators[1];
        Assert.AreEqual("Mean Total Risk", indicator.Label);
        Assert.AreEqual(summary.Mean.Total.Mean, indicator.Mean, 0d);
        Assert.IsTrue(indicator.EnsembleStandardError > 0d && indicator.CiHalfWidth > 0d);
    }

    /// <summary>
    /// Verifies the storage contract: the stored summary reproduces bit-for-bit from a JSON
    /// round-trip via <c>ComputeSummary</c>, and repeated runs (fresh thread schedules)
    /// reproduce every slot bit-for-bit — the sequential-reduction determinism pin.
    /// </summary>
    [TestMethod]
    public void Test_ScalarIntervals_RoundTrip_And_Reproducibility()
    {
        // Arrange / Act — two independent runs and a JSON round-trip of the first.
        var first = Build(uncertain: true);
        first.RunAsync().GetAwaiter().GetResult();
        var second = Build(uncertain: true);
        second.RunAsync().GetAwaiter().GetResult();
        var summary = first.RiskResults!.Summary!;
        var restored = EnsembleResults.FromJson(first.RiskResults.ToJson())!;
        var recomputed = restored.ComputeSummary(first.Options.ConfidenceIntervalWidth)!;

        // Assert — round-trip recomputation is bit-identical on every slot family.
        Assert.AreEqual(summary.Mean.Total.Mean, recomputed.Mean.Total.Mean, 0d);
        Assert.AreEqual(summary.Lower.Total.Mean, recomputed.Lower.Total.Mean, 0d);
        Assert.AreEqual(summary.Upper.Total.ValueAtRisk, recomputed.Upper.Total.ValueAtRisk, 0d);
        Assert.AreEqual(summary.Median.Fail.TotalProbability, recomputed.Median.Fail.TotalProbability, 0d);
        Assert.AreEqual(summary.Convergence.Indicators[0].EnsembleStandardError,
            recomputed.Convergence.Indicators[0].EnsembleStandardError, 0d);

        // Repeated runs are bit-identical (index-owned writes; sequential reductions).
        var secondSummary = second.RiskResults!.Summary!;
        Assert.AreEqual(summary.Mean.Total.Mean, secondSummary.Mean.Total.Mean, 0d);
        Assert.AreEqual(summary.Upper.Total.ConditionalValueAtRisk, secondSummary.Upper.Total.ConditionalValueAtRisk, 0d);
        Assert.AreEqual(summary.Lower.Fail.TotalProbability, secondSummary.Lower.Fail.TotalProbability, 0d);
        Assert.AreEqual(summary.Convergence.TotalFunctionEvaluations, secondSummary.Convergence.TotalFunctionEvaluations, 0d);
    }
}
