using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Verification.Analyses.Risk;

/// <summary>
/// Latin hypercube variance reduction: at N = 1,000 knowledge
/// realizations over repeated runs, the Latin hypercube scheme's ensemble grand mean must be
/// unbiased relative to plain Monte Carlo sampling and carry a substantially smaller
/// replicate-to-replicate variance — the deliberate v1.1 sampling upgrade the
/// <see cref="SamplingScheme"/> option carries.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Scenario:</b> a deterministic z-grid stage-frequency hazard from Normal(100, 20); a
/// DETERMINISTIC two-knot fragility; uncertain two-knot failure and non-failure consequence
/// curves (Triangular ordinates, the failure mode's failure/non-failure coupling pairing them into ONE
/// stratified knowledge dimension). Each knowledge realization integrates deterministically
/// (adaptive Gauss–Kronrod), so the only stochastic input is the coupling-matrix percentile
/// the sampling scheme controls — and the total-risk statistic is LINEAR in that percentile's
/// quantile functions. The fragility is deliberately deterministic: an uncertain response
/// would add a multiplicative P_F·C interaction whose variance per-dimension Latin hypercube
/// cannot remove (measured on the interactive variant: the ratio converges to the reciprocal
/// of the interaction share, ≈ 10 for that fixture) — the linear fixture isolates the property
/// under test, the stratification of the engine's knowledge-percentile streams, for which the
/// variance ratio is orders of magnitude.
/// </para>
/// <para>
/// <b>Design:</b> per scheme, five replicate full-uncertainty runs at 1,000 realizations
/// differing only in <c>PRNGSeed</c> (the content-based seed derivation folds the seed, so each
/// replicate draws an independent stream). The per-run statistic is the ensemble grand mean of
/// the total risk mean. Latin hypercube stratifies the coupling percentile stream into N
/// equal-probability bins, so the grand mean's sampling variance collapses relative to
/// independent uniform sampling (order N⁻³ versus N⁻¹ for a smooth monotone statistic). A
/// ratio near one would also be the loud failure signature if the scheme option ever stopped
/// reaching the failure mode's coupling matrix.
/// </para>
/// <para>
/// <b>Asserts and tolerances:</b> (1) the replicate variance ratio var(MC)/var(LHS) must exceed
/// 10 — the linear fixture's true ratio is orders of magnitude larger, so five-replicate
/// sample variances cannot plausibly invert below the threshold; (2) the two schemes'
/// replicate-pooled grand means agree within 4·√(var(MC)/R + var(LHS)/R) (unbiasedness);
/// (3) both pooled means match the deterministic mean-only run within the same combined error
/// — the expectation is linear in the sampled dimension and the symmetric Triangulars make
/// the expected curve the mean curve; (4) every replicate's LHS grand mean differs from every
/// other's (the seed must genuinely move the stratified stream).
/// </para>
/// </remarks>
[TestClass]
public class LhsVarianceReductionVerification
{
    /// <summary>The knowledge-uncertainty realization count per run (N = 1,000).</summary>
    private const int Realizations = 1000;

    /// <summary>
    /// The replicate run count per sampling scheme. Five is statistically ample: the ratio
    /// assert triggers only if the four-degree-of-freedom sample variances invert the true
    /// ratio (orders of magnitude) below 10 — a probability in the 1e-5 range — while every
    /// additional replicate costs a complete 1,000-realization ensemble analysis.
    /// </summary>
    private const int Replicates = 5;

    /// <summary>The replicate seed base (PRNGSeed = base + replicate index).</summary>
    private const int SeedBase = 20_260_723;

    /// <summary>The minimum accepted var(MC)/var(LHS) replicate variance ratio.</summary>
    private const double MinimumVarianceRatio = 10d;

    /// <summary>The shared z-grid step of the tabulated hazard.</summary>
    private const double ZStep = 0.25d;

    /// <summary>The shared z-grid half-range of the tabulated hazard.</summary>
    private const double ZRange = 8d;

    #region Builders

    /// <summary>Builds the deterministic z-grid stage-frequency hazard from Normal(100, 20).</summary>
    private static TabularHazard Hazard()
    {
        int count = (int)Math.Round(2d * ZRange / ZStep) + 1;
        var ordinates = new UncertainOrdinate[count];
        for (int i = 0; i < count; i++)
        {
            double z = -ZRange + i * ZStep;
            ordinates[i] = new UncertainOrdinate(1d - Normal.StandardCDF(z), new Deterministic(100d + 20d * z));
        }
        return new TabularHazard
        {
            Name = "Stage Frequency",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            ProbabilityTransform = Transform.None,
            NoUncertaintyFunction = new UncertainOrderedPairedData(ordinates,
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>
    /// Builds the deterministic two-knot fragility. Deterministic by design: an uncertain
    /// response would put a multiplicative interaction into the total-risk statistic that
    /// per-dimension Latin hypercube cannot stratify away (class remarks).
    /// </summary>
    private static TabularResponse Fragility()
    {
        return new TabularResponse
        {
            Name = "Fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(100d, new Deterministic(0.05d)),
                    new UncertainOrdinate(200d, new Deterministic(0.8d)),
                },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds an uncertain two-knot consequence curve.</summary>
    /// <param name="name">The function name.</param>
    /// <param name="low">The low-knot distribution (stage 60).</param>
    /// <param name="top">The top-knot distribution (stage 200).</param>
    private static TabularConsequence Consequence(string name, Triangular low, Triangular top)
    {
        return new TabularConsequence
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Loss",
            ConsequenceUnit = "lives",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(60d, low), new UncertainOrdinate(200d, top) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Triangular),
        };
    }

    /// <summary>Builds one replicate analysis: the uncertain component with the scheme and seed under test.</summary>
    /// <param name="scheme">The knowledge sampling scheme.</param>
    /// <param name="seed">The options PRNG seed.</param>
    /// <param name="meanOnly">True for the deterministic expected-function pass.</param>
    private static RiskAnalysis Build(SamplingScheme scheme, int seed, bool meanOnly)
    {
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = Hazard();
        component.AddFailureMode(new FailureMode(null, null, Fragility(),
            Consequence("Uncertain Failure Loss", new Triangular(0d, 10d, 20d), new Triangular(400d, 1000d, 1600d))));
        component.AddFailureMode(new FailureMode(null, null, null,
            Consequence("Uncertain Non-Failure Loss", new Triangular(0d, 5d, 10d), new Triangular(200d, 500d, 800d))));

        var analysis = new RiskAnalysis(new[] { component }) { Name = $"LHS variance {scheme} {seed}" };
        analysis.Options.EstimateMeanRiskOnly = meanOnly;
        analysis.Options.Realizations = Realizations;
        analysis.Options.SamplingScheme = scheme;
        analysis.Options.PRNGSeed = seed;
        // The replicate statistic is the grand mean alone, and the integrator error is common
        // to both schemes (each realization's sampled functions integrate deterministically),
        // so the default 1e-8 refinement buys nothing here — a 1e-6 tolerance with the minimum
        // evaluation budget and the minimum output resolution cuts the per-realization cost an
        // order of magnitude while keeping the residual integration noise (≲ 1e-6 relative per
        // realization, averaged 1000-fold in each grand mean) far below the Latin hypercube
        // replicate variance the ratio assert measures. The ensemble discipline is pinned
        // in-test at the same 1e-6: this family studies SAMPLING variance, so the
        // engine's relaxed ensemble default (1e-4) would inject a common integration-noise
        // floor into the very ratio the family measures.
        analysis.Options.UseDefaults = false;
        analysis.Options.Tolerance = 1e-6;
        analysis.Options.EnsembleTolerance = 1e-6;
        analysis.Options.EnsembleMinDepth = 2;
        analysis.Options.MaxEvaluations = 10_000;
        analysis.Options.LECOutputLength = 50;
        return analysis;
    }

    /// <summary>Runs an analysis synchronously and asserts it estimated, surfacing the completion error.</summary>
    /// <param name="analysis">The analysis to run.</param>
    /// <param name="label">The assert label.</param>
    private static void Run(RiskAnalysis analysis, string label)
    {
        Exception? error = null;
        analysis.AnalysisCompleted += (_, e) => error = e.Error;
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated,
            $"{label}: the analysis must estimate.{(error == null ? string.Empty : $" Run error: {error}")}");
    }

    /// <summary>Runs one replicate and returns its ensemble grand mean of the total risk mean.</summary>
    /// <param name="scheme">The knowledge sampling scheme.</param>
    /// <param name="seed">The options PRNG seed.</param>
    private static double GrandMean(SamplingScheme scheme, int seed)
    {
        var analysis = Build(scheme, seed, meanOnly: false);
        Run(analysis, $"{scheme} seed {seed}");
        double sum = 0d;
        for (int i = 0; i < Realizations; i++)
        {
            sum += analysis.RiskResults![i]!.Total.Mean;
        }
        return sum / Realizations;
    }

    /// <summary>The unbiased sample mean and variance of a replicate set.</summary>
    /// <param name="values">The replicate values.</param>
    private static (double Mean, double Variance) MeanAndVariance(double[] values)
    {
        double mean = 0d;
        for (int i = 0; i < values.Length; i++) mean += values[i];
        mean /= values.Length;
        double variance = 0d;
        for (int i = 0; i < values.Length; i++)
        {
            double delta = values[i] - mean;
            variance += delta * delta;
        }
        return (mean, variance / (values.Length - 1));
    }

    #endregion

    /// <summary>
    /// The variance-reduction gate: five replicate 1,000-realization runs per scheme —
    /// Latin hypercube must cut the grand-mean replicate variance by at least an order of
    /// magnitude relative to Monte Carlo while agreeing on the pooled mean, both schemes must
    /// reproduce the deterministic mean-only answer, and distinct seeds must produce distinct
    /// stratified streams.
    /// </summary>
    [TestMethod]
    public void Test_LhsVsMonteCarlo_GrandMeanVarianceReduction()
    {
        // Arrange / Act — the replicate grand means per scheme.
        var lhsMeans = new double[Replicates];
        var mcMeans = new double[Replicates];
        for (int r = 0; r < Replicates; r++)
        {
            lhsMeans[r] = GrandMean(SamplingScheme.LatinHypercube, SeedBase + r);
            mcMeans[r] = GrandMean(SamplingScheme.MonteCarlo, SeedBase + r);
        }
        var (lhsMean, lhsVariance) = MeanAndVariance(lhsMeans);
        var (mcMean, mcVariance) = MeanAndVariance(mcMeans);

        // Assert — the variance-reduction ratio.
        Assert.IsTrue(lhsVariance > 0d, "The LHS replicate variance must be positive (distinct seeds must move the stream).");
        double ratio = mcVariance / lhsVariance;
        Assert.IsTrue(ratio >= MinimumVarianceRatio,
            $"Latin hypercube must reduce the grand-mean replicate variance at least {MinimumVarianceRatio}× " +
            $"(measured var(MC) = {mcVariance:G4}, var(LHS) = {lhsVariance:G4}, ratio = {ratio:G4}).");

        // Unbiasedness: the pooled scheme means agree within the combined replicate errors.
        double combinedSe = Math.Sqrt(mcVariance / Replicates + lhsVariance / Replicates);
        Assert.AreEqual(mcMean, lhsMean, 4d * combinedSe,
            "The Latin hypercube and Monte Carlo pooled grand means must agree (both unbiased).");

        // Both schemes reproduce the deterministic expected-function answer (the expectation is
        // linear in each independently sampled knowledge dimension).
        var meanOnly = Build(SamplingScheme.LatinHypercube, SeedBase, meanOnly: true);
        Run(meanOnly, "mean-only");
        double meanOnlyTotal = meanOnly.RiskResults![0]!.Total.Mean;
        Assert.AreEqual(meanOnlyTotal, mcMean, 4d * Math.Sqrt(mcVariance / Replicates),
            "The Monte Carlo pooled grand mean must reproduce the deterministic mean-only answer.");
        Assert.AreEqual(meanOnlyTotal, lhsMean, Math.Max(4d * Math.Sqrt(lhsVariance / Replicates), 1e-6 * meanOnlyTotal),
            "The Latin hypercube pooled grand mean must reproduce the deterministic mean-only answer.");

        // Every LHS replicate is distinct — the seed genuinely re-randomizes the stratification.
        for (int a = 0; a < Replicates; a++)
        {
            for (int b = a + 1; b < Replicates; b++)
            {
                Assert.AreNotEqual(lhsMeans[a], lhsMeans[b],
                    $"LHS replicates {a} and {b} must differ — the PRNG seed must move the stratified stream.");
            }
        }

        Console.WriteLine(
            $"LHS variance reduction: var(MC) = {mcVariance:G4}, var(LHS) = {lhsVariance:G4}, ratio = {ratio:G4}; " +
            $"pooled means MC {mcMean:G8} / LHS {lhsMean:G8} / mean-only {meanOnlyTotal:G8}");
    }

    /// <summary>
    /// The scrambled-Sobol scheme gate on the same fixture: five replicate runs must cut the
    /// grand-mean replicate variance against Monte Carlo by at least the family floor while
    /// agreeing on the pooled mean and the deterministic mean-only answer, distinct seeds must
    /// move the scramble, an identical seed must reproduce bit-for-bit (the content-seed
    /// contract the unrandomized sequence cannot honor), and renaming the model must be
    /// bit-inert (the scramble seed derives from content, never names).
    /// </summary>
    /// <remarks>
    /// <b>Tolerance derivation:</b> the same replicate-variance framework as the Latin
    /// hypercube gate — the family floor of 10× is conservative for a low-discrepancy scheme on
    /// this near-linear statistic (the fixture's 1,000-realization count is not a power of two,
    /// which validation advises on; the stratification remains far better than plain Monte
    /// Carlo). The unbiasedness and mean-only asserts reuse the 4·SE combined-replicate bounds;
    /// the reproducibility and rename pins are exact (0, bit).
    /// </remarks>
    [TestMethod]
    public void Test_ScrambledSobolVsMonteCarlo_GrandMeanVarianceReduction()
    {
        // Arrange / Act — the replicate grand means per scheme.
        var sobolMeans = new double[Replicates];
        var mcMeans = new double[Replicates];
        for (int r = 0; r < Replicates; r++)
        {
            sobolMeans[r] = GrandMean(SamplingScheme.ScrambledSobol, SeedBase + r);
            mcMeans[r] = GrandMean(SamplingScheme.MonteCarlo, SeedBase + r);
        }
        var (sobolMean, sobolVariance) = MeanAndVariance(sobolMeans);
        var (mcMean, mcVariance) = MeanAndVariance(mcMeans);

        // Assert — the variance-reduction ratio and unbiasedness.
        Assert.IsTrue(sobolVariance > 0d, "The Sobol replicate variance must be positive (distinct seeds must move the scramble).");
        double ratio = mcVariance / sobolVariance;
        Assert.IsTrue(ratio >= MinimumVarianceRatio,
            $"Scrambled Sobol must reduce the grand-mean replicate variance at least {MinimumVarianceRatio}× " +
            $"(measured var(MC) = {mcVariance:G4}, var(Sobol) = {sobolVariance:G4}, ratio = {ratio:G4}).");
        double combinedSe = Math.Sqrt(mcVariance / Replicates + sobolVariance / Replicates);
        Assert.AreEqual(mcMean, sobolMean, 4d * combinedSe,
            "The scrambled-Sobol and Monte Carlo pooled grand means must agree (both unbiased).");

        var meanOnly = Build(SamplingScheme.ScrambledSobol, SeedBase, meanOnly: true);
        Run(meanOnly, "Sobol mean-only");
        double meanOnlyTotal = meanOnly.RiskResults![0]!.Total.Mean;
        Assert.AreEqual(meanOnlyTotal, sobolMean, Math.Max(4d * Math.Sqrt(sobolVariance / Replicates), 1e-6 * meanOnlyTotal),
            "The scrambled-Sobol pooled grand mean must reproduce the deterministic mean-only answer.");

        // Distinct seeds move the scramble; an identical seed reproduces bit-for-bit.
        for (int a = 0; a < Replicates; a++)
        {
            for (int b = a + 1; b < Replicates; b++)
            {
                Assert.AreNotEqual(sobolMeans[a], sobolMeans[b],
                    $"Sobol replicates {a} and {b} must differ — the PRNG seed must move the scramble.");
            }
        }
        Assert.AreEqual(sobolMeans[0], GrandMean(SamplingScheme.ScrambledSobol, SeedBase), 0d,
            "An identical seed must reproduce the scrambled grand mean bit-for-bit.");

        // Renaming is bit-inert: the scramble seed derives from content hashes, never names.
        var renamed = Build(SamplingScheme.ScrambledSobol, SeedBase, meanOnly: false);
        renamed.Components[0].Name = "Renamed component";
        renamed.Components[0].HazardFunction!.Name = "Renamed hazard";
        Run(renamed, "Sobol renamed");
        double renamedGrand = 0d;
        for (int i = 0; i < Realizations; i++) renamedGrand += renamed.RiskResults![i]!.Total.Mean;
        renamedGrand /= Realizations;
        Assert.AreEqual(sobolMeans[0], renamedGrand, 0d,
            "Renaming must be bit-inert under the scrambled scheme (content-based seeding).");

        Console.WriteLine(
            $"Scrambled-Sobol variance reduction: var(MC) = {mcVariance:G4}, var(Sobol) = {sobolVariance:G4}, ratio = {ratio:G4}; " +
            $"pooled means MC {mcMean:G8} / Sobol {sobolMean:G8} / mean-only {meanOnlyTotal:G8}");
    }
}
