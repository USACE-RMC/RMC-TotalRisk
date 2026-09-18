using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using Numerics.Distributions.Copulas;
using Numerics.Mathematics.Optimization;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;
using RMC.TotalRisk.Systems.Components.Graph;

namespace RMC.TotalRisk.Verification.Analyses.Risk;

/// <summary>
/// Copula-dependence verification — a greenfield family with no legacy counterpart: the
/// conditional-bin integration of a bivariate hazard verified against exact and independently
/// re-derived targets across the copula families. Covers the independence trapezoid-exactness
/// identity, the Normal-copula analytic mean through the closed form
/// E[UV] = 1/4 + arcsin(ρ/2)/(2π), the bin-count convergence study that measures and pins the
/// discretization allowances the legacy-oracle family consumes, the Clayton analytic
/// h-function and inverse-conditional anchors, the Gumbel upper-tail orientation pin (the
/// sign-error tripwire for the (u, v) non-exceedance convention), and posterior-injected
/// marginal-uncertainty propagation compared realization for realization against a
/// hand-rolled dense integral.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Fixture design.</b> The marginals are two-knot tabular curves whose probability knots
/// sit at 1e-15 and 1 − 1e-15, so each marginal is a uniform distribution up to saturation
/// atoms of mass 1e-15 — far below every tolerance here. The response surface is a single
/// bilinear cell over the marginal supports, so with corner probabilities z₀₀, z₀₁, z₁₀, z₁₁
/// the joint failure probability reduces to
/// E[P] = m·(z₀₀ + z₁₁) + (1/2 − m)·(z₀₁ + z₁₀) with m = E[UV] under the copula — an exact
/// target whenever m is known. Under independence m = 1/4; under the Normal copula
/// m = 1/4 + arcsin(ρ/2)/(2π) (write U = Φ(s), V = Φ(t): E[Φ(s)Φ(t)] = P(A &lt; s, B &lt; t)
/// with A, B independent standard normals, and (A − s, B − t) is bivariate normal with
/// correlation ρ/2); under the Archimedean families m is re-derived densely from the
/// analytic inverse conditional alone.
/// </para>
/// <para>
/// <b>Tolerance policy.</b> Exactness identities assert at 1e-10 relative (machine-level
/// residuals of the linear algebra plus the 1e-15 saturation atoms). Trapezoid-discretized
/// comparisons assert at allowances measured by the convergence study in this family and
/// documented per assert; the study's pinned figures are the derivation source for the
/// bins = 20 allowances used by <c>BivariateRiskVerification</c>.
/// </para>
/// </remarks>
[TestClass]
public class CopulaDependenceVerification
{
    #region Fixture builders

    /// <summary>
    /// Builds a two-knot tabular hazard that is uniform on [lo, hi] up to saturation atoms of
    /// mass 1e-15 at each end (the probability knots sit at 1e-15 and 1 − 1e-15).
    /// </summary>
    /// <param name="name">The function name.</param>
    /// <param name="hazard">The hazard type label.</param>
    /// <param name="lo">The lower support bound.</param>
    /// <param name="hi">The upper support bound.</param>
    private static TabularHazard UniformHazard(string name, string hazard, double lo, double hi)
        => BivariateOracleFixtures.DeterministicHazard(name, hazard, "u",
            new[] { 1d - 1e-15, 1e-15 }, new[] { lo, hi }, Transform.None);

    /// <summary>
    /// Builds the single-cell bilinear surface over [0, 100] × [0, 100] with the given corner
    /// probabilities (rows = primary, columns = secondary; axis transforms linear, probability
    /// transform selectable — the convergence study's smooth fixture interpolates in log space).
    /// </summary>
    /// <param name="z00">The (x-low, y-low) corner probability.</param>
    /// <param name="z01">The (x-low, y-high) corner probability.</param>
    /// <param name="z10">The (x-high, y-low) corner probability.</param>
    /// <param name="z11">The (x-high, y-high) corner probability.</param>
    /// <param name="probabilityTransform">The probability interpolation transform.</param>
    private static BivariateResponse SingleCellSurface(double z00, double z01, double z10, double z11,
        Transform probabilityTransform = Transform.None)
    {
        var response = new BivariateResponse
        {
            Name = "Single-Cell Surface",
            SpecifiedHazard = "X",
            HazardUnit = "u",
            SecondarySpecifiedHazard = "Y",
            SecondaryHazardUnit = "u",
            HazardTransform = Transform.None,
            SecondaryHazardTransform = Transform.None,
            ProbabilityTransform = probabilityTransform,
        };
        response.PrimaryHazardLevels.Clear();
        response.PrimaryHazardLevels.Add(0d);
        response.PrimaryHazardLevels.Add(100d);
        response.SecondaryHazardLevels.Clear();
        response.SecondaryHazardLevels.Add(new WeightedHazardLevel { Level = 0d, Weight = 0.5d });
        response.SecondaryHazardLevels.Add(new WeightedHazardLevel { Level = 100d, Weight = 0.5d });
        response.ProbabilityValues = new[,] { { z00, z01 }, { z10, z11 } };
        return response;
    }

    /// <summary>
    /// Builds the graph-wired single-cell engine scenario: uniform marginals under the given
    /// copula, the joint single-cell surface, and a trivial primary-signal consequence.
    /// </summary>
    /// <param name="copula">The copula (null for independence).</param>
    /// <param name="bins">The conditional-integration bin count.</param>
    /// <param name="surface">The joint response surface.</param>
    /// <param name="marginalY">Optional replacement secondary marginal.</param>
    private static SystemComponent SingleCellComponent(BivariateCopula? copula, int bins,
        BivariateResponse surface, IHazardFunction? marginalY = null)
    {
        var joint = new BivariateHazard(
            UniformHazard("Uniform X", "X", 0d, 100d),
            marginalY ?? UniformHazard("Uniform Y", "Y", 0d, 100d))
        {
            Name = "Copula Hazard",
            SpecifiedHazard = "X",
            HazardUnit = "u",
            SecondarySpecifiedHazard = "Y",
            SecondaryHazardUnit = "u",
            SecondaryIntegrationBins = bins,
        };
        if (copula != null) joint.Copula = copula;
        var component = new SystemComponent(joint) { Name = "Copula Component" };
        var hazard = component.Graph.GetElements<HazardElement>().Single();
        var breach = new ResponseElement("Joint Breach")
        {
            Function = surface,
            Input = new RiskConnection(hazard),
            SecondaryInput = new RiskConnection(hazard, 1),
        };
        component.Graph.AddElement(breach);
        var loss = new ConsequenceElement("Loss") { Input = new RiskConnection(breach) };
        loss.Functions.Add(new TabularConsequence
        {
            Name = "Unit Loss",
            SpecifiedHazard = "X",
            HazardUnit = "u",
            SpecifiedConsequence = "Loss",
            ConsequenceUnit = "$",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(100d, new Deterministic(100d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        });
        component.Graph.AddElement(loss);
        return component;
    }

    /// <summary>
    /// Runs a deterministic single-component analysis and returns the annualized failure
    /// probability of its only component.
    /// </summary>
    /// <param name="component">The component to run.</param>
    /// <param name="pinTolerance">
    /// Optional pinned quadrature tolerance for the primary integration (used by the
    /// convergence study so the measured error is the conditional discretization's, not the
    /// adaptive quadrature's — documented there).
    /// </param>
    private static double RunForFailureProbability(SystemComponent component, double? pinTolerance = null)
    {
        var analysis = new RiskAnalysis(new[] { component });
        if (pinTolerance.HasValue)
        {
            analysis.Options.UseDefaults = false;
            analysis.Options.Tolerance = pinTolerance.Value;
        }
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated, "The run must estimate.");
        return analysis.RiskResults![0]!.Fail.TotalProbability;
    }

    /// <summary>
    /// Evaluates the fixed-conditional-grid interior at the component's configured bin count
    /// through the quarantined discretization instrument (its configured level IS the fixed
    /// grid at that count — the production interior is adaptive).
    /// </summary>
    /// <param name="component">The component to evaluate.</param>
    /// <param name="pinTolerance">Optional pinned primary-quadrature tolerance (the study's rationale).</param>
    private static double InstrumentFailureProbability(SystemComponent component, double? pinTolerance = null)
    {
        var analysis = new RiskAnalysis(new[] { component });
        if (pinTolerance.HasValue)
        {
            analysis.Options.UseDefaults = false;
            analysis.Options.Tolerance = pinTolerance.Value;
        }
        var diagnostic = analysis.EstimateSecondaryDiscretizationError(0);
        Assert.IsNotNull(diagnostic, "The instrument evaluation requires a diagnosable bin count.");
        return diagnostic!.FailureProbability.Value;
    }

    /// <summary>The exact single-cell mean: E[P] = m·(z₀₀ + z₁₁) + (1/2 − m)·(z₀₁ + z₁₀).</summary>
    /// <param name="m">The copula's E[UV].</param>
    /// <param name="z00">The (x-low, y-low) corner probability.</param>
    /// <param name="z01">The (x-low, y-high) corner probability.</param>
    /// <param name="z10">The (x-high, y-low) corner probability.</param>
    /// <param name="z11">The (x-high, y-high) corner probability.</param>
    private static double SingleCellMean(double m, double z00, double z01, double z10, double z11)
        => m * (z00 + z11) + (0.5d - m) * (z01 + z10);

    #endregion

    #region Analytic copula forms (independent transcriptions)

    /// <summary>The analytic Normal-copula h-function: Φ((Φ⁻¹(v) − ρ·Φ⁻¹(u)) / √(1 − ρ²)).</summary>
    /// <param name="rho">The correlation parameter.</param>
    /// <param name="u">The conditioning non-exceedance probability.</param>
    /// <param name="v">The conditioned non-exceedance probability.</param>
    private static double NormalH(double rho, double u, double v)
        => Normal.StandardCDF((Normal.StandardZ(v) - rho * Normal.StandardZ(u)) / Math.Sqrt(1d - rho * rho));

    /// <summary>The analytic Clayton h-function: u^(−θ−1)·(u^(−θ) + v^(−θ) − 1)^(−(θ+1)/θ).</summary>
    /// <param name="theta">The Clayton parameter.</param>
    /// <param name="u">The conditioning non-exceedance probability.</param>
    /// <param name="v">The conditioned non-exceedance probability.</param>
    private static double ClaytonH(double theta, double u, double v)
        => Math.Pow(u, -theta - 1d) * Math.Pow(Math.Pow(u, -theta) + Math.Pow(v, -theta) - 1d, -(theta + 1d) / theta);

    /// <summary>
    /// The analytic Clayton inverse conditional: v = (u^(−θ)·(t^(−θ/(θ+1)) − 1) + 1)^(−1/θ).
    /// </summary>
    /// <param name="theta">The Clayton parameter.</param>
    /// <param name="u">The conditioning non-exceedance probability.</param>
    /// <param name="t">The conditional probability level.</param>
    private static double ClaytonHInverse(double theta, double u, double t)
        => Math.Pow(Math.Pow(u, -theta) * (Math.Pow(t, -theta / (theta + 1d)) - 1d) + 1d, -1d / theta);

    /// <summary>The analytic Gumbel copula: C(u, v) = exp(−((−ln u)^θ + (−ln v)^θ)^(1/θ)).</summary>
    /// <param name="theta">The Gumbel parameter.</param>
    /// <param name="u">The first non-exceedance probability.</param>
    /// <param name="v">The second non-exceedance probability.</param>
    private static double GumbelC(double theta, double u, double v)
        => Math.Exp(-Math.Pow(Math.Pow(-Math.Log(u), theta) + Math.Pow(-Math.Log(v), theta), 1d / theta));

    /// <summary>
    /// The analytic Joe copula: C(u, v) = 1 − ((1−u)^θ + (1−v)^θ − (1−u)^θ·(1−v)^θ)^(1/θ).
    /// </summary>
    /// <param name="theta">The Joe parameter.</param>
    /// <param name="u">The first non-exceedance probability.</param>
    /// <param name="v">The second non-exceedance probability.</param>
    private static double JoeC(double theta, double u, double v)
    {
        double du = Math.Pow(1d - u, theta);
        double dv = Math.Pow(1d - v, theta);
        return 1d - Math.Pow(du + dv - du * dv, 1d / theta);
    }

    /// <summary>
    /// Computes E[UV] for a copula densely from its analytic CDF through the hoeffding-style
    /// identity E[UV] = ∫₀¹∫₀¹ P(U &gt; s, V &gt; t) ds dt = ∫₀¹∫₀¹ (1 − s − t + C(s, t)) ds dt
    /// = ∫₀¹∫₀¹ C(s, t) ds dt (the linear terms integrate to zero), by composite Simpson on
    /// both axes. Integrating the CDF rather than the inverse conditional keeps the integrand
    /// smooth and bounded — the inverse conditional carries endpoint cusps in t that would
    /// corrupt uniform-grid Simpson — and keeps the reference independent of both the engine
    /// and the library's copula code. Self-check: for the independence CDF C = s·t the
    /// routine must return 1/4 to machine level, and refinement doubling must move the result
    /// below the documented residual.
    /// </summary>
    /// <param name="cdf">The analytic copula CDF C(s, t).</param>
    /// <param name="intervals">The (even) Simpson interval count per axis.</param>
    private static double DenseCopulaMoment(Func<double, double, double> cdf, int intervals = 2048)
    {
        double h = 1d / intervals;
        double outer = 0d;
        for (int i = 0; i <= intervals; i++)
        {
            double s = i == 0 ? 1e-12 : i == intervals ? 1d - 1e-12 : i * h;
            double inner = 0d;
            for (int j = 0; j <= intervals; j++)
            {
                double t = j == 0 ? 1e-12 : j == intervals ? 1d - 1e-12 : j * h;
                int weight = j == 0 || j == intervals ? 1 : (j & 1) == 1 ? 4 : 2;
                inner += weight * cdf(s, t);
            }
            inner *= h / 3d;
            int outerWeight = i == 0 || i == intervals ? 1 : (i & 1) == 1 ? 4 : 2;
            outer += outerWeight * inner;
        }
        return outer * h / 3d;
    }

    #endregion

    /// <summary>
    /// Verifies the independence exactness identity: with a surface linear in the secondary
    /// and a uniform secondary marginal, the engine reproduces the iterated closed form
    /// E[P] = (z₀₀+z₀₁+z₁₀+z₁₁)/4 at 1e-10 relative at both 3 and 20 configured bin counts.
    /// Re-anchored under the two-dimensional adaptive interior ruling (2026-09-05): the
    /// production interior integrates the conditional axis adaptively in the probit
    /// coordinate with the recorded masses renormalized to exactly one per column, so the
    /// bin count no longer selects a trapezoid grid — the two configurations must agree with
    /// the closed form (and each other) at quadrature scale, which the 1e-10 band verifies
    /// directly. (The pre-ruling mechanism was the trapezoid's exactness on y-linear
    /// integrands; the identity survives because the adaptive interior resolves the smooth
    /// conditional integrand below the same band.) The primary quadrature is exact because
    /// the marginalized integrand is linear in the primary signal (G10K21 integrates
    /// polynomials far beyond degree one exactly).
    /// </summary>
    [TestMethod]
    public void Test_Independence_TrapezoidExactAtAnyN()
    {
        // Arrange — corners with a genuine bilinear cross term (0.85 ≠ 0.4 + 0.3 − 0.1).
        double exact = SingleCellMean(0.25d, 0.1d, 0.4d, 0.3d, 0.85d);

        // Act
        double afp3 = RunForFailureProbability(SingleCellComponent(null, 3, SingleCellSurface(0.1d, 0.4d, 0.3d, 0.85d)));
        double afp20 = RunForFailureProbability(SingleCellComponent(null, 20, SingleCellSurface(0.1d, 0.4d, 0.3d, 0.85d)));

        // Assert
        Assert.AreEqual(exact, afp3, 1e-10 * exact, "Independence with a y-linear surface must be exact at 3 bins.");
        Assert.AreEqual(exact, afp20, 1e-10 * exact, "Independence with a y-linear surface must be exact at 20 bins.");
        Console.WriteLine($"independence exactness: exact {exact:G17}, bins 3 {afp3:G17}, bins 20 {afp20:G17}");
    }

    /// <summary>
    /// Verifies the Normal copula two ways: the library h-function matches the analytic
    /// conditional Φ((Φ⁻¹(v) − ρΦ⁻¹(u))/√(1−ρ²)) at 1e-12 over a probability grid (the
    /// independent transcription pin), and the engine's joint failure probability at 1000 bins
    /// matches the closed-form mean through E[UV] = 1/4 + arcsin(ρ/2)/(2π) within the
    /// retained study band (measured by the convergence study on this same fixture; the
    /// assert carries head-room above the observed error — under the 2026-09-05 adaptive
    /// interior ruling the production error sits far below the historical fixed-grid figure,
    /// so the band is kept as an upper bound).
    /// </summary>
    [TestMethod]
    public void Test_NormalCopula_HFunctionAndAnalyticMean()
    {
        // Arrange
        double rho = 0.7d;
        var copula = new NormalCopula(rho);
        var grid = new[] { 0.05d, 0.2d, 0.5d, 0.8d, 0.95d };

        // Assert — the h-function transcription pin (both signs of ρ).
        foreach (double u in grid)
        {
            foreach (double v in grid)
            {
                Assert.AreEqual(NormalH(rho, u, v), copula.ConditionalCDF(u, v), 1e-12,
                    $"Normal h-function at ({u}, {v}), ρ = {rho}.");
                Assert.AreEqual(NormalH(-0.4d, u, v), new NormalCopula(-0.4d).ConditionalCDF(u, v), 1e-12,
                    $"Normal h-function at ({u}, {v}), ρ = −0.4.");
            }
        }

        // Act — the engine against the analytic mean.
        double m = 0.25d + Math.Asin(rho / 2d) / (2d * Math.PI);
        double exact = SingleCellMean(m, 0.1d, 0.4d, 0.3d, 0.85d);
        double afp = RunForFailureProbability(SingleCellComponent(new NormalCopula(rho), 1000,
            SingleCellSurface(0.1d, 0.4d, 0.3d, 0.85d)));

        // Assert — 1.5e-5 relative, the retained study band. The figure derives from the
        // historical fixed-grid series (the trapezoid's endpoint-cusp order reduction under
        // the conditional map v(t) = Φ(√(1−ρ²)·Φ⁻¹(t) + ρ·Φ⁻¹(u)) measured ≈ 3.8e-6 at 1000
        // bins); under the 2026-09-05 adaptive interior ruling the production error sits far
        // below it, so the band holds as a deliberate upper bound (the convergence study's
        // adaptive layer pins the tighter figures).
        Assert.AreEqual(exact, afp, 1.5e-5 * exact, "Normal-copula engine mean vs the analytic E[UV] closed form.");
        Console.WriteLine($"normal copula: m {m:G17}, exact {exact:G17}, engine(1000) {afp:G17}, rel err {Math.Abs(afp - exact) / exact:G4}");
    }

    /// <summary>
    /// The bin-count convergence study — the derivation source for every fixed-grid
    /// discretization allowance in the bivariate families, run over three fixtures at
    /// N ∈ {20, 100, 1000}. Under the two-dimensional adaptive interior ruling (2026-09-05)
    /// the production mean run no longer integrates the fixed conditional trapezoid, so the
    /// fixed series is reached through the quarantined discretization instrument
    /// (<c>EstimateSecondaryDiscretizationError</c>'s configured level IS the fixed grid at
    /// the configured count) while the production series measures the adaptive interior at
    /// the same configurations.
    /// (i) The smooth-fixture O(N⁻²) assert: independence with a log-interpolated surface
    /// whose corner exponents are additively separable, making the conditional integrand a
    /// pure exponential in t (C^∞ to the endpoints) and the exact mean a product of
    /// one-dimensional closed forms; the 5× and 10× refinements must land near the
    /// theoretical 25× and 100× error reductions (the primary quadrature is pinned at 1e-10,
    /// three decades below the smallest measured error, so the measured error is the
    /// conditional discretization's alone — the pin rationale required by the ensemble
    /// discipline note). (ii) The Normal-copula endpoint-cusp series: under dependence the
    /// conditional map v(t) has one-sided endpoint cusps (dv/dt ~ t^{−ρ²} as t → 0), so the
    /// decay order drops to ≈ O(N^{−(2−ρ²)}); the series is measured and documented, with the
    /// monotone-decrease assert and a generous ratio floor. (iii) The legacy seismic SRP
    /// probe against the exact union-grid closed form — the measured series
    /// {≈ 0.353, ≈ 0.0491, ≈ 1.98e-3} relative shows the uniform-t trapezoid rate-limited by
    /// the marginal's normal-Z tail (P ~ e^{c·Φ⁻¹(t)} concentrates the integrand in the top
    /// bins before saturation flattens them), so the default 20 bins are INADEQUATE for
    /// tail-concentrated log-scale surfaces; the measured errors are pinned as the documented
    /// adequacy figures consumed by <c>BivariateRiskVerification</c>. The closed form itself
    /// is cross-checked against a dense Simpson integral over the engine's own
    /// empirical-distribution semantics before any engine comparison.
    /// (iv) The adaptive adequacy pins: at every configuration the production interior must
    /// resolve all three fixtures to quadrature scale — including the tail-concentrated
    /// legacy class the fixed default could not — beating the fixed default by well over two
    /// orders on the motivating fixture. All measurements complete before any assert so a
    /// single band failure cannot hide the remaining series.
    /// </summary>
    [TestMethod]
    public void Test_BinConvergence_OrderNSquaredAndDefaultAdequacy()
    {
        int[] binCounts = { 20, 100, 1000 };

        // Arrange — fixture (i): the separable log surface. log₁₀ corners {−4, −3, −2, −1}
        // give log₁₀P = −4 + 2u + v on the unit square, so the exact mean is
        // 10⁻⁴ · (10² − 1)/(2·ln10) · (10 − 1)/ln10.
        double ln10 = Math.Log(10d);
        double smoothExact = 1e-4d * ((100d - 1d) / (2d * ln10)) * ((10d - 1d) / ln10);

        // Fixture (ii): the Normal-copula cusp series target.
        double rho = 0.7d;
        double m = 0.25d + Math.Asin(rho / 2d) / (2d * Math.PI);
        double cuspExact = SingleCellMean(m, 0.1d, 0.4d, 0.3d, 0.85d);

        // Fixture (iii): the legacy SRP probe's exact target, cross-checked against the dense
        // empirical-distribution reference before use.
        double legacyExact = BivariateOracleFixtures.ExactMarginalizedSurface(0.8d);
        double legacyDense = BivariateOracleFixtures.DenseMarginalizedSurface(0.8d);

        // Act — the fixed-grid series through the quarantined instrument (the production
        // interior is adaptive under the 2026-09-05 ruling, so the study's subject — the
        // fixed conditional trapezoid — is reached through the discretization diagnostic's
        // configured level), and the adaptive production series at the same configurations.
        var smoothErrors = new double[3];
        var cuspErrors = new double[3];
        var legacyErrors = new double[3];
        var smoothAdaptive = new double[3];
        var cuspAdaptive = new double[3];
        var legacyAdaptive = new double[3];
        for (int i = 0; i < binCounts.Length; i++)
        {
            double smooth = InstrumentFailureProbability(SingleCellComponent(null, binCounts[i],
                SingleCellSurface(1e-4d, 1e-3d, 1e-2d, 1e-1d, Transform.Logarithmic)), pinTolerance: 1e-10);
            smoothErrors[i] = Math.Abs(smooth - smoothExact) / smoothExact;
            double smoothProduction = RunForFailureProbability(SingleCellComponent(null, binCounts[i],
                SingleCellSurface(1e-4d, 1e-3d, 1e-2d, 1e-1d, Transform.Logarithmic)), pinTolerance: 1e-10);
            smoothAdaptive[i] = Math.Abs(smoothProduction - smoothExact) / smoothExact;

            double cusp = InstrumentFailureProbability(SingleCellComponent(new NormalCopula(rho), binCounts[i],
                SingleCellSurface(0.1d, 0.4d, 0.3d, 0.85d)), pinTolerance: 1e-10);
            cuspErrors[i] = Math.Abs(cusp - cuspExact) / cuspExact;
            double cuspProduction = RunForFailureProbability(SingleCellComponent(new NormalCopula(rho), binCounts[i],
                SingleCellSurface(0.1d, 0.4d, 0.3d, 0.85d)), pinTolerance: 1e-10);
            cuspAdaptive[i] = Math.Abs(cuspProduction - cuspExact) / cuspExact;

            double legacy = InstrumentFailureProbability(
                BivariateOracleFixtures.JointComponent(binCounts[i], BivariateOracleFixtures.DegeneratePrimary(0.8d)));
            legacyErrors[i] = Math.Abs(legacy - legacyExact) / legacyExact;
            double legacyProduction = RunForFailureProbability(
                BivariateOracleFixtures.JointComponent(binCounts[i], BivariateOracleFixtures.DegeneratePrimary(0.8d)));
            legacyAdaptive[i] = Math.Abs(legacyProduction - legacyExact) / legacyExact;
            Console.WriteLine($"bins {binCounts[i]}: fixed smooth {smoothErrors[i]:G6}, cusp {cuspErrors[i]:G6}, legacy {legacyErrors[i]:G6}; " +
                $"adaptive smooth {smoothAdaptive[i]:G6}, cusp {cuspAdaptive[i]:G6}, legacy {legacyAdaptive[i]:G6}");
        }

        // Assert — the closed-form self-check: 2e-8 relative. The two references are
        // independent integration styles of the same fixture — the union-grid partial
        // expectations are exact per segment, while the uniform-grid Simpson reference
        // carries slope-discontinuity residuals at the union-grid knots (measured agreement
        // ≈ 5e-9 relative at 2²¹ intervals).
        Assert.AreEqual(legacyExact, legacyDense, 2e-8 * legacyExact,
            "The union-grid closed form must agree with the dense empirical-distribution reference.");

        // The smooth fixture's O(N⁻²) decay: the trapezoid error of ∫e^{kt}dt is
        // (k²/12)·N⁻² + O(N⁻⁴), so the refinement ratios sit at 25 and 100 up to the N⁻⁴
        // term and the quadrature floor (bands ±40%).
        Assert.IsTrue(smoothErrors[0] > smoothErrors[1] && smoothErrors[1] > smoothErrors[2],
            "The smooth-fixture discretization error must decrease with bins.");
        double firstRatio = smoothErrors[0] / smoothErrors[1];
        double secondRatio = smoothErrors[1] / smoothErrors[2];
        Assert.IsTrue(firstRatio > 15d && firstRatio < 35d,
            $"smooth 20 → 100 bins error ratio {firstRatio:G4} must sit near the O(N⁻²) factor 25.");
        Assert.IsTrue(secondRatio > 60d && secondRatio < 140d,
            $"smooth 100 → 1000 bins error ratio {secondRatio:G4} must sit near the O(N⁻²) factor 100.");

        // The Normal-copula cusp series: measured ≈ {6.9e-4, 8.2e-5, 3.8e-6} — slower than
        // N⁻² by the endpoint-cusp order reduction (observed refinement ratios ≈ 8.4 and 22
        // against the smooth fixture's 25 and 100); monotone with a generous total-reduction
        // floor (50× refinement must gain well over 10×).
        Assert.IsTrue(cuspErrors[0] > cuspErrors[1] && cuspErrors[1] > cuspErrors[2],
            "The cusp-series discretization error must decrease with bins.");
        Assert.IsTrue(cuspErrors[2] < cuspErrors[0] / 10d,
            "The 50× bin refinement must reduce the cusp-series error by well over 10×.");

        // The pinned legacy adequacy figures (run of record in
        // docs/verification/copula-dependence.md): measured ≈ {0.353, 0.0491, 1.98e-3}
        // relative — the pins carry head-room over the measured errors and are the
        // derivation source for the legacy-oracle family's discretization allowances.
        Assert.IsTrue(legacyErrors[0] < BivariateOracleFixtures.LegacySrpBins20RelativeError,
            $"bins = 20 error {legacyErrors[0]:G4} must sit under the pinned adequacy figure.");
        Assert.IsTrue(legacyErrors[2] < BivariateOracleFixtures.LegacySrpBins1000RelativeError,
            $"bins = 1000 error {legacyErrors[2]:G4} must sit under the pinned near-exact figure.");
        Assert.IsTrue(legacyErrors[0] > legacyErrors[1] && legacyErrors[1] > legacyErrors[2],
            "The legacy-fixture discretization error must decrease with bins.");
        Assert.IsTrue(legacyErrors[2] < legacyErrors[0] / 50d,
            "The 50× bin refinement must reduce the legacy-fixture error by well over 50×.");

        // The adaptive adequacy pins (the 2026-09-05 two-dimensional interior ruling): the
        // probit conditional coordinate resolves every fixture — the tail-concentrated legacy
        // class included — to far below the fixed default's error at EVERY configured budget,
        // and the default-budget adaptive answer beats the fixed default by well over two
        // orders on the fixture that motivated the ruling (measured ≈ 3.7e-6 against the fixed
        // grid's ≈ 0.353).
        for (int i = 0; i < binCounts.Length; i++)
        {
            Assert.IsTrue(smoothAdaptive[i] < 1e-5,
                $"Adaptive smooth error {smoothAdaptive[i]:G4} at bins {binCounts[i]} must sit at quadrature scale.");
            Assert.IsTrue(cuspAdaptive[i] < 1e-4,
                $"Adaptive cusp error {cuspAdaptive[i]:G4} at bins {binCounts[i]} must sit at quadrature scale.");
            Assert.IsTrue(legacyAdaptive[i] < 1e-4,
                $"Adaptive legacy error {legacyAdaptive[i]:G4} at bins {binCounts[i]} must sit far below the fixed grid's.");
        }
        Assert.IsTrue(legacyAdaptive[0] < legacyErrors[0] / 100d,
            $"The default-budget adaptive error {legacyAdaptive[0]:G4} must beat the fixed default {legacyErrors[0]:G4} by well over two orders.");
    }

    /// <summary>
    /// Verifies the Clayton copula three ways: the library h-function matches the analytic
    /// u^(−θ−1)·(u^(−θ)+v^(−θ)−1)^(−(θ+1)/θ) at 1e-12; the library inverse conditional
    /// matches the analytic inverse and closes the round trip at 1e-12; and the engine at
    /// 1000 bins matches the single-cell mean whose E[UV] is re-derived densely from the
    /// analytic Clayton CDF alone through E[UV] = ∫∫C (independent of both the engine and
    /// the library's copula code, self-checked by the independence identity and refinement
    /// doubling). The engine tolerance derivation sits at the assert.
    /// </summary>
    [TestMethod]
    public void Test_Clayton_AnalyticConditionals()
    {
        // Arrange
        double theta = 2d;
        var copula = new ClaytonCopula(theta);
        var grid = new[] { 0.05d, 0.2d, 0.5d, 0.8d, 0.95d };

        // Assert — the analytic transcription pins and the round trip.
        foreach (double u in grid)
        {
            foreach (double t in grid)
            {
                Assert.AreEqual(ClaytonH(theta, u, t), copula.ConditionalCDF(u, t), 1e-12,
                    $"Clayton h-function at ({u}, {t}).");
                Assert.AreEqual(ClaytonHInverse(theta, u, t), copula.InverseConditionalCDF(u, t), 1e-12,
                    $"Clayton inverse conditional at ({u}, {t}).");
                Assert.AreEqual(t, copula.ConditionalCDF(u, copula.InverseConditionalCDF(u, t)), 1e-12,
                    $"Clayton conditional round trip at ({u}, {t}).");
            }
        }

        // Act — the engine against the densely re-derived mean. The moment integrator is
        // self-checked against the independence identity (∫∫ s·t = 1/4) and by refinement
        // doubling before the Clayton value is trusted.
        Assert.AreEqual(0.25d, DenseCopulaMoment((s, t) => s * t), 1e-10,
            "The dense moment integrator must reproduce the independence E[UV] = 1/4.");
        double mCoarse = DenseCopulaMoment((s, t) => Math.Pow(Math.Pow(s, -theta) + Math.Pow(t, -theta) - 1d, -1d / theta));
        double mClayton = DenseCopulaMoment(
            (s, t) => Math.Pow(Math.Pow(s, -theta) + Math.Pow(t, -theta) - 1d, -1d / theta), intervals: 4096);
        Assert.IsTrue(Math.Abs(mClayton - mCoarse) < 1e-8,
            $"Refinement doubling must confirm the dense Clayton moment ({mCoarse:G12} vs {mClayton:G12}).");
        double exact = SingleCellMean(mClayton, 0.1d, 0.4d, 0.3d, 0.85d);
        double afp = RunForFailureProbability(SingleCellComponent(new ClaytonCopula(theta), 1000,
            SingleCellSurface(0.1d, 0.4d, 0.3d, 0.85d)));

        // Assert — 4e-5 relative, the retained study band: the figure derives from the
        // historical fixed-grid series (Clayton's lower-tail v ~ t^{1/(θ+1)} endpoint cusp
        // measured ≈ 1.0e-5 at 1000 bins with ≈ 4× head-room); under the 2026-09-05 adaptive
        // interior ruling the production error sits far below it, so the band holds as a
        // deliberate upper bound, with the dense-moment residual (< 1e-8 by the refinement
        // check) folded in.
        Assert.AreEqual(exact, afp, 4e-5 * exact, "Clayton engine mean vs the dense analytic-CDF re-derivation.");
        Console.WriteLine($"clayton: m {mClayton:G17}, exact {exact:G17}, engine(1000) {afp:G17}, rel err {Math.Abs(afp - exact) / exact:G4}");
    }

    /// <summary>
    /// The upper-tail orientation pin — the sign-error tripwire for the (u, v) non-exceedance
    /// convention. With a response loading ONLY the joint-extreme corner (z₁₁ = 0.9, all other
    /// corners zero), the Gumbel copula's upper-tail dependence must STRICTLY raise the joint
    /// failure probability over independence (analytically ≈ 1.23× at θ = 2 via Spearman's
    /// rho); an exceedance-convention flip would send the dependence to the lower tail and
    /// reverse the inequality. The library h-function is additionally pinned against a central
    /// finite difference of the independently transcribed Gumbel CDF (ε = 1e-6; the
    /// second-order truncation and cancellation residuals sit near 1e-10, asserted at 1e-8).
    /// </summary>
    [TestMethod]
    public void Test_Gumbel_UpperTailOrientation()
    {
        // Arrange
        double theta = 2d;
        var copula = new GumbelCopula(theta);
        var grid = new[] { 0.1d, 0.3d, 0.5d, 0.7d, 0.9d };

        // Assert — the finite-difference h-function pin.
        const double epsilon = 1e-6;
        foreach (double u in grid)
        {
            foreach (double v in grid)
            {
                double finiteDifference = (GumbelC(theta, u + epsilon, v) - GumbelC(theta, u - epsilon, v)) / (2d * epsilon);
                Assert.AreEqual(finiteDifference, copula.ConditionalCDF(u, v), 1e-8,
                    $"Gumbel h-function vs the central finite difference at ({u}, {v}).");
            }
        }

        // Act — the joint-extreme response under independence and under Gumbel.
        double independenceAfp = RunForFailureProbability(SingleCellComponent(null, 1000,
            SingleCellSurface(0d, 0d, 0d, 0.9d)));
        double gumbelAfp = RunForFailureProbability(SingleCellComponent(new GumbelCopula(theta), 1000,
            SingleCellSurface(0d, 0d, 0d, 0.9d)));

        // Assert — the orientation inequality with margin (the analytic ratio is ≈ 1.23).
        Assert.IsTrue(gumbelAfp > 1.15d * independenceAfp,
            $"Gumbel upper-tail dependence must raise the joint-extreme failure probability: independence {independenceAfp:G6}, Gumbel {gumbelAfp:G6}.");
        Assert.AreEqual(0.9d * 0.25d, independenceAfp, 1e-9,
            "The independence baseline is the exact corner mean 0.9·E[UV] = 0.225.");
        Console.WriteLine($"gumbel orientation: independence {independenceAfp:G10}, gumbel {gumbelAfp:G10}, ratio {gumbelAfp / independenceAfp:G6}");
    }

    /// <summary>
    /// The extreme-dependence boundary robustness probe for the two copula families whose
    /// conditional inverse is solved numerically (Gumbel and Joe — Brent over the conditional
    /// level). The adaptive conditional interior subdivides toward the probit-domain edges,
    /// querying the inverse at conditional levels down to the 1e-16 clamp and up to
    /// 1 − 1e-16 — the saturation band where the unguarded solver historically threw "root is
    /// not bracketed" — so a full engine run at θ = 8 (near-comonotone; Kendall's τ = 7/8 for
    /// Gumbel) through each family is the crash-class probe: it must complete AND land on the
    /// independently re-derived closed form. Oracles: E[UV] densely from the analytic CDFs
    /// alone (the Simpson moment integrator, self-checked by refinement doubling — at θ = 8
    /// the CDF approaches min(s, t), whose diagonal boundary layer of width ≈ 1/θ the
    /// 4096-interval grid resolves comfortably), then the corner mean 0.9·E[UV]. Tolerances
    /// measured-then-pinned: the adaptive interior lands ≈ 4.6e-12 (Gumbel) and ≈ 3.3e-12
    /// (Joe) relative on this fixture with refinement-doubling residuals ≤ 1e-11, so the
    /// asserts pin 1e-9 relative — over two orders of head-room — with the ordering
    /// inequality (both far above independence) closing the orientation.
    /// </summary>
    [TestMethod]
    public void Test_GumbelJoe_ExtremeDependenceAdaptiveRobustness()
    {
        // Arrange — the near-comonotone parameter and the joint-extreme corner surface.
        double theta = 8d;
        double independenceExact = 0.9d * 0.25d;

        // The dense E[UV] oracles with refinement self-checks.
        double gumbelCoarse = DenseCopulaMoment((s, t) => GumbelC(theta, s, t));
        double gumbelMoment = DenseCopulaMoment((s, t) => GumbelC(theta, s, t), intervals: 4096);
        Assert.IsTrue(Math.Abs(gumbelMoment - gumbelCoarse) < 1e-9,
            $"Refinement doubling must confirm the dense Gumbel moment ({gumbelCoarse:G12} vs {gumbelMoment:G12}).");
        double joeCoarse = DenseCopulaMoment((s, t) => JoeC(theta, s, t));
        double joeMoment = DenseCopulaMoment((s, t) => JoeC(theta, s, t), intervals: 4096);
        Assert.IsTrue(Math.Abs(joeMoment - joeCoarse) < 1e-9,
            $"Refinement doubling must confirm the dense Joe moment ({joeCoarse:G12} vs {joeMoment:G12}).");
        double gumbelExact = SingleCellMean(gumbelMoment, 0d, 0d, 0d, 0.9d);
        double joeExact = SingleCellMean(joeMoment, 0d, 0d, 0d, 0.9d);

        // Act — full engine runs through both numerically inverted families at default bins.
        double gumbelAfp = RunForFailureProbability(SingleCellComponent(new GumbelCopula(theta), 20,
            SingleCellSurface(0d, 0d, 0d, 0.9d)));
        double joeAfp = RunForFailureProbability(SingleCellComponent(new JoeCopula(theta), 20,
            SingleCellSurface(0d, 0d, 0d, 0.9d)));

        // Assert — measured ≈ 4.6e-12 (Gumbel) and ≈ 3.3e-12 (Joe) relative; pinned at 1e-9
        // with over two orders of head-room (the dense oracle's own residual and the engine's
        // adaptive interior agree at the same scale).
        Assert.AreEqual(gumbelExact, gumbelAfp, 1e-9 * gumbelExact,
            $"Gumbel θ = 8 engine mean {gumbelAfp:G12} vs the dense analytic-CDF re-derivation {gumbelExact:G12}.");
        Assert.AreEqual(joeExact, joeAfp, 1e-9 * joeExact,
            $"Joe θ = 8 engine mean {joeAfp:G12} vs the dense analytic-CDF re-derivation {joeExact:G12}.");
        Assert.IsTrue(gumbelAfp > 1.25d * independenceExact && joeAfp > 1.25d * independenceExact,
            $"Near-comonotone upper-tail dependence must sit far above independence: Gumbel {gumbelAfp:G6}, Joe {joeAfp:G6}, independence {independenceExact:G6}.");
        Console.WriteLine($"extreme dependence: gumbel exact {gumbelExact:G12}, engine {gumbelAfp:G12}, rel {Math.Abs(gumbelAfp - gumbelExact) / gumbelExact:G4}; " +
            $"joe exact {joeExact:G12}, engine {joeAfp:G12}, rel {Math.Abs(joeAfp - joeExact) / joeExact:G4}");
    }

    /// <summary>
    /// Verifies marginal-uncertainty propagation realization for realization: the secondary
    /// marginal is a parametric hazard with a deterministically injected Normal posterior (a
    /// fixed formula — no randomness), so each engine ensemble realization is a deterministic
    /// walk of the injected sets and compares directly against a hand-rolled dense conditional
    /// integral of the same parameter set. The single-cell surface is linear in the primary
    /// signal, so the primary integration is exact and the per-realization target reduces to
    /// ∫₀¹ P(x̄, Norm_i⁻¹(t)) dt by dense Simpson. The 0.1% relative assert covers the
    /// ensemble-pass quadrature discipline (1e-4 relative), the adaptive conditional-sweep
    /// residual at that discipline, and the oracle's own density — each orders of magnitude
    /// below the bound.
    /// </summary>
    [TestMethod]
    public void Test_MarginalUncertainty_RealizationParity()
    {
        // Arrange — the injected posterior: Normal(μᵢ, σᵢ) with μᵢ = 40 + 20·i/(R−1),
        // σᵢ = 6 + 4·i/(R−1).
        const int realizations = 120;
        var sets = new List<ParameterSet>(realizations);
        for (int i = 0; i < realizations; i++)
        {
            double fraction = i / (realizations - 1d);
            sets.Add(new ParameterSet(new[] { 40d + 20d * fraction, 6d + 4d * fraction }, 0d));
        }
        var marginalY = new ParametricUnivariateHazard
        {
            Name = "Posterior Y",
            SpecifiedHazard = "Y",
            HazardUnit = "u",
            ParentDistribution = new Normal(50d, 8d),
        };
        marginalY.Estimate(sets);

        var component = SingleCellComponent(null, 200, SingleCellSurface(0.1d, 0.4d, 0.3d, 0.85d), marginalY);
        var analysis = new RiskAnalysis(new[] { component });
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.Realizations = realizations;

        // Act
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated, "The ensemble run must estimate.");
        var ensemble = analysis.RiskResults!;
        Assert.AreEqual(realizations, ensemble.Count, "One ensemble entry per injected parameter set.");

        // Assert — realization for realization against the hand-rolled dense integral.
        double worst = 0d;
        for (int i = 0; i < realizations; i++)
        {
            double mu = sets[i].Values[0];
            double sigma = sets[i].Values[1];
            var normal = new Normal(mu, sigma);

            // ∫₀¹ P(50, Norm⁻¹(t)) dt by composite Simpson (the surface clamps at the cell
            // edges, so the unbounded tails integrate the flat corner values).
            const int intervals = 1 << 16;
            double h = 1d / intervals;
            double sum = 0d;
            for (int j = 0; j <= intervals; j++)
            {
                double t = j == 0 ? 1e-12 : j == intervals ? 1d - 1e-12 : j * h;
                double y = Math.Clamp(normal.InverseCDF(t), 0d, 100d);
                double atLow = 0.1d + (0.4d - 0.1d) * (y / 100d);
                double atHigh = 0.3d + (0.85d - 0.3d) * (y / 100d);
                double value = 0.5d * (atLow + atHigh);
                int weight = j == 0 || j == intervals ? 1 : (j & 1) == 1 ? 4 : 2;
                sum += weight * value;
            }
            double oracle = sum * h / 3d;

            double engine = ensemble[i]!.Fail.TotalProbability;
            double relative = Math.Abs(engine - oracle) / oracle;
            worst = Math.Max(worst, relative);
            Assert.AreEqual(oracle, engine, 1e-3 * oracle,
                $"Realization {i}: engine {engine:G10} vs dense integral {oracle:G10} (μ = {mu:G6}, σ = {sigma:G6}).");
        }
        Console.WriteLine($"marginal-uncertainty parity: {realizations} realizations, worst relative deviation {worst:G4}");
    }

    /// <summary>
    /// Verifies the secondary-axis discretization diagnostic — the quarantined fixed-grid
    /// instrument — against this family's measured truth. (i) Level cross-pins, re-anchored
    /// under the two-dimensional adaptive interior ruling (2026-09-05): the production mean
    /// run no longer integrates the fixed grid, so the levels cross-pin against a
    /// doubled-count twin diagnostic instead — the twenty- and ten-bin instrument evaluations
    /// must be bit-identical from either ladder (the levels are the instrument itself at
    /// reduced grids, never a re-implementation). (ii) On the smooth separable-log fixture
    /// (clean O(N⁻²) per the convergence study), the Richardson estimate must land within a
    /// factor of two of the true bins = 20 error — in-regime the estimate is e₂₀·(1 + O(N⁻²))
    /// exactly — the observed ratio must sit near the second-order four, and the extrapolated
    /// value must beat the configured-count value against the closed form. (iii) On the
    /// legacy tail-concentrated fixture (the study's measured pre-asymptotic series), the
    /// estimate must stay inside a documented indicative band of the true error — one order
    /// each way — while the observed ratio falls below the second-order four: the regime
    /// check flagging exactly the fixture the study flagged.
    /// </summary>
    [TestMethod]
    public void Test_DiscretizationDiagnostic_TracksMeasuredErrors()
    {
        // Arrange — the smooth fixture at the default 20 bins, primary quadrature pinned at
        // 1e-10 so every difference is the conditional discretization's (the study's rationale).
        double ln10 = Math.Log(10d);
        double smoothExact = 1e-4d * ((100d - 1d) / (2d * ln10)) * ((10d - 1d) / ln10);
        var smoothAnalysis = new RiskAnalysis(new[] { SingleCellComponent(null, 20,
            SingleCellSurface(1e-4d, 1e-3d, 1e-2d, 1e-1d, Transform.Logarithmic)) });
        smoothAnalysis.Options.UseDefaults = false;
        smoothAnalysis.Options.Tolerance = 1e-10;

        // Act
        var smooth = smoothAnalysis.EstimateSecondaryDiscretizationError(0);

        // Assert — (i) the level cross-pins against an independently configured
        // doubled-count instrument ladder (its halved and quartered levels are this
        // ladder's configured and halved counts).
        Assert.IsNotNull(smooth);
        var doubledAnalysis = new RiskAnalysis(new[] { SingleCellComponent(null, 40,
            SingleCellSurface(1e-4d, 1e-3d, 1e-2d, 1e-1d, Transform.Logarithmic)) });
        doubledAnalysis.Options.UseDefaults = false;
        doubledAnalysis.Options.Tolerance = 1e-10;
        var doubled = doubledAnalysis.EstimateSecondaryDiscretizationError(0);
        Assert.IsNotNull(doubled);
        Assert.AreEqual(doubled!.FailureProbability.HalfValue, smooth!.FailureProbability.Value, 0d,
            "The twenty-bin instrument level must be identical from either ladder, bit-exactly.");
        Assert.AreEqual(doubled.FailureProbability.QuarterValue, smooth.FailureProbability.HalfValue, 0d,
            "The ten-bin instrument level must be identical from either ladder, bit-exactly.");

        // (ii) the in-regime estimate against the true error.
        double trueSmoothError = Math.Abs(smooth.FailureProbability.Value - smoothExact) / smoothExact;
        double estimatedSmoothError = smooth.FailureProbability.EstimatedRelativeError;
        Assert.IsTrue(estimatedSmoothError > 0.5d * trueSmoothError && estimatedSmoothError < 2d * trueSmoothError,
            $"Smooth fixture: the Richardson estimate {estimatedSmoothError:G4} must sit within a factor of two of the true error {trueSmoothError:G4}.");
        Assert.IsTrue(smooth.FailureProbability.ObservedRatio > 3d && smooth.FailureProbability.ObservedRatio < 5.5d,
            $"Smooth fixture: the observed ratio {smooth.FailureProbability.ObservedRatio:G4} must sit near the second-order four.");
        Assert.IsTrue(Math.Abs(smooth.FailureProbability.ExtrapolatedValue - smoothExact) < Math.Abs(smooth.FailureProbability.Value - smoothExact),
            "The extrapolation must improve on the configured-count value in-regime.");

        // (iii) the tail-concentrated legacy fixture — indicative band plus the regime flag.
        double legacyExact = BivariateOracleFixtures.ExactMarginalizedSurface(0.8d);
        var legacyAnalysis = new RiskAnalysis(new[] {
            BivariateOracleFixtures.JointComponent(20, BivariateOracleFixtures.DegeneratePrimary(0.8d)) });
        var legacy = legacyAnalysis.EstimateSecondaryDiscretizationError(0);
        Assert.IsNotNull(legacy);
        double trueLegacyError = Math.Abs(legacy!.FailureProbability.Value - legacyExact) / legacyExact;
        double estimatedLegacyError = legacy.FailureProbability.EstimatedRelativeError;
        Assert.IsTrue(trueLegacyError > 0.2d,
            $"The legacy fixture's 20-bin error {trueLegacyError:G4} must reproduce the study's ≈ 0.353 inadequacy scale.");
        Assert.IsTrue(estimatedLegacyError > 0.1d * trueLegacyError && estimatedLegacyError < 10d * trueLegacyError,
            $"Legacy fixture: the estimate {estimatedLegacyError:G4} must stay within one order of the true error {trueLegacyError:G4} (indicative only, pre-asymptotic).");
        Assert.IsTrue(double.IsNaN(legacy.FailureProbability.ObservedRatio) || legacy.FailureProbability.ObservedRatio < 3.5d,
            $"Legacy fixture: the observed ratio {legacy.FailureProbability.ObservedRatio:G4} must fall below the second-order four — the regime check flagging the tail concentration.");
    }
}
