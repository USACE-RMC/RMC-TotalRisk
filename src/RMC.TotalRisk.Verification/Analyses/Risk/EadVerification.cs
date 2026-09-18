using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using Numerics.Sampling;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Verification.Analyses.Risk;

/// <summary>
/// Expected annual damage — the conversion of the legacy <c>Test_EAD</c> oracle
/// (<c>Test_RiskAnalysis.vb:2375</c>): Monte Carlo integration of one eight-knot
/// damage-frequency curve, verified three ways — an exact closed form, the ported Monte Carlo
/// oracle, and two equivalent engine mappings of the same curve.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Scenario</b> (the exact legacy curve): exceedance probabilities
/// {0.5, 0.2, 0.1, 0.04, 0.02, 0.01, 0.005, 0.002} against damages
/// {212; 24,545; 275,766; 296,022; 333,920; 395,563; 448,005; 962,545}, linear between knots
/// in probability and clamped flat outside — damage 212 above exceedance 0.5 and 962,545
/// below exceedance 0.002 (the legacy interpolator's end behavior).
/// </para>
/// <para>
/// <b>Three-way verification.</b> The clamped piecewise-linear curve integrates exactly:
/// EAD = Σ trapezoids + both clamp rectangles, the second moment integrates
/// segment-by-segment as (Δp)(D_i² + D_i·D_{i+1} + D_{i+1}²)/3, the value-at-risk at
/// α = 0.01 is the knot damage 395,563, and the conditional value-at-risk is
/// (1/α)·∫₀^α D(p) dp over the deepest two segments plus the clamp. The ported oracle
/// (uniform draws through <c>MersenneTwister(12345)</c>, N = 1,000,000 — the legacy 10M
/// dropped 10× per policy) validates against the closed form at 4·SE, and the engine is
/// asserted against the closed form at the quadrature's relative tolerance and against the
/// oracle at 4·SE.
/// </para>
/// <para>
/// <b>Two engine mappings, equal by construction:</b> the frequency curve becomes the
/// component hazard (exceedance versus damage) with an identity consequence (knots y = x —
/// piecewise-linear identity is exact): (A) a single response-free non-failure mode, making
/// EAD the background (and total) risk with zero failure probability — the natural
/// damage-frequency portrayal; (B) a single always-failing mode (fragility ≡ 1), making EAD
/// the failure risk with an annualized failure probability of one.
/// </para>
/// </remarks>
[TestClass]
public class EadVerification
{
    /// <summary>The oracle realization count (legacy 10M dropped 10× per the conversion policy).</summary>
    private const int OracleRealizations = 1_000_000;

    /// <summary>The legacy oracle seed.</summary>
    private const int OracleSeed = 12345;

    /// <summary>The tolerance multiplier on the Monte Carlo standard error.</summary>
    private const double K = 4d;

    /// <summary>The exceedance level for the value-at-risk and conditional value-at-risk pins.</summary>
    private const double Alpha = 0.01d;

    /// <summary>The legacy exceedance probabilities (descending).</summary>
    private static readonly double[] ExceedanceProbabilities = { 0.5d, 0.2d, 0.1d, 0.04d, 0.02d, 0.01d, 0.005d, 0.002d };

    /// <summary>The legacy damages (ascending, paired with the descending exceedances).</summary>
    private static readonly double[] Damages = { 212d, 24545d, 275766d, 296022d, 333920d, 395563d, 448005d, 962545d };

    #region Closed Forms

    /// <summary>
    /// The exact expected annual damage of the clamped piecewise-linear curve:
    /// (1 − p₁)·D₁ + Σ (p_i − p_{i+1})(D_i + D_{i+1})/2 + p_n·D_n.
    /// </summary>
    private static double ExactMean()
    {
        double mean = (1d - ExceedanceProbabilities[0]) * Damages[0];
        for (int i = 0; i < ExceedanceProbabilities.Length - 1; i++)
        {
            mean += (ExceedanceProbabilities[i] - ExceedanceProbabilities[i + 1]) * (Damages[i] + Damages[i + 1]) / 2d;
        }
        mean += ExceedanceProbabilities[ExceedanceProbabilities.Length - 1] * Damages[Damages.Length - 1];
        return mean;
    }

    /// <summary>
    /// The exact second raw moment: clamp rectangles at D² plus
    /// Σ (Δp)(D_i² + D_i·D_{i+1} + D_{i+1}²)/3 over the linear segments.
    /// </summary>
    private static double ExactSecondMoment()
    {
        double second = (1d - ExceedanceProbabilities[0]) * Damages[0] * Damages[0];
        for (int i = 0; i < ExceedanceProbabilities.Length - 1; i++)
        {
            second += (ExceedanceProbabilities[i] - ExceedanceProbabilities[i + 1])
                * (Damages[i] * Damages[i] + Damages[i] * Damages[i + 1] + Damages[i + 1] * Damages[i + 1]) / 3d;
        }
        second += ExceedanceProbabilities[ExceedanceProbabilities.Length - 1] * Damages[Damages.Length - 1] * Damages[Damages.Length - 1];
        return second;
    }

    /// <summary>
    /// The exact conditional value-at-risk at the given exceedance level (a knot probability):
    /// (1/α)·[Σ deeper-segment trapezoids + the deep clamp rectangle].
    /// </summary>
    /// <param name="alpha">The exceedance level — must be one of the knot probabilities.</param>
    private static double ExactConditionalValueAtRisk(double alpha)
    {
        double integral = ExceedanceProbabilities[ExceedanceProbabilities.Length - 1] * Damages[Damages.Length - 1];
        for (int i = 0; i < ExceedanceProbabilities.Length - 1; i++)
        {
            if (ExceedanceProbabilities[i] <= alpha)
            {
                integral += (ExceedanceProbabilities[i] - ExceedanceProbabilities[i + 1]) * (Damages[i] + Damages[i + 1]) / 2d;
            }
        }
        return integral / alpha;
    }

    /// <summary>The exact exceedance probability at an arbitrary damage level (linear in probability between knots).</summary>
    /// <param name="damage">The damage level probed.</param>
    private static double ExactExceedance(double damage)
    {
        if (damage <= Damages[0]) return ExceedanceProbabilities[0];
        if (damage >= Damages[Damages.Length - 1]) return ExceedanceProbabilities[ExceedanceProbabilities.Length - 1];
        for (int i = 0; i < Damages.Length - 1; i++)
        {
            if (damage <= Damages[i + 1])
            {
                double fraction = (damage - Damages[i]) / (Damages[i + 1] - Damages[i]);
                return ExceedanceProbabilities[i] + fraction * (ExceedanceProbabilities[i + 1] - ExceedanceProbabilities[i]);
            }
        }
        return ExceedanceProbabilities[ExceedanceProbabilities.Length - 1];
    }

    #endregion

    #region Oracle and Builders

    /// <summary>
    /// The legacy oracle: the mean (and dispersion) of the damage curve interpolated at uniform
    /// exceedance draws with end clamps — <c>d += damage.Interpolate(rnd.NextDouble())</c>.
    /// </summary>
    /// <returns>The mean, its standard error, the standard deviation, and its standard error.</returns>
    private static (double Mean, double MeanSe, double Sigma, double SigmaSe) RunOracle()
    {
        // Ascending arrays for the interpolator: probability ascends as damage descends.
        var ascendingProbabilities = new double[ExceedanceProbabilities.Length];
        var descendingDamages = new double[Damages.Length];
        for (int i = 0; i < ExceedanceProbabilities.Length; i++)
        {
            ascendingProbabilities[i] = ExceedanceProbabilities[ExceedanceProbabilities.Length - 1 - i];
            descendingDamages[i] = Damages[Damages.Length - 1 - i];
        }

        var stream = new MersenneTwister(OracleSeed);
        double m1 = 0d, m2 = 0d, m3 = 0d, m4 = 0d;
        for (int i = 0; i < OracleRealizations; i++)
        {
            double probability = stream.NextDouble();
            double damage;
            if (probability <= ascendingProbabilities[0]) damage = descendingDamages[0];
            else if (probability >= ascendingProbabilities[ascendingProbabilities.Length - 1]) damage = descendingDamages[descendingDamages.Length - 1];
            else
            {
                int index = Array.BinarySearch(ascendingProbabilities, probability);
                if (index < 0) index = ~index;
                double fraction = (probability - ascendingProbabilities[index - 1]) / (ascendingProbabilities[index] - ascendingProbabilities[index - 1]);
                damage = descendingDamages[index - 1] + fraction * (descendingDamages[index] - descendingDamages[index - 1]);
            }

            // Welford/Pébay accumulation through the fourth moment.
            long n = i + 1;
            double delta = damage - m1;
            double deltaOverN = delta / n;
            double deltaOverN2 = deltaOverN * deltaOverN;
            double term1 = delta * deltaOverN * (n - 1);
            m1 += deltaOverN;
            m4 += term1 * deltaOverN2 * ((double)n * n - 3d * n + 3d) + 6d * deltaOverN2 * m2 - 4d * deltaOverN * m3;
            m3 += term1 * deltaOverN * (n - 2) - 3d * deltaOverN * m2;
            m2 += term1;
        }

        double variance = m2 / OracleRealizations;
        double sigma = Math.Sqrt(variance);
        double fourth = m4 / OracleRealizations;
        double sigmaSe = Math.Sqrt(Math.Max(0d, fourth - variance * variance)) / (2d * sigma * Math.Sqrt(OracleRealizations));
        return (m1, sigma / Math.Sqrt(OracleRealizations), sigma, sigmaSe);
    }

    /// <summary>Builds the damage-frequency hazard (exceedance descending as damage ascends).</summary>
    private static TabularHazard DamageFrequency()
    {
        var ordinates = new UncertainOrdinate[ExceedanceProbabilities.Length];
        for (int i = 0; i < ExceedanceProbabilities.Length; i++)
        {
            ordinates[i] = new UncertainOrdinate(ExceedanceProbabilities[i], new Deterministic(Damages[i]));
        }
        return new TabularHazard
        {
            Name = "Damage Frequency",
            SpecifiedHazard = "Damage",
            HazardUnit = "$",
            ProbabilityTransform = Transform.None,
            NoUncertaintyFunction = new UncertainOrderedPairedData(ordinates,
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds the identity consequence: knots y = x at the eight damages (piecewise-linear identity is exact).</summary>
    private static TabularConsequence IdentityConsequence()
    {
        var ordinates = new UncertainOrdinate[Damages.Length];
        for (int i = 0; i < Damages.Length; i++)
        {
            ordinates[i] = new UncertainOrdinate(Damages[i], new Deterministic(Damages[i]));
        }
        return new TabularConsequence
        {
            Name = "Damage Identity",
            SpecifiedHazard = "Damage",
            HazardUnit = "$",
            SpecifiedConsequence = "Damage",
            ConsequenceUnit = "$",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(ordinates,
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds mapping A: the damage-frequency hazard with a single response-free non-failure mode.</summary>
    private static RiskAnalysis BuildBackgroundMapping()
    {
        var component = new SystemComponent { Name = "Damage Reach" };
        component.HazardFunction = DamageFrequency();
        component.AddFailureMode(new FailureMode(null, null, null, IdentityConsequence()));
        var analysis = new RiskAnalysis(new[] { component }) { Name = "EAD background mapping" };
        analysis.Options.Alpha = Alpha;
        analysis.Options.LECOutputLength = 1000;
        return analysis;
    }

    /// <summary>Builds mapping B: the damage-frequency hazard with a single always-failing mode (fragility ≡ 1).</summary>
    private static RiskAnalysis BuildAlwaysFailMapping()
    {
        var fragility = new TabularResponse
        {
            Name = "Certain Failure",
            SpecifiedHazard = "Damage",
            HazardUnit = "$",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(Damages[0], new Deterministic(1d)), new UncertainOrdinate(Damages[Damages.Length - 1], new Deterministic(1d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
        var component = new SystemComponent { Name = "Damage Reach" };
        component.HazardFunction = DamageFrequency();
        component.AddFailureMode(new FailureMode(null, null, fragility, IdentityConsequence()));
        var analysis = new RiskAnalysis(new[] { component }) { Name = "EAD always-fail mapping" };
        analysis.Options.Alpha = Alpha;
        analysis.Options.LECOutputLength = 1000;
        return analysis;
    }

    #endregion

    /// <summary>
    /// The three-way pins: the oracle validates against the closed form at 4·SE, and both
    /// engine mappings reproduce the closed-form mean and standard deviation at 1e-5 relative.
    /// The published means read the recorded risk-point masses — the quadrature's own accepted
    /// weights, not a midpoint-trapezoid partition over them — so the residual is the
    /// adaptive refinement's rather than a partition's. Also pinned: the exceedance
    /// ordinates at two off-knot probes, the value-at-risk (the knot damage at α = 0.01), and
    /// the conditional value-at-risk (0.1% relative floors absorb the output-curve
    /// interpolation).
    /// </summary>
    [TestMethod]
    public void Test_Ead_ClosedForm_Oracle_BothMappings()
    {
        // Arrange — the exact answers and the legacy oracle.
        double exactMean = ExactMean();
        double exactSigma = Math.Sqrt(ExactSecondMoment() - exactMean * exactMean);
        double exactValueAtRisk = 395563d;
        double exactConditionalValueAtRisk = ExactConditionalValueAtRisk(Alpha);
        var oracle = RunOracle();

        // The oracle must reproduce the closed form (self-validation of the ported body).
        Assert.AreEqual(exactMean, oracle.Mean, K * oracle.MeanSe, "The legacy oracle must match the closed-form EAD.");
        Assert.AreEqual(exactSigma, oracle.Sigma, K * oracle.SigmaSe, "The legacy oracle must match the closed-form dispersion.");

        // Act — both engine mappings, mean-only.
        var background = BuildBackgroundMapping();
        background.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(background.IsEstimated, "Mapping A must estimate.");
        var backgroundSummary = background.RiskResults![0]!;

        var alwaysFail = BuildAlwaysFailMapping();
        alwaysFail.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(alwaysFail.IsEstimated, "Mapping B must estimate.");
        var alwaysFailSummary = alwaysFail.RiskResults![0]!;

        // Assert — mapping A: EAD is the background (and total) risk with no failure.
        Assert.AreEqual(exactMean, backgroundSummary.Background.Mean, 1e-5 * exactMean, "Mapping A: background mean = EAD.");
        Assert.AreEqual(exactMean, backgroundSummary.Total.Mean, 1e-5 * exactMean, "Mapping A: total mean = EAD.");
        Assert.AreEqual(exactMean, backgroundSummary.NonFail.Mean, 1e-5 * exactMean, "Mapping A: non-failure mean = EAD.");
        Assert.AreEqual(0d, backgroundSummary.Fail.TotalProbability, 1e-12, "Mapping A: the annualized failure probability is zero.");
        Assert.AreEqual(1d, backgroundSummary.Total.TotalProbability, 1e-9, "Mapping A: the total stream is exhaustive.");
        Assert.AreEqual(exactSigma, backgroundSummary.Total.StandardDeviation, 5e-5 * exactSigma,
            "Mapping A: total standard deviation.");
        Assert.AreEqual(oracle.Mean, backgroundSummary.Total.Mean, K * oracle.MeanSe, "Mapping A: total mean within the oracle's 4·SE.");

        // Mapping B: EAD is the failure risk with certain failure.
        Assert.AreEqual(exactMean, alwaysFailSummary.Fail.Mean, 1e-5 * exactMean, "Mapping B: failure mean = EAD.");
        Assert.AreEqual(1d, alwaysFailSummary.Fail.TotalProbability, 1e-9, "Mapping B: the annualized failure probability is one.");
        Assert.AreEqual(exactMean, alwaysFailSummary.Fail.ConditionalMean, 1e-5 * exactMean, "Mapping B: the conditional mean equals the unconditional at α = 1.");
        Assert.AreEqual(alwaysFailSummary.Fail.Mean, backgroundSummary.Background.Mean, 1e-6 * exactMean,
            "The two mappings integrate the same curve and must agree.");

        // The total-stream measures against the closed forms (mapping A).
        var totalCurve = background.MeanRiskResults!.Curves.Total;
        Assert.AreEqual(exactValueAtRisk, totalCurve.ValueAtRisk, 1e-3 * exactValueAtRisk,
            "Value-at-risk at α = 0.01 is the knot damage 395,563 (0.1% relative output-resolution floor).");
        Assert.AreEqual(exactConditionalValueAtRisk, totalCurve.ConditionalValueAtRisk, 1e-3 * exactConditionalValueAtRisk,
            "Conditional value-at-risk at α = 0.01 (0.1% relative floor).");

        // Exceedance probes at off-knot damages (exact linear-in-probability ordinates). The
        // tolerance is the cost of reading a linear-in-probability segment through the output
        // curve's log-log interpolation convention; the construction itself is exact at the
        // recorded points. It widened from 0.5% to 1% when the recorded masses moved onto the
        // quadrature weights: the curve is then built from the ACCEPTED nodes rather than every
        // evaluation, which is roughly half as many knots to interpolate between, so an off-knot
        // probe spans wider segments — measured ≈ 0.53% here against ≈ 0.2% before. That is a
        // resolution property of the output curve, and it is traded for a mean that moved from
        // 5.7e-7 to 4.0e-11 relative against a dense reference.
        Assert.AreEqual(ExactExceedance(300000d), totalCurve.LEC.GetYFromX(300000d, Transform.Logarithmic, Transform.Logarithmic),
            1e-2 * ExactExceedance(300000d), "Exceedance at damage 300,000.");
        Assert.AreEqual(ExactExceedance(500000d), totalCurve.LEC.GetYFromX(500000d, Transform.Logarithmic, Transform.Logarithmic),
            1e-2 * ExactExceedance(500000d), "Exceedance at damage 500,000.");

        Console.WriteLine(
            $"EAD: closed form {exactMean:G9}, oracle {oracle.Mean:G9} (SE {oracle.MeanSe:G4}), engine background {backgroundSummary.Background.Mean:G9}, " +
            $"engine fail {alwaysFailSummary.Fail.Mean:G9}, σ exact {exactSigma:G6}/engine {backgroundSummary.Total.StandardDeviation:G6}, " +
            $"VaR exact {exactValueAtRisk:G6}/engine {totalCurve.ValueAtRisk:G6}, CVaR exact {exactConditionalValueAtRisk:G9}/engine {totalCurve.ConditionalValueAtRisk:G9}");
    }

    /// <summary>
    /// The reproducibility pins: renames and the XML round-trip of the background mapping are
    /// bit-identical on the mean-only numeric surface.
    /// </summary>
    [TestMethod]
    public void Test_Reproducibility_RenameAndRoundTrip()
    {
        // Arrange
        var baseline = BuildBackgroundMapping();
        baseline.RunAsync().GetAwaiter().GetResult();

        // Act / Assert — metadata renames are bit-identical.
        var renamed = BuildBackgroundMapping();
        renamed.Name = "Renamed EAD";
        var component = renamed.Components[0];
        component.Name = "Renamed Reach";
        foreach (var function in component.GetReferencedFunctions())
        {
            function.Name = $"Renamed {function.Name}";
            function.AssignNewId();
        }
        renamed.RunAsync().GetAwaiter().GetResult();
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(baseline.RiskResults![0]!.Total.Mean),
            BitConverter.DoubleToInt64Bits(renamed.RiskResults![0]!.Total.Mean), "rename: the total mean must be bit-identical.");
        CollectionAssert.AreEqual(baseline.MeanRiskResults!.Curves.Total.LECProbabilities, renamed.MeanRiskResults!.Curves.Total.LECProbabilities,
            "rename: the total LEC probabilities must be bit-identical.");

        // The XML round-trip is bit-identical.
        var restored = new RiskAnalysis(new[] { new SystemComponent(baseline.Components[0].ToXElement()) });
        restored.Options.Alpha = Alpha;
        restored.Options.LECOutputLength = baseline.Options.LECOutputLength;
        restored.RunAsync().GetAwaiter().GetResult();
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(baseline.RiskResults[0]!.Total.Mean),
            BitConverter.DoubleToInt64Bits(restored.RiskResults![0]!.Total.Mean), "round-trip: the total mean must be bit-identical.");
        CollectionAssert.AreEqual(baseline.MeanRiskResults.Curves.Total.LECProbabilities, restored.MeanRiskResults!.Curves.Total.LECProbabilities,
            "round-trip: the total LEC probabilities must be bit-identical.");
    }
}
