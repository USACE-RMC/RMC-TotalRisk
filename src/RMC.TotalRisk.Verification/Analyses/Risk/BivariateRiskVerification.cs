using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using Numerics.Sampling;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Transforms;
using RMC.TotalRisk.Systems.Components;
using RMC.TotalRisk.Systems.Components.Graph;

namespace RMC.TotalRisk.Verification.Analyses.Risk;

/// <summary>
/// Bivariate risk verification — the conversion of the legacy <c>Test_BivariateRisk</c>
/// oracles and the re-anchored DAMRAE chained-bilinear scenario, plus the deliberate
/// consistency cross-anchor between the preserved collapse method and the bivariate-hazard
/// path. The legacy seismic fixtures (curves, surface, life loss) are shared verbatim with
/// <see cref="BivariateOracleFixtures"/>; every discretization allowance cites the
/// convergence study in <see cref="CopulaDependenceVerification"/> as its derivation source.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Oracle policy.</b> The ported oracles preserve the legacy fixed seed
/// (<c>MersenneTwister(45678)</c>) and per-realization draw order at 1,000,000 realizations
/// (the legacy 100,000,000 dropped 100× per the conversion policy; every statistical
/// tolerance derives from the realization count actually run). Engine-versus-oracle
/// comparisons are statistical (k·SE with k = 4) with the engine's own conditional-bin
/// discretization allowance added where it is not negligible against the Monte Carlo error;
/// the discretization figures are the convergence study's pinned measurements, never
/// assumptions.
/// </para>
/// </remarks>
[TestClass]
public class BivariateRiskVerification
{
    /// <summary>The oracle realization count (legacy 100M dropped 100× per the conversion policy).</summary>
    private const int OracleRealizations = 1_000_000;

    /// <summary>The legacy oracle seed.</summary>
    private const int OracleSeed = 45678;

    /// <summary>The tolerance multiplier on Monte Carlo standard errors.</summary>
    private const double K = 4d;

    /// <summary>
    /// The legacy 100,000,000-realization mean of <c>Test_Bivariate_SRP</c> —
    /// E_Y[CDF(0.8, Y)] — re-captured by running the ported oracle at the legacy count and
    /// seed (the legacy test printed to the debugger and recorded nothing, so this constant
    /// is the run of record; the capture is documented in
    /// docs/verification/bivariate-risk.md). Kept as the k·SE cross-check against the exact
    /// closed form.
    /// </summary>
    private const double LegacySrp100MMean = 0.029662826999636242;

    /// <summary>The in-run standard deviation of the 100M SRP capture (for its standard error).</summary>
    private const double LegacySrp100MSigma = 0.079485927;

    #region Legacy seismic oracle

    /// <summary>The 1a oracle outputs.</summary>
    /// <param name="Ead">The mean life loss over all realizations (zeros included).</param>
    /// <param name="EadSe">The Monte Carlo standard error of the mean.</param>
    /// <param name="Afp">The failure fraction.</param>
    /// <param name="Failures">The failure count.</param>
    /// <param name="ProbeExceedance">P(loss ≥ probe) per probe level.</param>
    private readonly record struct SeismicOracle(double Ead, double EadSe, double Afp, int Failures,
        IReadOnlyList<double> ProbeExceedance);

    /// <summary>The FN probe levels (lives) for the seismic oracle comparison.</summary>
    private static readonly double[] SeismicProbes = { 1d, 25d, 50d, 100d, 150d, 200d };

    /// <summary>
    /// The ported legacy oracle (<c>Test_BivariateRisk.vb:12</c>): per realization draw
    /// (pga, stage, rnd) in that exact order — rnd unconditionally, matching the legacy
    /// loop — interpolate the surface at (pga, stage) through the same
    /// <see cref="BivariateEmpirical"/> the legacy test used, and on failure record the
    /// stage-interpolated life loss. Welford accumulation gives the mean and its dispersion.
    /// </summary>
    private static SeismicOracle RunSeismicOracle()
    {
        var pgaFrequency = new EmpiricalDistribution(
            BivariateOracleFixtures.PgaLevels, BivariateOracleFixtures.PgaExceedance,
            SortOrder.Ascending, SortOrder.Descending)
        { ProbabilityTransform = Transform.Logarithmic };
        var stageDuration = new EmpiricalDistribution(
            BivariateOracleFixtures.StageLevels, BivariateOracleFixtures.StageExceedance,
            SortOrder.Ascending, SortOrder.Descending)
        { ProbabilityTransform = Transform.NormalZ };
        var surface = new BivariateEmpirical(
            BivariateOracleFixtures.SurfacePrimaryLevels, BivariateOracleFixtures.SurfaceSecondaryLevels,
            (double[,])BivariateOracleFixtures.SurfaceProbabilities.Clone())
        { ProbabilityTransform = Transform.Logarithmic };
        var lifeLoss = new Linear(BivariateOracleFixtures.LifeLossStages, BivariateOracleFixtures.LifeLossValues);

        var stream = new MersenneTwister(OracleSeed);
        double mean = 0d, m2 = 0d;
        int failures = 0;
        var probeCounts = new int[SeismicProbes.Length];
        for (int i = 0; i < OracleRealizations; i++)
        {
            double pga = pgaFrequency.InverseCDF(stream.NextDouble());
            double stage = stageDuration.InverseCDF(stream.NextDouble());
            double srp = surface.CDF(pga, stage);
            double rnd = stream.NextDouble();
            double loss = 0d;
            if (rnd <= srp)
            {
                failures++;
                loss = lifeLoss.Interpolate(stage);
                for (int p = 0; p < SeismicProbes.Length; p++)
                {
                    if (loss >= SeismicProbes[p]) probeCounts[p]++;
                }
            }
            double delta = loss - mean;
            mean += delta / (i + 1);
            m2 += delta * (loss - mean);
        }
        double sigma = Math.Sqrt(m2 / OracleRealizations);
        var probeExceedance = new double[SeismicProbes.Length];
        for (int p = 0; p < SeismicProbes.Length; p++) probeExceedance[p] = probeCounts[p] / (double)OracleRealizations;
        return new SeismicOracle(mean, sigma / Math.Sqrt(OracleRealizations), failures / (double)OracleRealizations,
            failures, probeExceedance);
    }

    /// <summary>Runs the seismic engine scenario and returns its failure-stream outputs.</summary>
    /// <param name="bins">The conditional-integration bin count.</param>
    private static (double Ead, double Afp, RiskAnalysis Analysis) RunSeismicEngine(int bins)
    {
        var analysis = new RiskAnalysis(new[] { BivariateOracleFixtures.JointComponent(bins) });
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated, "The seismic engine run must estimate.");
        var summary = analysis.RiskResults![0]!;
        return (summary.Fail.Mean, summary.Fail.TotalProbability, analysis);
    }

    #endregion

    /// <summary>
    /// The legacy <c>Test_Bivariate_Risk</c> oracle versus the engine: the same curves,
    /// surface, and stage-bound life loss run through the adaptive two-dimensional interior
    /// at the thousand-bin and default refinement budgets. EAD asserts at K·σ̂/√N plus the
    /// adaptive conditional figure; the failure probability at the binomial K·√(p(1−p)/N)
    /// plus the same figure; FN ordinates at probe levels with at least ~100 expected
    /// exceedances, binomially; and the two budgets must agree with each other (the
    /// historical fixed-grid overshoot regime collapses under the adaptive interior — the
    /// re-anchoring is recorded in docs/verification/bivariate-risk.md).
    /// </summary>
    [TestMethod]
    public void Test_BivariateRisk_EngineVsLegacyOracle()
    {
        // Arrange — the ported oracle.
        var oracle = RunSeismicOracle();

        // Act — the engine at near-exact and default bins.
        var engine1000 = RunSeismicEngine(1000);
        var engine20 = RunSeismicEngine(20);

        // Assert — the adaptive engine against the oracle: K·SE plus the adaptive
        // conditional figure (1e-4 relative, measured 3.7e-6 on this fixture class —
        // negligible beside the Monte Carlo error at 298 failures).
        double afpSe = Math.Sqrt(oracle.Afp * (1d - oracle.Afp) / OracleRealizations);
        Assert.AreEqual(oracle.Ead, engine1000.Ead,
            K * oracle.EadSe + 1e-4 * engine1000.Ead,
            "Thousand-bin-budget EAD vs the legacy oracle.");
        Assert.AreEqual(oracle.Afp, engine1000.Afp,
            K * afpSe + 1e-4 * engine1000.Afp,
            "Thousand-bin-budget failure probability vs the legacy oracle.");

        // FN ordinates at the qualified probe levels — expected exceedance counts
        // {298, 298, 271, 179, 102} at 1M; the 200-lives probe is excluded with 12 expected
        // exceedances (< ~100 per the family policy). Binomial K·√(p(1−p)/N) plus the
        // adaptive conditional figure.
        var failCurve = engine1000.Analysis.MeanRiskResults!.Curves.Fail;
        for (int p = 0; p < SeismicProbes.Length - 1; p++)
        {
            double oracleExceedance = oracle.ProbeExceedance[p];
            double engineExceedance = failCurve.LEC.GetYFromX(SeismicProbes[p], Transform.Logarithmic, Transform.Logarithmic);
            double probeSe = Math.Sqrt(oracleExceedance * (1d - oracleExceedance) / OracleRealizations);
            Assert.AreEqual(oracleExceedance, engineExceedance,
                K * probeSe + 1e-4 * engineExceedance,
                $"FN ordinate at {SeismicProbes[p]} lives.");
        }

        // The budget-consistency pin, re-anchored under the two-dimensional adaptive interior
        // ruling (2026-09-05): the historical 20-versus-1000-bin trapezoid overshoot regime
        // (failure probability 1.351, EAD 2.418 at the run of record) collapses to unity —
        // the bin knob bounds the per-slice refinement budget rather than fixing a grid, and
        // this fixture converges below either budget, so the two configurations must agree.
        // The fixed grid's overshoot regime remains measured by the discretization
        // instrument in the copula-dependence family.
        double afpRatio = engine20.Afp / engine1000.Afp;
        double eadRatio = engine20.Ead / engine1000.Ead;
        Assert.IsTrue(Math.Abs(afpRatio - 1d) < 5e-3,
            $"The default-budget failure probability must agree with the thousand-bin budget (ratio {afpRatio:G6}).");
        Assert.IsTrue(Math.Abs(eadRatio - 1d) < 5e-3,
            $"The default-budget EAD must agree with the thousand-bin budget (ratio {eadRatio:G6}).");

        Console.WriteLine($"seismic: oracle EAD {oracle.Ead:G8} (se {oracle.EadSe:G4}) AFP {oracle.Afp:G8} " +
            $"({oracle.Failures} failures); engine(1000) EAD {engine1000.Ead:G8} AFP {engine1000.Afp:G8}; " +
            $"engine(20) ratios EAD {eadRatio:G4} AFP {afpRatio:G4}");
    }

    /// <summary>
    /// The legacy <c>Test_Bivariate_SRP</c> target E_Y[CDF(0.8, Y)] four ways: the exact
    /// union-grid closed form (derived in <see cref="BivariateOracleFixtures"/> and
    /// cross-checked there against a dense Simpson reference), the re-captured legacy 100M
    /// constant at its k·SE, the adaptive engine's marginalized failure probability through a
    /// degenerate primary bracket at PGA 0.8, and the per-slice probit sweep evaluated
    /// directly against the fixed-grid instrument at a thousand bins. Re-anchored under the
    /// two-dimensional adaptive interior ruling (2026-09-05): the probit conditional
    /// coordinate resolves the stage marginal's normal-Z tail concentration that rate-limited
    /// the fixed grid, so the full-run probes land within 1e-4 relative of the closed form at
    /// EITHER configured bin count (measured 3.7e-6 at both — the bin knob bounds the
    /// per-slice refinement budget, and this fixture converges far below it), the per-slice
    /// sweep reproduces the closed form to 1e-9 relative (measured 5.3e-12), and the
    /// fixed-grid instrument's thousand-bin value keeps its historical 4e-3 allowance.
    /// </summary>
    [TestMethod]
    public void Test_BivariateSRP_ClosedForm()
    {
        // Arrange — the exact target and the legacy-constant cross-check.
        double exact = BivariateOracleFixtures.ExactMarginalizedSurface(0.8d);
        double legacySe = LegacySrp100MSigma / Math.Sqrt(100_000_000d);
        Assert.AreEqual(exact, LegacySrp100MMean, K * legacySe,
            "The re-captured legacy 100M mean must sit within k·SE of the exact closed form.");

        // Act — the adaptive full-run probes at both configured budgets.
        double probe1000 = RunProbe(1000);
        double probe20 = RunProbe(20);

        // The per-slice decomposition: the adaptive probit sweep and the fixed-grid
        // instrument evaluated at the same slice.
        var adaptiveComponent = BivariateOracleFixtures.JointComponent(1000, BivariateOracleFixtures.DegeneratePrimary(0.8d));
        adaptiveComponent.SetupSamplers(100, 12345, RMC.TotalRisk.Core.Enums.SamplingScheme.LatinHypercube);
        var adaptiveSampled = adaptiveComponent.Sample(-1);
        double sliceLevel = adaptiveSampled.Hazard.InverseCDF(0.5d);
        double adaptiveSlice = adaptiveSampled.ComputeRisk(0.5d, sliceLevel, new RMC.TotalRisk.Results.RiskComputeFlags(),
            new RMC.TotalRisk.Results.ComponentRealization(adaptiveSampled.FailureModeCount)).ProbabilityOfFailure;

        var fixedComponent = BivariateOracleFixtures.JointComponent(1000, BivariateOracleFixtures.DegeneratePrimary(0.8d));
        fixedComponent.SetupSamplers(100, 12345, RMC.TotalRisk.Core.Enums.SamplingScheme.LatinHypercube);
        var fixedSampled = fixedComponent.SampleWithConditionalBins(1000);
        double fixedSlice = fixedSampled.ComputeRisk(0.5d, sliceLevel, new RMC.TotalRisk.Results.RiskComputeFlags(),
            new RMC.TotalRisk.Results.ComponentRealization(fixedSampled.FailureModeCount)).ProbabilityOfFailure;

        // Assert — the adaptive figures (measured 3.7e-6 relative full-run, 5.3e-12 per
        // slice; asserted with documented headroom), and the instrument's historical figure.
        Assert.AreEqual(exact, probe1000, 1e-4 * exact,
            "The adaptive probe at the thousand-bin budget must reproduce the closed form within the pinned figure.");
        Assert.AreEqual(exact, probe20, 1e-4 * exact,
            "The adaptive probe at the default budget must reproduce the closed form within the pinned figure.");
        Assert.AreEqual(exact, adaptiveSlice, 1e-9 * exact,
            "The per-slice probit sweep must reproduce the closed form to quadrature accuracy.");
        Assert.AreEqual(exact, fixedSlice, BivariateOracleFixtures.LegacySrpBins1000RelativeError * exact,
            "The fixed-grid instrument at a thousand bins keeps its historical near-exact figure.");
        Console.WriteLine($"srp closed form {exact:G17}, legacy 100M {LegacySrp100MMean:G17} (se {legacySe:G4}), " +
            $"probe(1000) {probe1000:G17}, probe(20) {probe20:G17}, slice adaptive {adaptiveSlice:G17}, slice fixed(1000) {fixedSlice:G17}");

        // Runs the degenerate-primary probe at the given bin count.
        static double RunProbe(int bins)
        {
            var analysis = new RiskAnalysis(new[]
            {
                BivariateOracleFixtures.JointComponent(bins, BivariateOracleFixtures.DegeneratePrimary(0.8d)),
            });
            analysis.RunAsync().GetAwaiter().GetResult();
            Assert.IsTrue(analysis.IsEstimated, "The probe run must estimate.");
            return analysis.RiskResults![0]!.Fail.TotalProbability;
        }
    }

    #region DAMRAE re-anchored fixtures

    /// <summary>The PFM-08 PGA levels (g), ascending (Test_DAMRAE.vb, verbatim).</summary>
    private static readonly double[] DamraePgaLevels =
        { 0.025d, 0.073831396d, 0.153234028d, 0.220828354d, 0.278729593d, 0.329787815d, 0.4d };

    /// <summary>The PFM-08 PGA exceedance probabilities, descending (log-interpolated).</summary>
    private static readonly double[] DamraePgaExceedance = { 0.36d, 0.11d, 0.016d, 0.0031d, 0.00076d, 0.00022d, 0.00004d };

    /// <summary>The PFM-08 pool levels (ft), ascending (Test_DAMRAE.vb, verbatim).</summary>
    private static readonly double[] DamraePoolLevels =
    {
        576.39d, 579.12d, 580.0975d, 581.9d, 583.45d, 586.83d, 588.15d, 589.03d, 593.075d, 594.88d,
        601.25d, 607.995d, 610.05d, 613.485d, 623.95d, 629.59d, 635.0275d, 636.48d, 638.73d,
        639.6275d, 641.42d, 643.65d, 644.86d, 648.82d, 651.5544d, 652.41d, 656.12d, 656.4d,
        656.51d, 658.33d, 659d,
    };

    /// <summary>
    /// The PFM-08 pool exceedance probabilities, descending (normal-Z interpolated). The
    /// legacy end knots carried probabilities exactly 1 and 0, which the normal-Z transform
    /// maps to ±∞; the re-anchored fixture trims them to 0.9999 and 0.00001 (order preserved
    /// against the neighboring 0.997 and 0.00007) — recorded as part of the re-anchoring in
    /// the traceability notes.
    /// </summary>
    private static readonly double[] DamraePoolExceedance =
    {
        0.9999d, 0.997d, 0.99d, 0.98d, 0.97d, 0.94d, 0.92d, 0.9d, 0.82d, 0.77d, 0.6d, 0.51d,
        0.48d, 0.43d, 0.32d, 0.25d, 0.19d, 0.18d, 0.16d, 0.15d, 0.13d, 0.1d, 0.09d, 0.06d,
        0.0375d, 0.0175d, 0.00347d, 0.002d, 0.00047d, 0.00007d, 0.00001d,
    };

    /// <summary>The deformation surface pool axis (ft) (Test_DAMRAE.vb, verbatim).</summary>
    private static readonly double[] DeformationPoolAxis = { 590d, 620d, 635d, 652.5d, 673d };

    /// <summary>The deformation surface: rows = PGA, columns = pool (ft of deformation; verbatim).</summary>
    private static readonly double[,] DeformationGrid =
    {
        { 0.2d, 0.3d, 0.3d, 0.3d, 0.3d },
        { 1.4d, 2.1d, 2.6d, 4.4d, 6.508571429d },
        { 4.5d, 4.7d, 5.5d, 7.1d, 8.974285714d },
        { 7.1d, 11d, 12d, 15d, 18.51428571d },
        { 12.5d, 27d, 28d, 31d, 34.51428571d },
    };

    /// <summary>The deformation surface PGA axis (g) (verbatim).</summary>
    private static readonly double[] DeformationPgaAxis = { 0.1d, 0.2d, 0.25d, 0.3d, 0.4d };

    /// <summary>
    /// The warning-time surface's re-anchored primary axis: the legacy probability knots
    /// {0.11, 0.14, 0.17, 0.28, 0.44, 0.63} mapped affinely onto deformation,
    /// def = (u − 0.11)·66 + 0.2, so the verbatim time grid becomes an exact function of
    /// (deformation, pool) — an affine reparameterization of one axis preserves bilinearity
    /// on the mapped knots. The map spans the deformation surface's output range
    /// [0.2, 34.514] exactly.
    /// </summary>
    private static readonly double[] TimeDeformationAxis = { 0.2d, 2.18d, 4.16d, 11.42d, 21.98d, 34.52d };

    /// <summary>The warning-time surface pool axis (ft) (verbatim).</summary>
    private static readonly double[] TimePoolAxis = { 567d, 635d, 652.5d, 659d };

    /// <summary>The warning-time grid (minutes; rows = deformation, columns = pool; verbatim).</summary>
    private static readonly double[,] TimeGrid =
    {
        { 0d, 0d, 0d, 0d },
        { 30d, 30d, 22.5d, 22.5d },
        { 60d, 60d, 45d, 45d },
        { 180d, 180d, 132d, 132d },
        { 360d, 360d, 261.82d, 261.82d },
        { 600d, 600d, 430d, 430d },
    };

    /// <summary>The re-anchored SRP surface time axis (minutes).</summary>
    private static readonly double[] SrpTimeAxis = { 0d, 60d, 180d, 360d, 600d };

    /// <summary>The re-anchored SRP surface pool axis (ft).</summary>
    private static readonly double[] SrpPoolAxis = { 567d, 620d, 640d, 652.5d, 659d };

    /// <summary>
    /// The re-anchored SRP surface (rows = time, columns = pool): probability rising with
    /// pool and falling with warning time, spanning the magnitudes of the legacy
    /// overtopping tabulation it replaces (the legacy scenario built its SRP from a
    /// per-realization triangular distribution — not representable as a deterministic
    /// surface — so this grid is the one genuinely new fixture of the re-anchoring).
    /// </summary>
    private static readonly double[,] SrpGrid =
    {
        { 0.00001d, 0.002d, 0.02d, 0.15d, 0.6d },
        { 0.00001d, 0.0015d, 0.015d, 0.12d, 0.5d },
        { 0.000008d, 0.001d, 0.01d, 0.08d, 0.38d },
        { 0.000005d, 0.0006d, 0.006d, 0.05d, 0.25d },
        { 0.000003d, 0.0004d, 0.004d, 0.03d, 0.15d },
    };

    /// <summary>The life-loss surface time axis (minutes) (verbatim).</summary>
    private static readonly double[] LifeLossTimeAxis =
        { 0d, 30d, 45d, 60d, 75d, 90d, 105d, 120d, 135d, 150d, 165d, 180d, 195d, 210d, 225d, 240d, 255d, 270d, 285d, 300d };

    /// <summary>The life-loss surface pool axis (ft) (verbatim).</summary>
    private static readonly double[] LifeLossPoolAxis = { 590d, 630d, 634d, 652.5d, 659d };

    /// <summary>The life-loss grid (lives; rows = time, columns = pool; verbatim).</summary>
    private static readonly double[,] DamraeLifeLossGrid =
    {
        { 9.4074d, 9.8774d, 9.97510379d, 451.36d, 451.36d },
        { 9.4074d, 9.8774d, 9.97510379d, 310.363d, 310.363d },
        { 9.4074d, 9.8774d, 9.97510379d, 296.032625d, 296.032625d },
        { 9.4074d, 9.8774d, 9.97510379d, 281.70225d, 281.70225d },
        { 9.4074d, 9.8774d, 9.97510379d, 267.371875d, 267.371875d },
        { 9.4074d, 9.8774d, 9.97510379d, 253.0415d, 253.0415d },
        { 9.4074d, 9.8774d, 9.97510379d, 238.711125d, 238.711125d },
        { 9.4074d, 9.8774d, 9.97510379d, 224.38075d, 224.38075d },
        { 9.4074d, 9.8774d, 9.97510379d, 210.050375d, 210.050375d },
        { 9.4074d, 9.8774d, 9.97510379d, 195.72d, 195.72d },
        { 9.4074d, 9.8774d, 9.97510379d, 173.597175d, 173.597175d },
        { 9.4074d, 9.8774d, 9.97510379d, 151.47435d, 151.47435d },
        { 9.4074d, 9.8774d, 9.97510379d, 129.351525d, 129.351525d },
        { 9.4074d, 9.8774d, 9.97510379d, 107.2287d, 107.2287d },
        { 9.4074d, 9.8774d, 9.97510379d, 85.105875d, 85.105875d },
        { 9.4074d, 9.8774d, 9.97510379d, 62.98305d, 62.98305d },
        { 9.4074d, 9.8774d, 9.97510379d, 40.860225d, 40.860225d },
        { 9.4074d, 9.8774d, 9.97510379d, 18.7374d, 18.7374d },
        { 9.4074d, 9.8774d, 9.97510379d, 18.7374d, 18.7374d },
        { 9.4074d, 9.8774d, 9.97510379d, 18.7374d, 18.7374d },
    };

    /// <summary>
    /// Builds the re-anchored DAMRAE engine component: the PGA × pool independence hazard
    /// feeding two chained bivariate transforms (deformation, then warning time on the
    /// remapped axis), the joint SRP surface on (time, pool), and the bivariate life-loss
    /// consequence on (time, pool).
    /// </summary>
    /// <param name="bins">The conditional-integration bin count.</param>
    private static SystemComponent DamraeComponent(int bins)
    {
        var joint = new BivariateHazard(
            BivariateOracleFixtures.DeterministicHazard("PFM-08 PGA", "PGA", "g",
                DamraePgaExceedance, DamraePgaLevels, Transform.Logarithmic),
            BivariateOracleFixtures.DeterministicHazard("PFM-08 Pool", "Pool", "ft",
                DamraePoolExceedance, DamraePoolLevels, Transform.NormalZ))
        {
            Name = "PFM-08 Joint Hazard",
            SpecifiedHazard = "PGA",
            HazardUnit = "g",
            SecondarySpecifiedHazard = "Pool",
            SecondaryHazardUnit = "ft",
            SecondaryIntegrationBins = bins,
        };
        var component = new SystemComponent(joint) { Name = "PFM-08" };
        var hazard = component.Graph.GetElements<HazardElement>().Single();

        var deformation = new TransformElement("Deformation")
        {
            Function = new BivariateTransform
            {
                Name = "Deformation Surface",
                SpecifiedHazard = "PGA",
                HazardUnit = "g",
                SecondarySpecifiedHazard = "Pool",
                SecondaryHazardUnit = "ft",
                TransformedHazard = "Deformation",
                TransformedHazardUnit = "ft",
                X1Values = (double[])DeformationPgaAxis.Clone(),
                X2Values = (double[])DeformationPoolAxis.Clone(),
                ZValues = (double[,])DeformationGrid.Clone(),
            },
            Input = new RiskConnection(hazard),
            SecondaryInput = new RiskConnection(hazard, 1),
        };
        component.Graph.AddElement(deformation);

        var warningTime = new TransformElement("Warning Time")
        {
            Function = new BivariateTransform
            {
                Name = "Warning Time Surface",
                SpecifiedHazard = "Deformation",
                HazardUnit = "ft",
                SecondarySpecifiedHazard = "Pool",
                SecondaryHazardUnit = "ft",
                TransformedHazard = "Warning Time",
                TransformedHazardUnit = "min",
                X1Values = (double[])TimeDeformationAxis.Clone(),
                X2Values = (double[])TimePoolAxis.Clone(),
                ZValues = (double[,])TimeGrid.Clone(),
            },
            Input = new RiskConnection(deformation),
            SecondaryInput = new RiskConnection(deformation, 1),
        };
        component.Graph.AddElement(warningTime);

        var srp = new BivariateResponse
        {
            Name = "Overtopping SRP",
            SpecifiedHazard = "Warning Time",
            HazardUnit = "min",
            SecondarySpecifiedHazard = "Pool",
            SecondaryHazardUnit = "ft",
            HazardTransform = Transform.None,
            SecondaryHazardTransform = Transform.None,
            ProbabilityTransform = Transform.None,
        };
        srp.PrimaryHazardLevels.Clear();
        foreach (double level in SrpTimeAxis) srp.PrimaryHazardLevels.Add(level);
        srp.SecondaryHazardLevels.Clear();
        foreach (double level in SrpPoolAxis)
        {
            srp.SecondaryHazardLevels.Add(new WeightedHazardLevel { Level = level, Weight = 1d / SrpPoolAxis.Length });
        }
        srp.ProbabilityValues = (double[,])SrpGrid.Clone();
        var breach = new ResponseElement("Overtopping Breach")
        {
            Function = srp,
            Input = new RiskConnection(warningTime),
            SecondaryInput = new RiskConnection(warningTime, 1),
        };
        component.Graph.AddElement(breach);

        var lifeLoss = new ConsequenceElement("Life Loss")
        {
            Input = new RiskConnection(breach),
            SecondaryInput = new RiskConnection(warningTime, 1),
        };
        lifeLoss.Functions.Add(new BivariateConsequence
        {
            Name = "Life Loss Surface",
            SpecifiedHazard = "Warning Time",
            HazardUnit = "min",
            SecondarySpecifiedHazard = "Pool",
            SecondaryHazardUnit = "ft",
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
            X1Values = (double[])LifeLossTimeAxis.Clone(),
            X2Values = (double[])LifeLossPoolAxis.Clone(),
            ZValues = (double[,])DamraeLifeLossGrid.Clone(),
        });
        component.Graph.AddElement(lifeLoss);
        return component;
    }

    /// <summary>The DAMRAE oracle outputs.</summary>
    /// <param name="Ead">The mean life loss over all realizations (zeros included).</param>
    /// <param name="EadSe">The Monte Carlo standard error of the mean.</param>
    /// <param name="Afp">The failure fraction.</param>
    /// <param name="Failures">The failure count.</param>
    private readonly record struct DamraeOracle(double Ead, double EadSe, double Afp, int Failures);

    /// <summary>
    /// The re-anchored DAMRAE oracle: per realization draw (pga, pool, rnd), evaluate the
    /// same four chained bilinear surfaces the engine holds — deformation(pga, pool) →
    /// time(def, pool) → SRP(time, pool) — and on failure record the life-loss surface at
    /// (time, pool). The legacy scenario's per-realization triangular SRP and its
    /// conditional warning-time draw are replaced by the deterministic chain (recorded in
    /// the traceability notes); the pinned generator is <see cref="MersenneTwister"/> at the
    /// family seed, replacing the legacy BCL <c>Random</c> whose stream is not contractual.
    /// </summary>
    private static DamraeOracle RunDamraeOracle()
    {
        var pgaFrequency = new EmpiricalDistribution(DamraePgaLevels, DamraePgaExceedance,
            SortOrder.Ascending, SortOrder.Descending)
        { ProbabilityTransform = Transform.Logarithmic };
        var poolDuration = new EmpiricalDistribution(DamraePoolLevels, DamraePoolExceedance,
            SortOrder.Ascending, SortOrder.Descending)
        { ProbabilityTransform = Transform.NormalZ };
        var deformation = new Bilinear(DeformationPgaAxis, DeformationPoolAxis, DeformationGrid);
        var warningTime = new Bilinear(TimeDeformationAxis, TimePoolAxis, TimeGrid);
        var srpSurface = new Bilinear(SrpTimeAxis, SrpPoolAxis, SrpGrid);
        var lifeLoss = new Bilinear(LifeLossTimeAxis, LifeLossPoolAxis, DamraeLifeLossGrid);

        var stream = new MersenneTwister(OracleSeed);
        double mean = 0d, m2 = 0d;
        int failures = 0;
        for (int i = 0; i < OracleRealizations; i++)
        {
            double pga = pgaFrequency.InverseCDF(stream.NextDouble());
            double pool = poolDuration.InverseCDF(stream.NextDouble());
            double def = deformation.Interpolate(pga, pool);
            double time = warningTime.Interpolate(def, pool);
            double srp = srpSurface.Interpolate(time, pool);
            double rnd = stream.NextDouble();
            double loss = 0d;
            if (rnd <= srp)
            {
                failures++;
                loss = lifeLoss.Interpolate(time, pool);
            }
            double delta = loss - mean;
            mean += delta / (i + 1);
            m2 += delta * (loss - mean);
        }
        double sigma = Math.Sqrt(m2 / OracleRealizations);
        return new DamraeOracle(mean, sigma / Math.Sqrt(OracleRealizations), failures / (double)OracleRealizations, failures);
    }

    #endregion

    /// <summary>
    /// The re-anchored PFM-08 chained-bilinear oracle versus the engine: four bilinear
    /// surfaces chained through two bivariate transforms, the joint response, and the
    /// bivariate consequence, integrated by the conditional-bin engine at 1000 and 20 bins
    /// against the Monte Carlo oracle at k·SE per output. The pool marginal's trimmed
    /// normal-Z tail (z ≤ 4.26) and the linear-space SRP surface keep this fixture far from
    /// the seismic fixture's tail-concentration regime, so the discretization allowances are
    /// the measured per-fixture figures recorded with the run of record.
    /// </summary>
    [TestMethod]
    public void Test_Damrae_ChainedBilinear()
    {
        // Arrange — the re-anchored oracle.
        var oracle = RunDamraeOracle();

        // Act — the engine at near-exact and default bins.
        var engine1000 = RunEngine(1000);
        var engine20 = RunEngine(20);

        // Assert — the adaptive engine against the oracle at K·SE plus the 2.5% historical
        // allowance kept as an upper bound: the fixed grid's run of record carried a
        // deterministic +1.6% EAD / +1.2% failure-probability overshoot at a thousand bins
        // (the pool marginal's normal-Z tail); the adaptive probit interior removes that
        // mechanism, so the band is now generous rather than tight.
        double afpSe = Math.Sqrt(oracle.Afp * (1d - oracle.Afp) / OracleRealizations);
        Assert.AreEqual(oracle.Ead, engine1000.Ead, K * oracle.EadSe + 0.025d * engine1000.Ead,
            "Thousand-bin-budget EAD vs the re-anchored oracle.");
        Assert.AreEqual(oracle.Afp, engine1000.Afp, K * afpSe + 0.025d * engine1000.Afp,
            "Thousand-bin-budget failure probability vs the re-anchored oracle.");

        // The budget-consistency pin, re-anchored under the two-dimensional adaptive interior
        // ruling (2026-09-05): the historical fixed-grid overshoot regime (failure
        // probability 1.460, EAD 1.643 at the run of record) collapses to unity.
        double afpRatio = engine20.Afp / engine1000.Afp;
        double eadRatio = engine20.Ead / engine1000.Ead;
        Assert.IsTrue(Math.Abs(afpRatio - 1d) < 0.01d,
            $"The default-budget failure probability must agree with the thousand-bin budget (ratio {afpRatio:G6}).");
        Assert.IsTrue(Math.Abs(eadRatio - 1d) < 0.01d,
            $"The default-budget EAD must agree with the thousand-bin budget (ratio {eadRatio:G6}).");

        Console.WriteLine($"damrae: oracle EAD {oracle.Ead:G8} (se {oracle.EadSe:G4}) AFP {oracle.Afp:G8} " +
            $"({oracle.Failures} failures); engine(1000) EAD {engine1000.Ead:G8} AFP {engine1000.Afp:G8}; " +
            $"engine(20) ratios EAD {eadRatio:G4} AFP {afpRatio:G4}");

        // Runs the DAMRAE engine scenario at the given bin count.
        static (double Ead, double Afp) RunEngine(int bins)
        {
            var analysis = new RiskAnalysis(new[] { DamraeComponent(bins) });
            analysis.RunAsync().GetAwaiter().GetResult();
            Assert.IsTrue(analysis.IsEstimated, "The DAMRAE engine run must estimate.");
            var summary = analysis.RiskResults![0]!;
            return (summary.Fail.Mean, summary.Fail.TotalProbability);
        }
    }

    /// <summary>
    /// The v1.0 arrangement of the legacy scenario, measured against the same oracle: the
    /// STAGE curve drives the component as the univariate hazard, and PGA is marginalized out
    /// through the collapse mode's automatically derived Voronoi weights (the transposed 6×4
    /// surface with <c>EstimateWeights(PgaHazard)</c>). The consequence reads stage, which is
    /// now the driving signal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the arrangement question the bivariate work has to answer honestly: the two
    /// axes are not interchangeable, because the engine treats them asymmetrically. The
    /// primary axis is integrated by the adaptive quadrature to its own tolerance, while the
    /// secondary axis is discretized — by conditional bins in joint mode, or by the four
    /// weighted PGA levels here. Putting STAGE on the primary axis therefore spends the exact
    /// integration where the integrand is hardest (the normal-Z tail the convergence study
    /// showed dominates the discretization error) and spends the coarse rule on PGA, where
    /// the frequency curve puts ~99.9% of its mass below the surface's first level 0.2 g, in
    /// the clamped region a single weighted point represents almost exactly.
    /// </para>
    /// <para>
    /// The exact reference is the same double integral both arrangements estimate, evaluated
    /// by Fubini in the opposite order: the closed-form stage marginalization at each PGA
    /// level (life-loss-weighted for the risk axis) integrated densely over the PGA curve.
    /// Its unit-consequence identity against the unweighted closed form is asserted first, so
    /// the weighted reference cannot silently drift.
    /// </para>
    /// </remarks>
    [TestMethod]
    public void Test_V1Arrangement_StagePrimaryPgaCollapsed()
    {
        // Arrange — the exact references (Fubini in the opposite order).
        double afpReference = BivariateOracleFixtures.DensePgaExpectation(
            BivariateOracleFixtures.ExactMarginalizedSurface, 1 << 17);
        double unitCheck = BivariateOracleFixtures.ExactMarginalizedSurfaceWeighted(0.8d, _ => 1d);
        Assert.AreEqual(BivariateOracleFixtures.ExactMarginalizedSurface(0.8d), unitCheck, 1e-12,
            "The weighted closed form must reproduce the unweighted one at a unit consequence.");
        double eadReference = BivariateOracleFixtures.DensePgaExpectation(
            x => BivariateOracleFixtures.ExactMarginalizedSurfaceWeighted(x, BivariateOracleFixtures.LifeLossAt), 1 << 17);

        // The v1.0 component: stage drives, PGA collapses through derived weights.
        var response = BivariateOracleFixtures.TransposedSurfaceResponse();
        response.EstimateWeights(BivariateOracleFixtures.PgaHazard());
        var component = new SystemComponent { Name = "Seismic Reach (stage-driven)" };
        component.HazardFunction = BivariateOracleFixtures.StageHazard();
        component.AddFailureMode(new FailureMode(null, null, response, BivariateOracleFixtures.LifeLossConsequence()));
        var analysis = new RiskAnalysis(new[] { component });

        // Act — the v1.0 arrangement, the default-bin bivariate arrangement of the SAME
        // scenario, and the legacy oracle.
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated, "The v1.0-arrangement run must estimate.");
        var summary = analysis.RiskResults![0]!;
        var bivariate20 = RunSeismicEngine(20);
        var oracle = RunSeismicOracle();

        double afpDistance = Math.Abs(summary.Fail.TotalProbability - afpReference) / afpReference;
        double eadDistance = Math.Abs(summary.Fail.Mean - eadReference) / eadReference;
        double bivariateAfpDistance = Math.Abs(bivariate20.Afp - afpReference) / afpReference;
        double bivariateEadDistance = Math.Abs(bivariate20.Ead - eadReference) / eadReference;
        double afpSe = Math.Sqrt(oracle.Afp * (1d - oracle.Afp) / OracleRealizations);

        // Assert — accuracy against the exact references. Run of record: 0.241% on BOTH axes
        // (the same figure twice because the arrangement's only approximation, the four-point
        // PGA collapse, scales the whole stage-conditional integrand almost uniformly). The
        // pin carries ≈ 2× head-room.
        Assert.IsTrue(afpDistance < 0.005d,
            $"The v1.0 arrangement's failure probability sits {afpDistance:P3} from the exact reference.");
        Assert.IsTrue(eadDistance < 0.005d,
            $"The v1.0 arrangement's EAD sits {eadDistance:P3} from the exact reference.");

        // The arrangement question, re-anchored under the two-dimensional adaptive interior
        // ruling (2026-09-05): under the fixed conditional grid the default-bin bivariate
        // arrangement sat 35.4% from the exact reference and the collapse arrangement's
        // two-order superiority was the practitioner rule; the adaptive probit interior
        // resolves the tail-concentrated conditional axis, so BOTH arrangements now land
        // within the same half-percent band and the axis choice is no longer
        // accuracy-critical for this scenario class (the historical measurement stays in
        // docs/verification/bivariate-risk.md).
        Assert.IsTrue(bivariateAfpDistance < 0.005d,
            $"The bivariate arrangement's failure probability sits {bivariateAfpDistance:P3} from the exact reference under the adaptive interior.");
        Assert.IsTrue(bivariateEadDistance < 0.005d,
            $"The bivariate arrangement's EAD sits {bivariateEadDistance:P3} from the exact reference under the adaptive interior.");

        // And against the oracle at k·SE plus the measured discretization distance. The EAD
        // comparison is oracle-noise-limited, not engine-limited: only 298 of the 1,000,000
        // realizations fail, so the oracle's own EAD standard error is ≈ 6.4% relative and
        // the oracle sits 1.2σ from the exact value the engine reproduces to 0.24%.
        Assert.AreEqual(oracle.Afp, summary.Fail.TotalProbability, K * afpSe + 0.005d * afpReference,
            "The v1.0 arrangement's failure probability vs the legacy oracle.");
        Assert.AreEqual(oracle.Ead, summary.Fail.Mean, K * oracle.EadSe + 0.005d * eadReference,
            "The v1.0 arrangement's EAD vs the legacy oracle.");

        // The derived weights are the PGA marginal's Voronoi masses and sum to one exactly.
        // Run of record {0.999593, 2.90e-4, 7.49e-5, 4.21e-5}: the frequency curve puts
        // 99.96% of its mass on the first surface level, which is why four points suffice.
        double weightSum = 0d;
        foreach (var level in response.SecondaryHazardLevels) weightSum += level.Weight;
        Assert.AreEqual(1d, weightSum, 1e-12, "The derived PGA weights must sum to one.");
        Assert.IsTrue(response.SecondaryHazardLevels[0].Weight > 0.999d,
            "The PGA collapse's first level must carry the frequency curve's clamped mass.");

        Console.WriteLine($"v1.0 arrangement (stage primary, PGA collapsed): AFP {summary.Fail.TotalProbability:G8} " +
            $"({afpDistance:P3} from exact), EAD {summary.Fail.Mean:G8} ({eadDistance:P3} from exact); " +
            $"default-bin bivariate: AFP {bivariate20.Afp:G8} ({bivariateAfpDistance:P3}), EAD {bivariate20.Ead:G8} ({bivariateEadDistance:P3}); " +
            $"oracle AFP {oracle.Afp:G8} (SE {afpSe:G4}), EAD {oracle.Ead:G8} (SE {oracle.EadSe:G4})");
    }

    #region Collapse-versus-joint support

    /// <summary>
    /// Returns an interpolant-preserving refinement of a seismic surface response: midpoints
    /// inserted on both axes with the new rows and columns log₁₀-interpolated, so the refined
    /// grid samples the SAME piecewise log-bilinear surface at twice the density. The
    /// secondary weights are re-derived from the stage hazard by the explicit
    /// <see cref="BivariateResponse.EstimateWeights(RMC.TotalRisk.Core.Interfaces.IHazardFunction)"/>.
    /// </summary>
    /// <param name="source">The surface to refine.</param>
    private static BivariateResponse RefineSurface(BivariateResponse source)
    {
        var primary = source.PrimaryHazardLevels.ToArray();
        var secondary = source.SecondaryHazardLevels.Select(l => l.Level).ToArray();
        var grid = source.ProbabilityValues;

        var refinedPrimary = InsertMidpoints(primary);
        var refinedSecondary = InsertMidpoints(secondary);
        var refined = new double[refinedPrimary.Length, refinedSecondary.Length];
        for (int i = 0; i < refinedPrimary.Length; i++)
        {
            for (int j = 0; j < refinedSecondary.Length; j++)
            {
                double rowLow = LogAt(i / 2, j);
                double value = (i & 1) == 0 ? rowLow : (rowLow + LogAt(i / 2 + 1, j)) / 2d;
                refined[i, j] = Math.Pow(10d, value);
            }
        }

        var response = new BivariateResponse
        {
            Name = source.Name,
            SpecifiedHazard = source.SpecifiedHazard,
            HazardUnit = source.HazardUnit,
            SecondarySpecifiedHazard = source.SecondarySpecifiedHazard,
            SecondaryHazardUnit = source.SecondaryHazardUnit,
            HazardTransform = source.HazardTransform,
            SecondaryHazardTransform = source.SecondaryHazardTransform,
            ProbabilityTransform = source.ProbabilityTransform,
        };
        response.PrimaryHazardLevels.Clear();
        foreach (double level in refinedPrimary) response.PrimaryHazardLevels.Add(level);
        response.SecondaryHazardLevels.Clear();
        foreach (double level in refinedSecondary)
        {
            response.SecondaryHazardLevels.Add(new WeightedHazardLevel { Level = level, Weight = 1d / refinedSecondary.Length });
        }
        response.ProbabilityValues = refined;
        return response;

        // Interpolates the source grid's log10 value at (source row, refined column).
        double LogAt(int sourceRow, int refinedColumn)
        {
            double columnLow = Math.Log10(grid[sourceRow, refinedColumn / 2]);
            if ((refinedColumn & 1) == 0) return columnLow;
            return (columnLow + Math.Log10(grid[sourceRow, refinedColumn / 2 + 1])) / 2d;
        }

        // Returns the axis with arithmetic midpoints inserted.
        static double[] InsertMidpoints(double[] axis)
        {
            var result = new double[axis.Length * 2 - 1];
            for (int i = 0; i < axis.Length - 1; i++)
            {
                result[2 * i] = axis[i];
                result[2 * i + 1] = (axis[i] + axis[i + 1]) / 2d;
            }
            result[result.Length - 1] = axis[axis.Length - 1];
            return result;
        }
    }

    /// <summary>
    /// Evaluates the collapse-mode system response the univariate engine computes for the
    /// given surface: the weighted collapse K_i = Σ_j P[i,j]·w_j at each primary knot,
    /// interpolated in log₁₀-probability between knots and clamped to the first/last
    /// collapse ordinate outside the primary range (the stored-sample empirical-curve
    /// semantics).
    /// </summary>
    /// <param name="response">The collapse-mode surface (weights already assigned).</param>
    /// <param name="x">The primary hazard level.</param>
    private static double ReferenceCollapseSrp(BivariateResponse response, double x)
    {
        var primary = response.PrimaryHazardLevels;
        int rows = primary.Count;
        int columns = response.SecondaryHazardLevels.Count;
        var grid = response.ProbabilityValues;

        double CollapseAt(int i)
        {
            double sum = 0d;
            for (int j = 0; j < columns; j++) sum += grid[i, j] * response.SecondaryHazardLevels[j].Weight;
            return sum;
        }

        if (x <= primary[0]) return CollapseAt(0);
        if (x >= primary[rows - 1]) return CollapseAt(rows - 1);
        int index = 0;
        while (x > primary[index + 1]) index++;
        double fraction = (x - primary[index]) / (primary[index + 1] - primary[index]);
        double low = Math.Log10(CollapseAt(index));
        double high = Math.Log10(CollapseAt(index + 1));
        return Math.Pow(10d, low + fraction * (high - low));
    }

    /// <summary>
    /// Computes the true conditional expectation of a refined surface — E_Y[P(x, Y)] with P
    /// read from the surface's own grid rather than the base fixture — exactly, through the
    /// same union-grid machinery (the refined grids sample the same interpolant, so
    /// <see cref="BivariateOracleFixtures.ExactMarginalizedSurface"/> applies to every
    /// refinement level unchanged).
    /// </summary>
    /// <param name="x">The primary hazard level.</param>
    private static double TrueMarginalizedSurface(double x) => BivariateOracleFixtures.ExactMarginalizedSurface(x);

    /// <summary>Runs a collapse-mode univariate engine analysis over the given surface.</summary>
    /// <param name="response">The collapse-mode surface (weights already assigned).</param>
    private static (double Afp, double Ead) RunCollapseEngine(BivariateResponse response)
    {
        var component = new SystemComponent { Name = "Seismic Collapse Component" };
        component.HazardFunction = BivariateOracleFixtures.PgaHazard();
        component.AddFailureMode(new FailureMode(null, null, response, BivariateOracleFixtures.PrimaryDamageConsequence()));
        var analysis = new RiskAnalysis(new[] { component });
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated, "The collapse run must estimate.");
        var summary = analysis.RiskResults![0]!;
        return (summary.Fail.TotalProbability, summary.Fail.Mean);
    }

    /// <summary>Runs the joint-mode engine analysis with the primary-bound damage.</summary>
    /// <param name="bins">The conditional-integration bin count.</param>
    private static (double Afp, double Ead) RunJointEngine(int bins)
    {
        var analysis = new RiskAnalysis(new[]
        {
            BivariateOracleFixtures.JointComponent(bins, primaryBoundConsequence: true),
        });
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated, "The joint run must estimate.");
        var summary = analysis.RiskResults![0]!;
        return (summary.Fail.TotalProbability, summary.Fail.Mean);
    }

    /// <summary>The linear primary damage evaluated with end clamps (0 → 0, 1 → 100).</summary>
    /// <param name="x">The PGA level.</param>
    private static double PrimaryDamageAt(double x) => 100d * Math.Clamp(x, 0d, 1d);

    #endregion

    /// <summary>
    /// The collapse-versus-joint consistency cross-anchor: the SAME log-bilinear surface run
    /// (a) v1.0-style — the collapse mode under the univariate PGA hazard with Voronoi
    /// weights derived from the stage hazard by the explicit estimator — and (b) new-style —
    /// under the independence bivariate hazard in joint mode — with the same primary-bound
    /// damage, so both paths estimate ∫ E_Y[P(x, Y)]·C(x) dF_X and its probability analogue.
    /// Both are compared against the exact dense reference (the union-grid conditional
    /// expectation integrated densely over the PGA curve). The collapse side runs at two
    /// refinement levels — the verbatim surface with six Voronoi cells and the twice-refined
    /// interpolant-preserving surface (13 × 21 with re-derived Voronoi weights) — and its
    /// distances must shrink under refinement. Re-anchored under the two-dimensional
    /// adaptive interior ruling (2026-09-05): the joint side integrates adaptively at BOTH
    /// bin configurations (the historical 20-versus-1000 fixed-grid refinement narrative is
    /// retired — the bin count no longer selects the production grid), so both joint runs
    /// must sit inside the study's near-exact figure and the tightened paths must agree
    /// within their combined pinned distances. The engine's collapse semantics are
    /// additionally pinned against an independent re-implementation (log-interpolated
    /// collapse knots with end clamps) at the quadrature scale, so the comparison rests on
    /// verified semantics, not assumptions — the deliberate demonstration that the preserved
    /// method and the new path estimate the same quantity.
    /// </summary>
    [TestMethod]
    public void Test_CollapseVsJoint_Consistency()
    {
        // Arrange — the exact dense references.
        double afpReference = BivariateOracleFixtures.DensePgaExpectation(TrueMarginalizedSurface, 1 << 17);
        double eadReference = BivariateOracleFixtures.DensePgaExpectation(
            x => TrueMarginalizedSurface(x) * PrimaryDamageAt(x), 1 << 17);

        // The collapse surfaces: verbatim with derived Voronoi weights, and twice-refined.
        var stageHazard = BivariateOracleFixtures.StageHazard();
        var loose = BivariateOracleFixtures.SurfaceResponse();
        loose.EstimateWeights(stageHazard);
        var tight = RefineSurface(RefineSurface(BivariateOracleFixtures.SurfaceResponse()));
        tight.EstimateWeights(stageHazard);

        // The collapse-semantics pin references.
        double looseSemantics = BivariateOracleFixtures.DensePgaExpectation(x => ReferenceCollapseSrp(loose, x), 1 << 17);
        double tightSemantics = BivariateOracleFixtures.DensePgaExpectation(x => ReferenceCollapseSrp(tight, x), 1 << 17);

        // Act — the four engine runs.
        var collapseLoose = RunCollapseEngine(loose);
        var collapseTight = RunCollapseEngine(tight);
        var jointLoose = RunJointEngine(20);
        var jointTight = RunJointEngine(1000);

        // Assert — the collapse-semantics pins: the engine's collapse-mode integration must
        // match the independent re-implementation at 2e-5 relative (measured agreement
        // ≈ 5e-6 — the adaptive quadrature and the dense Simpson reference both sit well
        // inside; a wrong clamp or interpolation-space assumption would miss by percents).
        Assert.AreEqual(looseSemantics, collapseLoose.Afp, 2e-5 * looseSemantics,
            "The engine's collapse semantics diverged from the independent re-implementation (verbatim surface).");
        Assert.AreEqual(tightSemantics, collapseTight.Afp, 2e-5 * tightSemantics,
            "The engine's collapse semantics diverged from the independent re-implementation (refined surface).");

        // The measured distances to the exact reference. Collapse-side run of record:
        // 27.3% → 1.91% under surface refinement (the pins carry head-room). Joint-side,
        // re-anchored under the 2026-09-05 adaptive interior ruling: both bin
        // configurations integrate adaptively (the bin count steers only the surrogate
        // probe grid and the fixed-slice sweep budget), so BOTH joint runs must sit inside
        // the study's near-exact figure — the historical 20-bin 35.4% regime is retired.
        double DistanceAfp(double value) => Math.Abs(value - afpReference) / afpReference;
        double DistanceEad(double value) => Math.Abs(value - eadReference) / eadReference;
        Assert.IsTrue(DistanceAfp(collapseLoose.Afp) < 0.35d, "The verbatim collapse distance left its measured regime.");
        Assert.IsTrue(DistanceAfp(collapseTight.Afp) < 0.03d, "The refined collapse distance left its measured regime.");
        Assert.IsTrue(DistanceAfp(jointLoose.Afp) < BivariateOracleFixtures.LegacySrpBins1000RelativeError,
            "The default-configuration adaptive joint distance exceeded the study's near-exact figure.");
        Assert.IsTrue(DistanceAfp(jointTight.Afp) < BivariateOracleFixtures.LegacySrpBins1000RelativeError,
            "The 1000-bin-configuration adaptive joint distance exceeded the study's near-exact figure.");

        // The convergence demonstration: the collapse side must shrink toward the reference
        // under surface refinement, and every pairing must agree within the sum of its
        // pinned distances.
        Assert.IsTrue(DistanceAfp(collapseTight.Afp) < DistanceAfp(collapseLoose.Afp),
            "Refining the collapse surface must move it toward the reference.");
        Assert.IsTrue(Math.Abs(collapseTight.Afp - jointTight.Afp)
            < (0.03d + BivariateOracleFixtures.LegacySrpBins1000RelativeError) * afpReference,
            "The tightened collapse and joint paths must agree within their combined pinned distances.");
        Assert.IsTrue(Math.Abs(collapseLoose.Afp - jointLoose.Afp)
            < (0.35d + BivariateOracleFixtures.LegacySrpBins1000RelativeError) * afpReference,
            "The verbatim collapse and default-configuration joint paths must agree within their combined pinned distances.");

        // The same structure holds on the risk (EAD) axis with the shared primary damage.
        Assert.IsTrue(DistanceEad(collapseLoose.Ead) < 0.35d, "The verbatim collapse EAD distance left its measured regime.");
        Assert.IsTrue(DistanceEad(collapseTight.Ead) < 0.03d, "The refined collapse EAD distance left its measured regime.");
        Assert.IsTrue(DistanceEad(jointLoose.Ead) < BivariateOracleFixtures.LegacySrpBins1000RelativeError,
            "The default-configuration adaptive joint EAD distance exceeded the study's near-exact figure.");
        Assert.IsTrue(DistanceEad(jointTight.Ead) < BivariateOracleFixtures.LegacySrpBins1000RelativeError,
            "The 1000-bin-configuration adaptive joint EAD distance exceeded the study's near-exact figure.");
        Assert.IsTrue(DistanceEad(collapseTight.Ead) < DistanceEad(collapseLoose.Ead),
            "Refining the collapse surface must move its EAD toward the reference.");
        Assert.IsTrue(Math.Abs(collapseTight.Ead - jointTight.Ead)
            < (0.03d + BivariateOracleFixtures.LegacySrpBins1000RelativeError) * eadReference,
            "The tightened EAD paths must agree within their combined pinned distances.");

        Console.WriteLine($"collapse-vs-joint: reference AFP {afpReference:G8} EAD {eadReference:G8}; " +
            $"collapse {DistanceAfp(collapseLoose.Afp):P2} → {DistanceAfp(collapseTight.Afp):P2}; " +
            $"joint {DistanceAfp(jointLoose.Afp):P2} → {DistanceAfp(jointTight.Afp):P2}; " +
            $"tight-vs-tight gap {Math.Abs(collapseTight.Afp - jointTight.Afp) / afpReference:P2}");
    }

}
