using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using Numerics.Mathematics.RootFinding;
using Numerics.Sampling;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Transforms;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Verification.Analyses.Risk;

/// <summary>
/// The closed-form-functions family: <c>LinearTransform</c>, <c>PowerTransform</c>, and
/// <c>NonparametricHazard</c> verified with fresh oracles — no legacy <c>Test_TotalRisk</c>
/// scenarios exist for these types (a survey of the legacy v1.0 repository found zero transform usages and only
/// FDA-importer usages of the nonparametric hazard), so the documented anchor is the 2024
/// verification report's Nonparametric Hazard Function section (Beargrass Creek SF-8 vs
/// HEC-FDA 1.4.3, Table 38) plus hand-rolled Monte Carlo and quadrature oracles for the first
/// engine passage of the closed-form transforms.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>V1/V2 — transform chains through the engine.</b> A deterministic five-knot flow-frequency
/// hazard drives a flow→stage transform (linear in V1, power forward in V2), a deterministic
/// stage fragility ramp, and a deterministic stage consequence ramp. The knowledge dimension is
/// the transform's single co-monotonic percentile — the first time a closed-form transform's
/// uncertainty flows through the engine end to end. The ensemble grand means (failure risk and
/// annualized failure probability over 1,000 LHS realizations) are verified against a flat
/// two-uniform Monte Carlo oracle (<c>MersenneTwister(12345)</c>, N = 1,000,000): by linearity
/// of the expectation, the grand mean over knowledge realizations of the exactly integrated
/// event dimension equals the double integral the flat oracle estimates. Tolerances combine the
/// oracle's standard error with the engine's outer-sampling error charged at the Monte Carlo
/// rate (k = 4; Latin hypercube is tighter, so the bound is conservative). Each test also runs
/// the deterministic-transform variant (IsUncertain = false — sampling dimension zero) through
/// BOTH the mean-only and ensemble paths against a dense trapezoid quadrature of the closed-form
/// chain — the proof that a D = 0 transform rides the sampler walk and the by-index sampling
/// path cleanly (per-realization outputs bit-identical across realizations).
/// </para>
/// <para>
/// <b>V3 — the SF-8 / Table 38 anchor.</b> The report's SF-8 inputs (transcribed from Figure 16:
/// nine AEP→flow ordinates anchored at 0.999, logarithmic hazard and Normal-Z probability
/// transforms, effective record length 48, extrapolation AEP 1e-4) are re-derived independently
/// in-test with the legacy object pipeline — base-e <c>LogNormal</c> moment mapping and the
/// per-ordinate <c>Brent</c> 1%-bound repair, the exact v1.0 method the shipped class replaces
/// with closed forms — and every derived ordinate must agree (the optimization-equivalence
/// anchor). The class's ±2 log-standard-deviation quantiles are then pinned against all twenty
/// published RMC-TotalRisk constants of Table 38 (log10 space, tolerance 5.5e-4 = the table's
/// rounding half-width plus solver headroom; the report's HEC-FDA columns are context — its
/// ≤ 0.9% differences are interpolation-design gaps, not targets). The uncertain mean curve is
/// checked for internal consistency (bracketed by the 5%/95% percentile curves and strictly
/// ordered); its assembly is the TabularHazard Hazard-mode pattern already verified
/// against exact quadrature by the NFIP dense-tabular family.
/// </para>
/// <para>
/// <b>V4 — the nonparametric hazard through the reliability engine.</b> The deterministic-mode
/// SF-8 curve (a known knot ladder with ln-flow linear in z(AEP) between knots) drives a
/// deterministic flow fragility in reliability mode; the engine's annualized failure probability
/// is verified against an independent Monte Carlo oracle that reconstructs the curve's declared
/// interpolation semantics from the raw knots (<c>MersenneTwister(45678)</c>, N = 1,000,000).
/// The uncertain-mode ensemble grand-mean AFP (300 LHS realizations) is verified against a
/// two-loop oracle — 10,000 outer knowledge percentiles, each inner AFP a dense trapezoid over
/// the percentile curve built from the independently verified derived table. Reproducibility is
/// pinned on the deterministic-mode scenario (same seed → bit-identical; rename + XML round-trip
/// → bit-identical): the uncertain mean-curve assembly reduces with a parallel sum and is
/// deliberately excluded from bit pins (the documented ExpectedProbabilities ulp
/// nondeterminism).
/// </para>
/// </remarks>
[TestClass]
public class ClosedFormFunctionsVerification
{
    /// <summary>The flat-oracle realization count.</summary>
    private const int OracleRealizations = 1_000_000;

    /// <summary>The legacy primary oracle seed.</summary>
    private const int OracleSeed = 12345;

    /// <summary>The legacy secondary oracle seed (the V4 reliability oracle).</summary>
    private const int ReliabilitySeed = 45678;

    /// <summary>The engine ensemble realization count for the transform chains.</summary>
    private const int EngineRealizations = 1000;

    /// <summary>The tolerance multiplier on Monte Carlo standard errors.</summary>
    private const double K = 4d;

    /// <summary>The five-knot flow-frequency ladder (descending AEP).</summary>
    private static readonly double[] HazardProbabilities = { 0.99d, 0.5d, 0.1d, 0.01d, 0.001d };

    /// <summary>The five-knot flows (ascending, paired with the descending AEPs).</summary>
    private static readonly double[] HazardFlows = { 500d, 1000d, 2000d, 4000d, 8000d };

    /// <summary>The SF-8 input AEP ladder (Figure 16 of the 2024 verification report).</summary>
    private static readonly double[] Sf8Probabilities = { 0.999d, 0.5d, 0.2d, 0.1d, 0.04d, 0.02d, 0.01d, 0.004d, 0.002d };

    /// <summary>The SF-8 input flows (Figure 16 of the 2024 verification report).</summary>
    private static readonly double[] Sf8Flows = { 900d, 1489d, 2106d, 3119d, 4183d, 5036d, 6198d, 7001d, 9610d };

    /// <summary>The SF-8 effective record length (Figure 16).</summary>
    private const int Sf8RecordLength = 48;

    /// <summary>
    /// Table 38's RMC-TotalRisk columns: AEP, the −2 log-standard-deviation quantile, and the
    /// +2 quantile, in log10 flow space.
    /// </summary>
    private static readonly (double Aep, double Lo, double Hi)[] Table38 =
    {
        (0.0001d, 4.248d, 4.740d),
        (0.002d, 3.737d, 4.229d),
        (0.004d, 3.599d, 4.091d),
        (0.01d, 3.546d, 4.038d),
        (0.02d, 3.456d, 3.948d),
        (0.04d, 3.445d, 3.798d),
        (0.1d, 3.336d, 3.652d),
        (0.2d, 3.222d, 3.424d),
        (0.5d, 3.136d, 3.210d),
        (0.999d, 2.878d, 3.030d),
    };

    #region Shared Builders and Helpers

    /// <summary>Builds the deterministic five-knot flow-frequency hazard (linear probability interpolation).</summary>
    private static TabularHazard FlowFrequency()
    {
        var ordinates = new UncertainOrdinate[HazardProbabilities.Length];
        for (int i = 0; i < HazardProbabilities.Length; i++)
        {
            ordinates[i] = new UncertainOrdinate(HazardProbabilities[i], new Deterministic(HazardFlows[i]));
        }
        return new TabularHazard
        {
            Name = "Flow Frequency",
            SpecifiedHazard = "Flow",
            HazardUnit = "cfs",
            ProbabilityTransform = Transform.None,
            NoUncertaintyFunction = new UncertainOrderedPairedData(ordinates,
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds a deterministic two-knot stage fragility ramp.</summary>
    /// <param name="zeroStage">The stage at and below which failure probability is zero.</param>
    /// <param name="oneStage">The stage at and above which failure probability is one.</param>
    private static TabularResponse StageFragility(double zeroStage, double oneStage)
    {
        return new TabularResponse
        {
            Name = "Stage Fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(zeroStage, new Deterministic(0d)), new UncertainOrdinate(oneStage, new Deterministic(1d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds a deterministic two-knot stage consequence ramp.</summary>
    /// <param name="zeroStage">The stage at and below which the consequence is zero.</param>
    /// <param name="topStage">The stage at and above which the consequence saturates.</param>
    /// <param name="topValue">The saturated consequence value.</param>
    private static TabularConsequence StageConsequence(double zeroStage, double topStage, double topValue)
    {
        return new TabularConsequence
        {
            Name = "Stage Damages",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(zeroStage, new Deterministic(0d)), new UncertainOrdinate(topStage, new Deterministic(topValue)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>
    /// The oracle's flow draw: u is the exceedance probability (the legacy convention — small u
    /// is rare), linearly interpolated between the flow-frequency knots with flat end clamps.
    /// </summary>
    /// <param name="u">The uniform exceedance draw.</param>
    private static double DrawFlow(double u)
    {
        if (u <= HazardProbabilities[HazardProbabilities.Length - 1]) return HazardFlows[HazardFlows.Length - 1];
        if (u >= HazardProbabilities[0]) return HazardFlows[0];
        for (int i = 0; i < HazardProbabilities.Length - 1; i++)
        {
            if (u >= HazardProbabilities[i + 1])
            {
                double fraction = (u - HazardProbabilities[i]) / (HazardProbabilities[i + 1] - HazardProbabilities[i]);
                return HazardFlows[i] + fraction * (HazardFlows[i + 1] - HazardFlows[i]);
            }
        }
        return HazardFlows[HazardFlows.Length - 1];
    }

    /// <summary>A two-knot ramp with flat clamps (the fragility/consequence closed form).</summary>
    /// <param name="x0">The zero knot.</param>
    /// <param name="x1">The saturation knot.</param>
    /// <param name="top">The saturated value.</param>
    /// <param name="x">The evaluation point.</param>
    private static double Ramp(double x0, double x1, double top, double x)
    {
        if (x <= x0) return 0d;
        if (x >= x1) return top;
        return (x - x0) / (x1 - x0) * top;
    }

    /// <summary>The sample mean and standard deviation of a set.</summary>
    /// <param name="values">The samples.</param>
    private static (double Mean, double Sigma) MeanSigma(IReadOnlyList<double> values)
    {
        double mean = 0d;
        for (int i = 0; i < values.Count; i++) mean += values[i];
        mean /= values.Count;
        double sumSquares = 0d;
        for (int i = 0; i < values.Count; i++)
        {
            double delta = values[i] - mean;
            sumSquares += delta * delta;
        }
        return (mean, Math.Sqrt(sumSquares / (values.Count - 1)));
    }

    /// <summary>
    /// Builds a single-component transform-chain analysis: hazard → transform → fragility →
    /// consequence (the consequence binds to the transformed hazard by the default structural
    /// binding).
    /// </summary>
    /// <param name="transform">The flow→stage transform under test.</param>
    /// <param name="label">The scenario label.</param>
    /// <param name="fragility">The stage fragility.</param>
    /// <param name="consequence">The stage consequence.</param>
    private static RiskAnalysis BuildChain(RMC.TotalRisk.Core.Interfaces.ITransformFunction transform, string label,
        TabularResponse fragility, TabularConsequence consequence)
    {
        var component = new SystemComponent { Name = "Levee Reach" };
        component.HazardFunction = FlowFrequency();
        component.AddFailureMode(new FailureMode(
            new List<RMC.TotalRisk.Core.Interfaces.ITransformFunction> { transform },
            null, fragility, consequence));
        var analysis = new RiskAnalysis(new[] { component }) { Name = label };
        analysis.Options.LECOutputLength = 1000;
        return analysis;
    }

    /// <summary>
    /// Runs the flat two-uniform oracle over the chain: u₁ draws the flow from the frequency
    /// ladder, u₂ draws the transform's co-monotonic percentile, and the fragility ramp and
    /// consequence ramp evaluate at the transformed stage.
    /// </summary>
    /// <param name="stage">The stage closed form given (flow, percentile draw).</param>
    /// <param name="fragilityZero">The fragility ramp zero knot.</param>
    /// <param name="fragilityOne">The fragility saturation knot.</param>
    /// <param name="consequenceZero">The consequence ramp zero knot.</param>
    /// <param name="consequenceTop">The consequence saturation knot.</param>
    /// <param name="consequenceValue">The saturated consequence.</param>
    /// <returns>The failure-risk and failure-probability means with their standard errors.</returns>
    private static (double FailMean, double FailSe, double Afp, double AfpSe) RunChainOracle(
        Func<double, double, double> stage, double fragilityZero, double fragilityOne,
        double consequenceZero, double consequenceTop, double consequenceValue)
    {
        var stream = new MersenneTwister(OracleSeed);
        double failM1 = 0d, failM2 = 0d, afpM1 = 0d, afpM2 = 0d;
        for (int i = 0; i < OracleRealizations; i++)
        {
            double flow = DrawFlow(stream.NextDouble());
            double s = stage(flow, stream.NextDouble());
            double pf = Ramp(fragilityZero, fragilityOne, 1d, s);
            double loss = pf * Ramp(consequenceZero, consequenceTop, consequenceValue, s);

            long n = i + 1;
            double deltaFail = loss - failM1;
            failM1 += deltaFail / n;
            failM2 += deltaFail * (loss - failM1);
            double deltaAfp = pf - afpM1;
            afpM1 += deltaAfp / n;
            afpM2 += deltaAfp * (pf - afpM1);
        }
        double failSigma = Math.Sqrt(failM2 / OracleRealizations);
        double afpSigma = Math.Sqrt(afpM2 / OracleRealizations);
        return (failM1, failSigma / Math.Sqrt(OracleRealizations), afpM1, afpSigma / Math.Sqrt(OracleRealizations));
    }

    /// <summary>
    /// Dense trapezoid quadrature of a deterministic chain over the exceedance axis — the exact
    /// reference for the D = 0 (deterministic transform) engine runs.
    /// </summary>
    /// <param name="stage">The deterministic stage closed form given the flow.</param>
    /// <param name="fragilityZero">The fragility ramp zero knot.</param>
    /// <param name="fragilityOne">The fragility saturation knot.</param>
    /// <param name="consequenceZero">The consequence ramp zero knot.</param>
    /// <param name="consequenceTop">The consequence saturation knot.</param>
    /// <param name="consequenceValue">The saturated consequence.</param>
    /// <returns>The failure-risk mean and the annualized failure probability.</returns>
    private static (double FailMean, double Afp) DenseQuadrature(
        Func<double, double> stage, double fragilityZero, double fragilityOne,
        double consequenceZero, double consequenceTop, double consequenceValue)
    {
        const int steps = 400_000;
        double failMean = 0d, afp = 0d;
        double previousFail = 0d, previousPf = 0d;
        double previousP = 0d;
        for (int i = 1; i <= steps; i++)
        {
            double p = (double)i / steps;
            double s = stage(DrawFlow(p));
            double pf = Ramp(fragilityZero, fragilityOne, 1d, s);
            double fail = pf * Ramp(consequenceZero, consequenceTop, consequenceValue, s);
            failMean += 0.5d * (fail + previousFail) * (p - previousP);
            afp += 0.5d * (pf + previousPf) * (p - previousP);
            previousFail = fail;
            previousPf = pf;
            previousP = p;
        }
        return (failMean, afp);
    }

    /// <summary>
    /// Reads the ensemble grand mean and between-realization standard deviation of a
    /// per-realization selector.
    /// </summary>
    /// <param name="analysis">The estimated ensemble analysis.</param>
    /// <param name="selector">The per-realization scalar.</param>
    private static (double GrandMean, double SigmaK) EnsembleGrand(RiskAnalysis analysis, Func<Results.Risk.SystemRiskResults, double> selector)
    {
        var values = new double[analysis.RiskResults!.Count];
        for (int i = 0; i < values.Length; i++) values[i] = selector(analysis.RiskResults[i]!);
        return MeanSigma(values);
    }

    #endregion

    #region SF-8 Builders and the Independent Derivation Oracle

    /// <summary>Builds the SF-8 nonparametric hazard exactly per Figure 16 of the 2024 report.</summary>
    /// <param name="isUncertain">Whether the knowledge-uncertainty derivation is active.</param>
    private static NonparametricHazard BuildSf8(bool isUncertain = true)
    {
        var ordinates = new UncertainOrdinate[Sf8Probabilities.Length];
        for (int i = 0; i < Sf8Probabilities.Length; i++)
        {
            ordinates[i] = new UncertainOrdinate(Sf8Probabilities[i], new Deterministic(Sf8Flows[i]));
        }
        return new NonparametricHazard
        {
            Name = "SF-8 WO Base Yr",
            SpecifiedHazard = "Flow",
            HazardUnit = "cfs",
            IsUncertain = isUncertain,
            EffectiveRecordLength = Sf8RecordLength,
            ExtrapolationEP = 0.0001d,
            InputUncertainFunction = new UncertainOrderedPairedData(ordinates,
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>
    /// The independent re-derivation oracle: the exact legacy object pipeline — extrapolation on
    /// snapshot arrays, the order-statistic standard errors with the 0.99/0.01 pins and both
    /// monotone-smoothing passes, the base-e <c>LogNormal</c> moment mapping, the 1e-16 σ floor,
    /// and the per-ordinate <c>Brent</c> 1%-bound repair (the v1.0 method the shipped class
    /// replaces with the closed form).
    /// </summary>
    /// <returns>The derived (AEP, mean, standard deviation) triples.</returns>
    private static List<(double P, double Mean, double Sd)> DeriveSf8Independently()
    {
        var xVals = new List<double>(Sf8Flows.Length + 2);
        var pVals = new List<double>(Sf8Probabilities.Length + 2);
        for (int i = 0; i < Sf8Flows.Length; i++)
        {
            xVals.Add(Math.Log(Sf8Flows[i]));
            pVals.Add(Sf8Probabilities[i]);
        }

        var lin = new Linear(pVals.ToArray(), xVals.ToArray(), SortOrder.Descending) { XTransform = Transform.NormalZ };
        const double extrapolationEp = 0.0001d;
        bool extendRare = pVals[pVals.Count - 1] > extrapolationEp;
        bool extendFrequent = pVals[0] < 0.999d;
        double rareValue = extendRare ? lin.Extrapolate(extrapolationEp) : 0d;
        double frequentValue = extendFrequent ? lin.Extrapolate(0.999d) : 0d;
        if (extendRare) { pVals.Add(extrapolationEp); xVals.Add(rareValue); }
        if (extendFrequent) { pVals.Insert(0, 0.999d); xVals.Insert(0, frequentValue); }

        var opd = new OrderedPairedData(xVals, pVals, true, SortOrder.Ascending, true, SortOrder.Descending);
        var empDist = new EmpiricalDistribution(opd) { ProbabilityTransform = Transform.NormalZ };
        int n = Sf8RecordLength;
        double se99 = Math.Sqrt((1d - 0.99d) * 0.99d / (n * Math.Pow(empDist.PDF(lin.Interpolate(0.99d)), 2d)));
        double se01 = Math.Sqrt((1d - 0.01d) * 0.01d / (n * Math.Pow(empDist.PDF(lin.Interpolate(0.01d)), 2d)));

        var se = new List<double>(xVals.Count);
        for (int i = 0; i < xVals.Count; i++)
        {
            if (pVals[i] >= 0.99d) se.Add(se99);
            else if (pVals[i] <= 0.01d) se.Add(se01);
            else se.Add(Math.Sqrt((1d - pVals[i]) * pVals[i] / (n * Math.Pow(empDist.PDF(xVals[i]), 2d))));
            if (i > 0 && pVals[i] < 0.5d && se[i] < se[i - 1]) se[i] = se[i - 1];
        }
        for (int i = xVals.Count - 2; i >= 0; i--)
        {
            if (pVals[i] > 0.5d && se[i] < se[i + 1]) se[i] = se[i + 1];
        }

        var results = new List<(double P, double Mean, double Sd)>(xVals.Count);
        LnNormal? previous = null;
        for (int i = 0; i < xVals.Count; i++)
        {
            var logNormal = new LogNormal(xVals[i], se[i]) { Base = Math.E };
            double mean = logNormal.Mean;
            double sd = logNormal.StandardDeviation;
            sd = double.IsNaN(sd) ? 1e-16 : Math.Max(1e-16, sd);

            var current = new LnNormal(mean, sd);
            if (previous is not null)
            {
                double previousLow = previous.InverseCDF(0.01d);
                if (current.InverseCDF(0.01d) < previousLow)
                {
                    double captureMean = mean;
                    sd = Brent.Solve(x => previousLow - new LnNormal(captureMean, x).InverseCDF(0.01d), Numerics.Tools.DoubleMachineEpsilon, sd);
                    sd = double.IsNaN(sd) ? 1e-16 : Math.Max(1e-16, sd);
                    current = new LnNormal(mean, sd);
                }
            }

            results.Add((pVals[i], mean, sd));
            previous = current;
        }
        return results;
    }

    /// <summary>
    /// The independent flow draw over the deterministic SF-8 curve's declared interpolation
    /// semantics: ln-flow linear in z(AEP) between the knots (logarithmic hazard axis, Normal-Z
    /// probability axis), flat clamps beyond the extended ladder.
    /// </summary>
    /// <param name="knots">The (AEP descending, flow ascending) knots.</param>
    /// <param name="u">The uniform exceedance draw.</param>
    private static double DrawSf8Flow(IReadOnlyList<(double P, double X)> knots, double u)
    {
        var z = new Normal(0d, 1d);
        if (u >= knots[0].P) return knots[0].X;
        if (u <= knots[knots.Count - 1].P) return knots[knots.Count - 1].X;
        for (int i = 0; i < knots.Count - 1; i++)
        {
            if (u >= knots[i + 1].P)
            {
                double zU = z.InverseCDF(u);
                double z0 = z.InverseCDF(knots[i].P);
                double z1 = z.InverseCDF(knots[i + 1].P);
                double fraction = (zU - z0) / (z1 - z0);
                return Math.Exp(Math.Log(knots[i].X) + fraction * (Math.Log(knots[i + 1].X) - Math.Log(knots[i].X)));
            }
        }
        return knots[knots.Count - 1].X;
    }

    #endregion

    /// <summary>
    /// V1 — the linear-transform chain: the ensemble grand means against the flat Monte Carlo
    /// oracle, and the deterministic-transform (D = 0) variant against dense quadrature through
    /// both engine paths.
    /// </summary>
    [TestMethod]
    public void Test_LinearTransformChain_EngineVsOracle()
    {
        // Arrange — Y = 2 + 0.005·flow + ε, ε ~ N(0, 0.75): stages span ≈ [4.5, 42].
        LinearTransform Transform(bool uncertain) => new()
        {
            Name = "Flow To Stage",
            SpecifiedHazard = "Flow",
            HazardUnit = "cfs",
            TransformedHazard = "Stage",
            TransformedHazardUnit = "ft",
            Alpha = 2d,
            Beta = 0.005d,
            Sigma = 0.75d,
            IsUncertain = uncertain,
            Minimum = 0d,
            Maximum = 10000d,
        };
        var residual = new Normal(0d, 0.75d);
        var oracle = RunChainOracle(
            (flow, u) => 2d + 0.005d * flow + residual.InverseCDF(u),
            10d, 30d, 10d, 40d, 1000d);

        // Act — the uncertain ensemble run.
        var analysis = BuildChain(Transform(uncertain: true), "Linear chain", StageFragility(10d, 30d), StageConsequence(10d, 40d, 1000d));
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.Realizations = EngineRealizations;
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated);
        var (failGrand, failSigmaK) = EnsembleGrand(analysis, r => r.Fail.Mean);
        var (afpGrand, afpSigmaK) = EnsembleGrand(analysis, r => r.Fail.TotalProbability);

        // Assert — grand means within the combined sampling error (k = 4; engine charged at the
        // Monte Carlo rate).
        double failTolerance = K * Math.Sqrt(oracle.FailSe * oracle.FailSe + failSigmaK * failSigmaK / EngineRealizations);
        double afpTolerance = K * Math.Sqrt(oracle.AfpSe * oracle.AfpSe + afpSigmaK * afpSigmaK / EngineRealizations);
        Assert.AreEqual(oracle.FailMean, failGrand, failTolerance, "Linear chain: the failure-risk grand mean.");
        Assert.AreEqual(oracle.Afp, afpGrand, afpTolerance, "Linear chain: the annualized failure probability grand mean.");

        // The D = 0 proof: the deterministic transform through the mean-only AND ensemble paths
        // against dense quadrature, with bit-identical realizations.
        var quadrature = DenseQuadrature(flow => 2d + 0.005d * flow, 10d, 30d, 10d, 40d, 1000d);
        var meanOnly = BuildChain(Transform(uncertain: false), "Linear chain D0", StageFragility(10d, 30d), StageConsequence(10d, 40d, 1000d));
        meanOnly.RunAsync().GetAwaiter().GetResult();
        Assert.AreEqual(quadrature.FailMean, meanOnly.RiskResults![0]!.Fail.Mean, 1e-4 * quadrature.FailMean,
            "Linear chain D0: the mean-only failure risk against dense quadrature.");
        Assert.AreEqual(quadrature.Afp, meanOnly.RiskResults[0]!.Fail.TotalProbability, 1e-4 * quadrature.Afp,
            "Linear chain D0: the mean-only failure probability against dense quadrature.");

        var ensembleD0 = BuildChain(Transform(uncertain: false), "Linear chain D0 ensemble", StageFragility(10d, 30d), StageConsequence(10d, 40d, 1000d));
        ensembleD0.Options.EstimateMeanRiskOnly = false;
        ensembleD0.Options.Realizations = 100;
        ensembleD0.RunAsync().GetAwaiter().GetResult();
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(ensembleD0.RiskResults![0]!.Fail.Mean),
            BitConverter.DoubleToInt64Bits(ensembleD0.RiskResults[57]!.Fail.Mean),
            "Linear chain D0: a deterministic chain's realizations must be bit-identical (the D = 0 walk).");
        Assert.AreEqual(quadrature.FailMean, ensembleD0.RiskResults[0]!.Fail.Mean, 2e-4 * quadrature.FailMean,
            "Linear chain D0: the ensemble realizations against dense quadrature (relaxed ensemble discipline).");

        Console.WriteLine(
            $"Linear chain: oracle fail {oracle.FailMean:G8} (SE {oracle.FailSe:G3}) vs engine {failGrand:G8}; " +
            $"oracle AFP {oracle.Afp:G8} vs engine {afpGrand:G8}; D0 quadrature {quadrature.FailMean:G8} vs mean-only {meanOnly.RiskResults[0]!.Fail.Mean:G8}");
    }

    /// <summary>
    /// V2 — the power-transform chain: the forward uncertain ensemble against the flat Monte
    /// Carlo oracle, and the deterministic inverse-form variant against dense quadrature.
    /// </summary>
    [TestMethod]
    public void Test_PowerTransformChain_EngineVsOracle()
    {
        // Arrange — forward: Y = 0.05·flow^0.8 · exp(z·0.15): stages span ≈ [7.2, 66].
        var forward = new PowerTransform
        {
            Name = "Flow To Stage",
            SpecifiedHazard = "Flow",
            HazardUnit = "cfs",
            TransformedHazard = "Stage",
            TransformedHazardUnit = "ft",
            Alpha = 0.05d,
            Beta = 0.8d,
            Xi = 0d,
            Sigma = 0.15d,
            IsUncertain = true,
            Minimum = 0d,
            Maximum = 10000d,
        };
        var residual = new Normal(0d, 0.15d);
        var oracle = RunChainOracle(
            (flow, u) => Math.Exp(Math.Log(0.05d) + 0.8d * Math.Log(flow) + residual.InverseCDF(u)),
            10d, 60d, 10d, 70d, 1000d);

        // Act
        var analysis = BuildChain(forward, "Power chain", StageFragility(10d, 60d), StageConsequence(10d, 70d, 1000d));
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.Realizations = EngineRealizations;
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated);
        var (failGrand, failSigmaK) = EnsembleGrand(analysis, r => r.Fail.Mean);
        var (afpGrand, afpSigmaK) = EnsembleGrand(analysis, r => r.Fail.TotalProbability);

        // Assert
        double failTolerance = K * Math.Sqrt(oracle.FailSe * oracle.FailSe + failSigmaK * failSigmaK / EngineRealizations);
        double afpTolerance = K * Math.Sqrt(oracle.AfpSe * oracle.AfpSe + afpSigmaK * afpSigmaK / EngineRealizations);
        Assert.AreEqual(oracle.FailMean, failGrand, failTolerance, "Power chain: the failure-risk grand mean.");
        Assert.AreEqual(oracle.Afp, afpGrand, afpTolerance, "Power chain: the annualized failure probability grand mean.");

        // The deterministic inverse form: stage = sqrt(flow/5) spans [10, 40]; dense quadrature.
        var inverse = new PowerTransform
        {
            Name = "Inverse Rating",
            SpecifiedHazard = "Flow",
            HazardUnit = "cfs",
            TransformedHazard = "Stage",
            TransformedHazardUnit = "ft",
            Alpha = 5d,
            Beta = 2d,
            Xi = 0d,
            IsInverse = true,
            IsUncertain = false,
            Minimum = 1d,
            Maximum = 10000d,
        };
        var quadrature = DenseQuadrature(flow => Math.Exp((Math.Log(flow) - Math.Log(5d)) / 2d), 12d, 35d, 12d, 45d, 500d);
        var meanOnly = BuildChain(inverse, "Inverse power chain", StageFragility(12d, 35d), StageConsequence(12d, 45d, 500d));
        meanOnly.RunAsync().GetAwaiter().GetResult();
        Assert.AreEqual(quadrature.FailMean, meanOnly.RiskResults![0]!.Fail.Mean, 1e-4 * quadrature.FailMean,
            "Inverse power chain: the mean-only failure risk against dense quadrature.");
        Assert.AreEqual(quadrature.Afp, meanOnly.RiskResults[0]!.Fail.TotalProbability, 1e-4 * quadrature.Afp,
            "Inverse power chain: the mean-only failure probability against dense quadrature.");

        Console.WriteLine(
            $"Power chain: oracle fail {oracle.FailMean:G8} (SE {oracle.FailSe:G3}) vs engine {failGrand:G8}; " +
            $"oracle AFP {oracle.Afp:G8} vs engine {afpGrand:G8}; inverse D0 quadrature {quadrature.FailMean:G8} vs engine {meanOnly.RiskResults[0]!.Fail.Mean:G8}");
    }

    /// <summary>
    /// V3 — the SF-8 anchor: the independent legacy-pipeline re-derivation (the
    /// optimization-equivalence oracle), all twenty Table 38 RMC-TotalRisk pins, and the mean
    /// curve's internal consistency.
    /// </summary>
    [TestMethod]
    public void Test_NonparametricHazard_Sf8_Table38()
    {
        // Arrange
        var hazard = BuildSf8();
        var derived = hazard.TrueUncertainFunction;
        var oracle = DeriveSf8Independently();

        // Assert — the derived ladder matches the independent oracle ordinate for ordinate
        // (means at 1e-9 relative — inlined vs object moment mapping; spreads at 1e-6 relative —
        // the closed-form repair against the Brent fixed point).
        Assert.AreEqual(oracle.Count, derived.Count, "The derived ordinate count.");
        for (int i = 0; i < oracle.Count; i++)
        {
            Assert.AreEqual(oracle[i].P, derived[i].X, 0d, $"Ordinate {i}: the AEP ladder.");
            Assert.AreEqual(oracle[i].Mean, derived[i].Y!.Mean, Math.Abs(oracle[i].Mean) * 1e-9,
                $"Ordinate {i}: the derived mean (inlined moment mapping vs the legacy object path).");
            Assert.AreEqual(oracle[i].Sd, derived[i].Y!.StandardDeviation, Math.Abs(oracle[i].Sd) * 1e-6,
                $"Ordinate {i}: the derived spread (closed-form repair vs the legacy Brent fixed point).");
        }

        // Table 38: the ±2 log-standard-deviation quantiles in log10 space against all twenty
        // published RMC-TotalRisk constants (tolerance 5.5e-4 = the table's rounding half-width
        // plus solver headroom).
        var z = new Normal(0d, 1d);
        double pLo = z.CDF(-2d);
        double pHi = z.CDF(2d);
        foreach (var (aep, lo, hi) in Table38)
        {
            int index = -1;
            for (int i = 0; i < derived.Count; i++)
            {
                if (Math.Abs(derived[i].X - aep) < 1e-12) { index = i; break; }
            }
            Assert.IsTrue(index >= 0, $"Table 38: no derived ordinate at AEP {aep}.");
            var y = derived[index].Y!;
            Assert.AreEqual(lo, Math.Log10(y.InverseCDF(pLo)), 5.5e-4, $"Table 38: the −2SD quantile at AEP {aep}.");
            Assert.AreEqual(hi, Math.Log10(y.InverseCDF(pHi)), 5.5e-4, $"Table 38: the +2SD quantile at AEP {aep}.");
        }

        // The uncertain mean curve: strictly ordered and spanning the full-uncertainty hazard
        // envelope — its 200-point grid runs [MinHazard(false), MaxHazard(false)] by
        // construction, deliberately wider than any central percentile curve (internal
        // consistency; the assembly itself is the TabularHazard Hazard-mode pattern
        // verified against exact quadrature by the NFIP dense-tabular family).
        var mean = (EmpiricalDistribution)hazard.SampleFunction();
        for (int i = 1; i < mean.XValues.Count; i++)
        {
            Assert.IsTrue(mean.XValues[i] > mean.XValues[i - 1], "The mean curve must be strictly ordered.");
        }
        Assert.IsTrue(mean.XValues[0] >= hazard.MinHazard(false) - 1e-9, "The mean grid starts at the full-uncertainty lower bound.");
        Assert.IsTrue(mean.XValues[mean.XValues.Count - 1] <= hazard.MaxHazard(false) + 1e-9, "The mean grid ends within the full-uncertainty upper bound.");
        var p05 = (EmpiricalDistribution)hazard.SampleFunction(0.05d);
        var p95 = (EmpiricalDistribution)hazard.SampleFunction(0.95d);
        Assert.IsTrue(mean.XValues[0] <= p05.XValues[0], "The full-envelope grid start sits below the 5% percentile curve's first knot.");
        Assert.IsTrue(mean.XValues[mean.XValues.Count - 1] >= p95.XValues[p95.XValues.Count - 1] - 1e-9,
            "The full-envelope grid end sits at or above the 95% percentile curve's last knot.");

        Console.WriteLine($"SF-8 derived ordinates: {derived.Count}; Table 38 pins: {Table38.Length * 2} green; " +
            $"log10 quantiles at AEP 1e-4: {Math.Log10(derived[derived.Count - 1].Y!.InverseCDF(pLo)):F4}/{Math.Log10(derived[derived.Count - 1].Y!.InverseCDF(pHi)):F4}");
    }

    /// <summary>
    /// V4 — the nonparametric hazard through the reliability engine: the deterministic-mode AFP
    /// against the independent knot-semantics oracle with bit-identity pins, and the
    /// uncertain-mode ensemble grand-mean AFP against a two-loop oracle over the independently
    /// verified derived table.
    /// </summary>
    [TestMethod]
    public void Test_NonparametricHazard_ReliabilityEngine()
    {
        // Arrange — the deterministic-mode SF-8 ladder with a flow fragility ramp.
        RiskAnalysis Build(bool uncertain)
        {
            var component = new SystemComponent { Name = "SF-8 Reach" };
            component.HazardFunction = BuildSf8(uncertain);
            component.AddFailureMode(new FailureMode(null, null, FlowFragility(), null));
            var analysis = new RiskAnalysis(new[] { component }) { Name = "SF-8 reliability" };
            analysis.Options.Mode = RiskAnalysisMode.Reliability;
            return analysis;
        }
        TabularResponse FlowFragility() => new()
        {
            Name = "Flow Fragility",
            SpecifiedHazard = "Flow",
            HazardUnit = "cfs",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(5000d, new Deterministic(0d)), new UncertainOrdinate(30000d, new Deterministic(1d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };

        // The deterministic-mode oracle: the derived knot ladder (inputs + the 1e-4
        // extrapolation) with the declared ln-flow-in-z interpolation semantics.
        var deterministicHazard = BuildSf8(isUncertain: false);
        var knots = new List<(double P, double X)>(deterministicHazard.TrueUncertainFunction.Count);
        for (int i = 0; i < deterministicHazard.TrueUncertainFunction.Count; i++)
        {
            knots.Add((deterministicHazard.TrueUncertainFunction[i].X, deterministicHazard.TrueUncertainFunction[i].Y!.Mean));
        }
        var stream = new MersenneTwister(ReliabilitySeed);
        double oracleAfp = 0d, oracleM2 = 0d;
        for (int i = 0; i < OracleRealizations; i++)
        {
            double pf = Ramp(5000d, 30000d, 1d, DrawSf8Flow(knots, stream.NextDouble()));
            long n = i + 1;
            double delta = pf - oracleAfp;
            oracleAfp += delta / n;
            oracleM2 += delta * (pf - oracleAfp);
        }
        double oracleAfpSe = Math.Sqrt(oracleM2 / OracleRealizations) / Math.Sqrt(OracleRealizations);

        // Act / Assert — the deterministic-mode engine AFP within the oracle's 4·SE.
        var deterministicRun = Build(uncertain: false);
        deterministicRun.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(deterministicRun.IsEstimated);
        double engineAfp = deterministicRun.RiskResults![0]!.Fail.TotalProbability;
        Assert.AreEqual(oracleAfp, engineAfp, K * oracleAfpSe,
            "Deterministic SF-8 reliability: the engine AFP against the independent knot-semantics oracle.");

        // Bit-identity pins on the deterministic-mode scenario (the uncertain mean-curve
        // assembly is deliberately excluded — the documented ExpectedProbabilities parallel-sum
        // ulp nondeterminism).
        var repeat = Build(uncertain: false);
        repeat.RunAsync().GetAwaiter().GetResult();
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(engineAfp),
            BitConverter.DoubleToInt64Bits(repeat.RiskResults![0]!.Fail.TotalProbability),
            "Repeated run: the reliability AFP must be bit-identical.");

        var renamed = Build(uncertain: false);
        renamed.Name = "Renamed SF-8";
        var component0 = renamed.Components[0];
        component0.Name = "Renamed Reach";
        foreach (var function in component0.GetReferencedFunctions())
        {
            function.Name = $"Renamed {function.Name}";
            function.AssignNewId();
        }
        renamed.RunAsync().GetAwaiter().GetResult();
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(engineAfp),
            BitConverter.DoubleToInt64Bits(renamed.RiskResults![0]!.Fail.TotalProbability),
            "Rename: the reliability AFP must be bit-identical.");

        var roundTripped = new RiskAnalysis(new[] { new SystemComponent(deterministicRun.Components[0].ToXElement()) });
        roundTripped.Options.Mode = RiskAnalysisMode.Reliability;
        roundTripped.RunAsync().GetAwaiter().GetResult();
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(engineAfp),
            BitConverter.DoubleToInt64Bits(roundTripped.RiskResults![0]!.Fail.TotalProbability),
            "XML round-trip: the reliability AFP must be bit-identical (the derived table recomputes on load).");

        // The uncertain-mode ensemble grand mean against the two-loop oracle: outer knowledge
        // percentiles over the independently verified derived table, inner dense trapezoid.
        var uncertainRun = Build(uncertain: true);
        uncertainRun.Options.EstimateMeanRiskOnly = false;
        uncertainRun.Options.Realizations = 300;
        uncertainRun.RunAsync().GetAwaiter().GetResult();
        var (grandAfp, sigmaK) = EnsembleGrand(uncertainRun, r => r.Fail.TotalProbability);

        var uncertainHazard = BuildSf8();
        var derivedTable = uncertainHazard.TrueUncertainFunction;
        const int outer = 10_000;
        const int innerSteps = 2000;
        var knowledgeStream = new MersenneTwister(OracleSeed);
        var outerValues = new double[outer];
        var percentileKnots = new List<(double P, double X)>(derivedTable.Count);
        for (int k = 0; k < outer; k++)
        {
            double percentile = knowledgeStream.NextDouble();
            percentileKnots.Clear();
            for (int i = 0; i < derivedTable.Count; i++)
            {
                percentileKnots.Add((derivedTable[i].X, derivedTable[i].Y!.InverseCDF(percentile)));
            }
            double afpK = 0d, previousPf = 0d, previousP = 0d;
            for (int s = 1; s <= innerSteps; s++)
            {
                double p = (double)s / innerSteps;
                double pf = Ramp(5000d, 30000d, 1d, DrawSf8Flow(percentileKnots, p));
                afpK += 0.5d * (pf + previousPf) * (p - previousP);
                previousPf = pf;
                previousP = p;
            }
            outerValues[k] = afpK;
        }
        var (oracleGrand, oracleSigma) = MeanSigma(outerValues);
        double grandTolerance = K * oracleSigma * Math.Sqrt(1d / outer + 1d / 300d);
        Assert.AreEqual(oracleGrand, grandAfp, grandTolerance,
            "Uncertain SF-8 reliability: the ensemble grand-mean AFP against the two-loop oracle.");

        Console.WriteLine(
            $"SF-8 reliability: deterministic oracle {oracleAfp:G8} (SE {oracleAfpSe:G3}) vs engine {engineAfp:G8}; " +
            $"uncertain two-loop oracle {oracleGrand:G8} vs engine grand {grandAfp:G8} (σ_k {sigmaK:G4})");
    }
}
