using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using Numerics.Mathematics.Optimization;
using Numerics.Sampling;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Verification.Analyses.Risk;

/// <summary>
/// Single-component knowledge uncertainty — a family with no legacy counterpart:
/// the full-uncertainty two-loop simulation (outer knowledge realizations, inner risk
/// integral) verified against an independent two-loop oracle whose inner integral is exact.
/// Covers the tabular co-monotonic percentile contract, the fail/non-fail consequence
/// coupling (with a decoupled counter-pin), the ensemble percentile surfaces, Latin
/// hypercube versus Monte Carlo scheme agreement, and the parametric posterior-injection
/// lifecycle (the <c>ParametricResponse</c> verification anchor).
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Scenario A (tabular knowledge uncertainty):</b> hazard tabulated from Normal(100, 20)
/// quantiles on a ±8 z-grid at step 0.25 (deterministic); one failure mode under the
/// competing method (a single mode short-circuits to its raw response probability — the
/// per-mode path that carries the coupling pairing) with an uncertain two-knot fragility
/// (100 → Triangular(0, 0.05, 0.1), 200 → Triangular(0.6, 0.8, 1.0)), an uncertain failure
/// consequence (60 → Triangular(0, 10, 20), 200 → Triangular(400, 1000, 1600)), and an
/// uncertain non-failure consequence (60 → Triangular(0, 5, 10), 200 → Triangular(200, 500,
/// 800) — every ordinate carries the container's distribution type, and the failure knots
/// quantile-dominate the non-failure knots). Every curve knot sits on the hazard
/// grid, so within each grid interval the sampled curves are linear in stage and the stage is
/// linear in probability — the inner risk integrand is piecewise quadratic and the oracle's
/// per-interval Simpson rule integrates it EXACTLY. The oracle's outer loop draws the
/// documented knowledge contract with its own independent streams: one co-monotonic
/// percentile per function per realization; the failure mode's consequence pair (failure and
/// its paired non-failure) shares the mode's coupling percentile; the non-failure
/// mode's own percentile drives the background and non-failure streams.
/// </para>
/// <para>
/// <b>Coupling counter-pin:</b> under quantile dominance the coupled excess never clamps and its
/// ensemble dispersion is the dispersion of the QUANTILE DIFFERENCE — far smaller than the
/// decoupled (independent-percentile) dispersion. The pin asserts the engine's ensemble
/// standard deviation of the excess mean sits at the coupled oracle's value and that the
/// decoupled oracle is well separated (constructed ≳ 1.5×), proving the coupling is real and
/// exercised.
/// </para>
/// <para>
/// <b>Scenario B (parametric posterior injection):</b> the same hazard behind a
/// <c>ParametricResponse</c> whose Normal(140, 30) parent receives a deterministic injected
/// posterior of <see cref="EngineRealizations"/> parameter sets (a fixed formula — no
/// randomness), with deterministic consequences. The engine's full-uncertainty pass looks the posterior up by realization
/// index (the v1.0 D = 0 semantics), so the ensemble is a deterministic walk of the injected
/// sets and the oracle compares REALIZATION FOR REALIZATION — each engine realization mean
/// against the oracle's dense-trapezoid integral of the same parameter set at 0.1% relative
/// (covering the engine's mass-accounting residual and the oracle's own discretization).
/// </para>
/// <para>
/// <b>Tolerances (Scenario A):</b> the oracle's inner integral is exact, so every ensemble
/// comparison is outer-sampling statistics only: grand means at k·√(σ²/N_o + σ²/N_e) with
/// k = 4 (the engine side charged at the Monte Carlo rate — conservative for Latin
/// hypercube); ensemble percentiles at the density-scaled quantile SE combined the same way;
/// the ensemble σ pin at k·(SE_σ,oracle + SE_σ,engine) by the delta method.
/// </para>
/// </remarks>
[TestClass]
public class SingleComponentUncertaintyVerification
{
    /// <summary>The oracle's outer knowledge-realization count.</summary>
    private const int OracleRealizations = 20_000;

    /// <summary>
    /// The engine's knowledge-realization count (Scenario A; also the Scenario B posterior
    /// size). 500 keeps the five full-uncertainty engine runs in this family within the
    /// deliberate-suite runtime budget; every statistical tolerance derives from the counts,
    /// so the asserts scale with it.
    /// </summary>
    private const int EngineRealizations = 500;

    /// <summary>The tolerance multiplier on standard errors.</summary>
    private const double K = 4d;

    /// <summary>The shared z-grid step of the tabulated hazard.</summary>
    private const double ZStep = 0.25d;

    /// <summary>The shared z-grid half-range of the tabulated hazard.</summary>
    private const double ZRange = 8d;

    /// <summary>The oracle stream seed for the fragility knowledge percentiles.</summary>
    private const int FragilitySeed = 12345;

    /// <summary>The oracle stream seed for the failure mode's coupling percentiles.</summary>
    private const int CouplingSeed = 45678;

    /// <summary>The oracle stream seed for the non-failure mode's own percentiles.</summary>
    private const int NonFailureSeed = 78910;

    /// <summary>The oracle stream seed for the decoupled counter-pin's independent non-failure pairing draws.</summary>
    private const int DecoupledSeed = 13579;

    /// <summary>The consequence probe for the percentile loss-exceedance curve asserts.</summary>
    private const double ProbeConsequence = 300d;

    #region Scenario A — Builders

    /// <summary>Builds the hazard table: non-exceedance probabilities (ascending) and stages from Normal(100, 20).</summary>
    private static (double[] Probabilities, double[] Stages) HazardTable()
    {
        int count = (int)Math.Round(2d * ZRange / ZStep) + 1;
        var probabilities = new double[count];
        var stages = new double[count];
        for (int i = 0; i < count; i++)
        {
            double z = -ZRange + i * ZStep;
            probabilities[i] = Normal.StandardCDF(z);
            stages[i] = 100d + 20d * z;
        }
        return (probabilities, stages);
    }

    /// <summary>The fragility knot stages (on the hazard grid).</summary>
    private static readonly double[] FragilityStages = { 100d, 200d };

    /// <summary>The fragility knot distributions (ordered supports keep co-monotonic samples monotone).</summary>
    private static Triangular[] FragilityDistributions() => new[] { new Triangular(0d, 0.05d, 0.1d), new Triangular(0.6d, 0.8d, 1d) };

    /// <summary>The consequence knot stages (on the hazard grid).</summary>
    private static readonly double[] ConsequenceStages = { 60d, 200d };

    /// <summary>The failure-consequence low-knot distribution (every ordinate must carry the container's distribution type).</summary>
    private static Triangular FailureLow() => new Triangular(0d, 10d, 20d);

    /// <summary>The failure-consequence top-knot distribution.</summary>
    private static Triangular FailureTop() => new Triangular(400d, 1000d, 1600d);

    /// <summary>The non-failure-consequence low-knot distribution (quantile-dominated by the failure low knot).</summary>
    private static Triangular NonFailureLow() => new Triangular(0d, 5d, 10d);

    /// <summary>The non-failure-consequence top-knot distribution (quantile-dominated by the failure top).</summary>
    private static Triangular NonFailureTop() => new Triangular(200d, 500d, 800d);

    /// <summary>Builds the Scenario A engine analysis.</summary>
    /// <param name="scheme">The knowledge sampling scheme.</param>
    private static RiskAnalysis BuildScenarioA(SamplingScheme scheme)
    {
        var (hazardProbabilities, hazardStages) = HazardTable();
        var hazardOrdinates = new UncertainOrdinate[hazardStages.Length];
        for (int i = 0; i < hazardStages.Length; i++)
        {
            hazardOrdinates[i] = new UncertainOrdinate(1d - hazardProbabilities[i], new Deterministic(hazardStages[i]));
        }
        var hazard = new TabularHazard
        {
            Name = "Stage Frequency",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            ProbabilityTransform = Transform.None,
            NoUncertaintyFunction = new UncertainOrderedPairedData(hazardOrdinates,
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
        };

        var fragilityDistributions = FragilityDistributions();
        var fragility = new TabularResponse
        {
            Name = "Uncertain Fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(FragilityStages[0], fragilityDistributions[0]),
                    new UncertainOrdinate(FragilityStages[1], fragilityDistributions[1]),
                },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Triangular),
        };

        var failure = new TabularConsequence
        {
            Name = "Uncertain Failure Loss",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Loss",
            ConsequenceUnit = "lives",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(ConsequenceStages[0], FailureLow()), new UncertainOrdinate(ConsequenceStages[1], FailureTop()) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Triangular),
        };
        var nonFailure = new TabularConsequence
        {
            Name = "Uncertain Non-Failure Loss",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Loss",
            ConsequenceUnit = "lives",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(ConsequenceStages[0], NonFailureLow()), new UncertainOrdinate(ConsequenceStages[1], NonFailureTop()) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Triangular),
        };

        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = hazard;
        component.AddFailureMode(new FailureMode(null, null, fragility, failure));
        component.AddFailureMode(new FailureMode(null, null, null, nonFailure));
        // The single-mode competing path short-circuits to the raw response probability — the
        // per-mode accounting that carries the fail/non-fail consequence pairing.
        component.FailureModeMethod = FailureModeMethod.CompetingFailures;

        var analysis = new RiskAnalysis(new[] { component }) { Name = "Uncertainty A" };
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.Realizations = EngineRealizations;
        analysis.Options.SamplingScheme = scheme;
        analysis.Options.LECOutputLength = 1000;
        // Discipline pin: an earlier in-test 1e-6 relaxation never took effect —
        // UseDefaults stayed true, so the run reset every integration knob to the 1e-8 defaults
        // before integrating. The family's captured literals therefore reflect 1e-8/MinDepth-2
        // discipline on BOTH passes, and the pins below keep exactly that (the engine's relaxed
        // ensemble default is exercised by the MultiConsequence ensemble family instead).
        analysis.Options.UseDefaults = false;
        analysis.Options.Tolerance = 1e-8;
        analysis.Options.EnsembleTolerance = 1e-8;
        analysis.Options.EnsembleMinDepth = 2;
        return analysis;
    }

    #endregion

    #region Scenario A — Oracle

    /// <summary>One knowledge realization's exactly integrated outputs.</summary>
    private readonly struct RealizationOutputs
    {
        /// <summary>Initializes the outputs.</summary>
        /// <param name="fail">The failure risk mean.</param>
        /// <param name="excess">The excess risk mean (coupled pairing).</param>
        /// <param name="excessDecoupled">The excess risk mean under the decoupled counter-pin pairing.</param>
        /// <param name="background">The background risk mean.</param>
        /// <param name="nonFailure">The non-failure risk mean.</param>
        /// <param name="exceedance">The failure loss-exceedance ordinate at the probe consequence.</param>
        public RealizationOutputs(double fail, double excess, double excessDecoupled, double background, double nonFailure, double exceedance)
        {
            Fail = fail;
            Excess = excess;
            ExcessDecoupled = excessDecoupled;
            Background = background;
            NonFailure = nonFailure;
            Exceedance = exceedance;
        }

        /// <summary>The failure risk mean.</summary>
        public double Fail { get; }

        /// <summary>The excess risk mean (coupled pairing).</summary>
        public double Excess { get; }

        /// <summary>The excess risk mean under the decoupled pairing.</summary>
        public double ExcessDecoupled { get; }

        /// <summary>The background risk mean.</summary>
        public double Background { get; }

        /// <summary>The non-failure risk mean.</summary>
        public double NonFailure { get; }

        /// <summary>The failure loss-exceedance ordinate at the probe consequence.</summary>
        public double Exceedance { get; }
    }

    /// <summary>Linear interpolation over two knots with flat end clamps.</summary>
    /// <param name="x0">The first knot abscissa.</param>
    /// <param name="y0">The first knot value.</param>
    /// <param name="x1">The second knot abscissa.</param>
    /// <param name="y1">The second knot value.</param>
    /// <param name="x">The evaluation point.</param>
    private static double TwoKnot(double x0, double y0, double x1, double y1, double x)
    {
        if (x <= x0) return y0;
        if (x >= x1) return y1;
        return y0 + (x - x0) / (x1 - x0) * (y1 - y0);
    }

    /// <summary>
    /// The Scenario A oracle: per knowledge realization, sample the documented percentile
    /// contract from independent streams (fragility u; the failure mode's coupling percentile v
    /// driving BOTH the failure consequence and its paired non-failure; the non-failure mode's
    /// own percentile w; and a decoupled pairing draw for the counter-pin), then integrate the
    /// inner risk exactly — per hazard-grid interval every sampled curve is linear in stage and
    /// the stage is linear in probability, so Simpson's rule per interval is exact for the
    /// piecewise-quadratic integrands. The probe exceedance splits the interval at the
    /// consequence crossing and integrates the response probability exactly on the exceeding
    /// side.
    /// </summary>
    private static RealizationOutputs[] RunOracleA()
    {
        var (hazardProbabilities, hazardStages) = HazardTable();
        var fragilityDistributions = FragilityDistributions();
        var failureLow = FailureLow();
        var failureTop = FailureTop();
        var nonFailureLow = NonFailureLow();
        var nonFailureTop = NonFailureTop();

        var fragilityStream = new MersenneTwister(FragilitySeed);
        var couplingStream = new MersenneTwister(CouplingSeed);
        var nonFailureStream = new MersenneTwister(NonFailureSeed);
        var decoupledStream = new MersenneTwister(DecoupledSeed);

        var results = new RealizationOutputs[OracleRealizations];
        for (int r = 0; r < OracleRealizations; r++)
        {
            double u = fragilityStream.NextDouble();
            double v = couplingStream.NextDouble();
            double w = nonFailureStream.NextDouble();
            double vDecoupled = decoupledStream.NextDouble();

            double fragilityLow = fragilityDistributions[0].InverseCDF(u);
            double fragilityHigh = fragilityDistributions[1].InverseCDF(u);
            double failLowValue = failureLow.InverseCDF(v);
            double failTop = failureTop.InverseCDF(v);
            double pairedNonFailLow = nonFailureLow.InverseCDF(v);
            double pairedNonFailTop = nonFailureTop.InverseCDF(v);
            double decoupledNonFailLow = nonFailureLow.InverseCDF(vDecoupled);
            double decoupledNonFailTop = nonFailureTop.InverseCDF(vDecoupled);
            double ownNonFailLow = nonFailureLow.InverseCDF(w);
            double ownNonFailTop = nonFailureTop.InverseCDF(w);

            double SampledFragility(double h) => Math.Max(0d, Math.Min(1d, TwoKnot(FragilityStages[0], fragilityLow, FragilityStages[1], fragilityHigh, h)));
            double FailureAt(double h) => TwoKnot(ConsequenceStages[0], failLowValue, ConsequenceStages[1], failTop, h);
            double PairedNonFailureAt(double h) => TwoKnot(ConsequenceStages[0], pairedNonFailLow, ConsequenceStages[1], pairedNonFailTop, h);
            double DecoupledNonFailureAt(double h) => TwoKnot(ConsequenceStages[0], decoupledNonFailLow, ConsequenceStages[1], decoupledNonFailTop, h);
            double OwnNonFailureAt(double h) => TwoKnot(ConsequenceStages[0], ownNonFailLow, ConsequenceStages[1], ownNonFailTop, h);

            double fail = 0d, excess = 0d, excessDecoupled = 0d, background = 0d, nonFailure = 0d, exceedance = 0d;
            for (int i = 0; i < hazardStages.Length - 1; i++)
            {
                double pLow = hazardProbabilities[i];
                double pHigh = hazardProbabilities[i + 1];
                double width = pHigh - pLow;
                if (width <= 0d) continue;
                double hLow = hazardStages[i];
                double hHigh = hazardStages[i + 1];
                double hMid = 0.5d * (hLow + hHigh);

                double Simpson(Func<double, double> g) => width / 6d * (g(hLow) + 4d * g(hMid) + g(hHigh));

                fail += Simpson(h => SampledFragility(h) * FailureAt(h));
                excess += Simpson(h => SampledFragility(h) * Math.Max(0d, FailureAt(h) - PairedNonFailureAt(h)));
                excessDecoupled += Simpson(h => SampledFragility(h) * Math.Max(0d, FailureAt(h) - DecoupledNonFailureAt(h)));
                background += Simpson(OwnNonFailureAt);
                nonFailure += Simpson(h => (1d - SampledFragility(h)) * OwnNonFailureAt(h));

                // The probe exceedance: the failure consequence is linear on the interval, so it
                // crosses the probe at most once — integrate the response probability exactly on
                // the exceeding side (the fragility is linear there, trapezoid-exact in stage,
                // and the stage is linear in probability).
                double fLow = FailureAt(hLow);
                double fHigh = FailureAt(hHigh);
                if (fLow >= ProbeConsequence && fHigh >= ProbeConsequence)
                {
                    exceedance += width / 2d * (SampledFragility(hLow) + SampledFragility(hHigh));
                }
                else if (fHigh > ProbeConsequence)
                {
                    double fraction = (ProbeConsequence - fLow) / (fHigh - fLow);
                    double hCross = hLow + fraction * (hHigh - hLow);
                    double subWidth = width * (1d - fraction);
                    exceedance += subWidth / 2d * (SampledFragility(hCross) + SampledFragility(hHigh));
                }
            }

            results[r] = new RealizationOutputs(fail, excess, excessDecoupled, background, nonFailure, exceedance);
        }
        return results;
    }

    /// <summary>Computes the sample mean and standard deviation of a selected output.</summary>
    /// <param name="values">The per-realization values.</param>
    private static (double Mean, double Sigma) MeanSigma(IReadOnlyList<double> values)
    {
        double mean = 0d;
        for (int i = 0; i < values.Count; i++) mean += values[i];
        mean /= values.Count;
        double m2 = 0d;
        for (int i = 0; i < values.Count; i++)
        {
            double delta = values[i] - mean;
            m2 += delta * delta;
        }
        return (mean, Math.Sqrt(m2 / values.Count));
    }

    #endregion

    /// <summary>
    /// The Scenario A ensemble pins: grand means for the failure, excess, background, and
    /// non-failure streams; the deterministic mean-only linearity cross-check; the ensemble
    /// 5th/95th percentiles of the per-realization failure mean; and the 90% confidence
    /// loss-exceedance band at the probe consequence against the oracle's per-realization
    /// exceedance percentiles.
    /// </summary>
    [TestMethod]
    public void Test_ScenarioA_Ensemble_VsTwoLoopOracle()
    {
        // Arrange — the oracle ensemble and its summary statistics.
        var oracle = RunOracleA();
        var failValues = new double[OracleRealizations];
        var excessValues = new double[OracleRealizations];
        var backgroundValues = new double[OracleRealizations];
        var nonFailureValues = new double[OracleRealizations];
        var exceedanceValues = new double[OracleRealizations];
        for (int r = 0; r < OracleRealizations; r++)
        {
            failValues[r] = oracle[r].Fail;
            excessValues[r] = oracle[r].Excess;
            backgroundValues[r] = oracle[r].Background;
            nonFailureValues[r] = oracle[r].NonFailure;
            exceedanceValues[r] = oracle[r].Exceedance;
        }

        // Act — the engine's full-uncertainty ensemble (Latin hypercube).
        var analysis = BuildScenarioA(SamplingScheme.LatinHypercube);
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated);
        var ensemble = analysis.RiskResults!;
        Assert.AreEqual(EngineRealizations, ensemble.Count, "The ensemble must carry one entry per knowledge realization.");

        var engineFail = new double[EngineRealizations];
        double engineExcessGrand = 0d, engineBackgroundGrand = 0d, engineNonFailureGrand = 0d;
        for (int i = 0; i < EngineRealizations; i++)
        {
            var realization = ensemble[i]!;
            engineFail[i] = realization.Fail.Mean;
            engineExcessGrand += realization.Excess.Mean;
            engineBackgroundGrand += realization.Background.Mean;
            engineNonFailureGrand += realization.NonFail.Mean;
        }
        engineExcessGrand /= EngineRealizations;
        engineBackgroundGrand /= EngineRealizations;
        engineNonFailureGrand /= EngineRealizations;
        double engineFailGrand = 0d;
        for (int i = 0; i < EngineRealizations; i++) engineFailGrand += engineFail[i];
        engineFailGrand /= EngineRealizations;

        // Assert — grand means at the combined outer-sampling error (engine side charged at the
        // Monte Carlo rate; Latin hypercube is tighter, so the tolerance is conservative).
        double Combined(double sigma) => K * sigma * Math.Sqrt(1d / OracleRealizations + 1d / EngineRealizations);
        var (oracleFailGrand, oracleFailSigma) = MeanSigma(failValues);
        var (oracleExcessGrand, oracleExcessSigma) = MeanSigma(excessValues);
        var (oracleBackgroundGrand, oracleBackgroundSigma) = MeanSigma(backgroundValues);
        var (oracleNonFailureGrand, oracleNonFailureSigma) = MeanSigma(nonFailureValues);
        Assert.AreEqual(oracleFailGrand, engineFailGrand, Combined(oracleFailSigma), "Grand mean of the failure risk.");
        Assert.AreEqual(oracleExcessGrand, engineExcessGrand, Combined(oracleExcessSigma), "Grand mean of the excess risk (coupling-paired).");
        Assert.AreEqual(oracleBackgroundGrand, engineBackgroundGrand, Combined(oracleBackgroundSigma), "Grand mean of the background risk.");
        Assert.AreEqual(oracleNonFailureGrand, engineNonFailureGrand, Combined(oracleNonFailureSigma), "Grand mean of the non-failure risk.");

        // The deterministic mean-only pass is the expectation of the linear ensemble (the
        // coupled excess never clamps under quantile dominance), so the grand mean must sit on
        // it within the engine's own outer-sampling error.
        var meanOnly = BuildScenarioA(SamplingScheme.LatinHypercube);
        meanOnly.Options.EstimateMeanRiskOnly = true;
        meanOnly.RunAsync().GetAwaiter().GetResult();
        double meanOnlyFail = meanOnly.RiskResults![0]!.Fail.Mean;
        Assert.AreEqual(meanOnlyFail, engineFailGrand, K * oracleFailSigma / Math.Sqrt(EngineRealizations),
            "The ensemble grand mean must reproduce the mean-only answer (linearity of the expectation).");

        // Ensemble percentiles of the per-realization failure mean (5th/95th at the combined
        // density-scaled quantile error).
        Array.Sort(failValues);
        Array.Sort(engineFail);
        foreach (double percentile in new[] { 0.05d, 0.95d })
        {
            double oracleQuantile = failValues[(int)Math.Round(percentile * (OracleRealizations - 1))];
            double engineQuantile = engineFail[(int)Math.Round(percentile * (EngineRealizations - 1))];
            double bandLow = failValues[(int)Math.Round(Math.Max(0d, percentile - 0.01d) * (OracleRealizations - 1))];
            double bandHigh = failValues[(int)Math.Round(Math.Min(1d, percentile + 0.01d) * (OracleRealizations - 1))];
            double density = 0.02d / Math.Max(1e-12, bandHigh - bandLow);
            double quantileSe = Math.Sqrt(percentile * (1d - percentile)) * Math.Sqrt(1d / OracleRealizations + 1d / EngineRealizations) / density;
            Assert.AreEqual(oracleQuantile, engineQuantile, K * quantileSe,
                $"Ensemble {percentile:P0} percentile of the failure mean.");
        }

        // The 90% confidence loss-exceedance band at the probe consequence: the engine's
        // lower/upper percentile curves against the oracle's 5th/95th percentiles of the
        // per-realization exceedance (log-log output interpolation absorbed by the quantile
        // error at these scales).
        Array.Sort(exceedanceValues);
        double lower = analysis.LowerRiskResults!.Curves.Fail.LEC.GetYFromX(ProbeConsequence, Transform.Logarithmic, Transform.Logarithmic);
        double upper = analysis.UpperRiskResults!.Curves.Fail.LEC.GetYFromX(ProbeConsequence, Transform.Logarithmic, Transform.Logarithmic);
        foreach (var (percentile, engineValue, label) in new[] { (0.05d, lower, "lower"), (0.95d, upper, "upper") })
        {
            double oracleQuantile = exceedanceValues[(int)Math.Round(percentile * (OracleRealizations - 1))];
            double bandLow = exceedanceValues[(int)Math.Round(Math.Max(0d, percentile - 0.01d) * (OracleRealizations - 1))];
            double bandHigh = exceedanceValues[(int)Math.Round(Math.Min(1d, percentile + 0.01d) * (OracleRealizations - 1))];
            double density = 0.02d / Math.Max(1e-12, bandHigh - bandLow);
            double quantileSe = Math.Sqrt(percentile * (1d - percentile)) * Math.Sqrt(1d / OracleRealizations + 1d / EngineRealizations) / density;
            Assert.AreEqual(oracleQuantile, engineValue, K * quantileSe,
                $"The {label} 90% confidence exceedance at consequence {ProbeConsequence}.");
        }

        Console.WriteLine(
            $"Scenario A: grand fail {oracleFailGrand:G6}/{engineFailGrand:G6}, excess {oracleExcessGrand:G6}/{engineExcessGrand:G6}, " +
            $"background {oracleBackgroundGrand:G6}/{engineBackgroundGrand:G6}, nonfail {oracleNonFailureGrand:G6}/{engineNonFailureGrand:G6}, " +
            $"mean-only fail {meanOnlyFail:G6}, CI band at {ProbeConsequence}: [{lower:G4}, {upper:G4}] (oracle/engine)");
    }

    /// <summary>
    /// The consequence-coupling pin and its counter-pin: the engine's ensemble dispersion of the excess
    /// mean must sit at the COUPLED oracle's dispersion (the failure and paired non-failure
    /// consequences share the mode's knowledge percentile), and the decoupled oracle must be
    /// well separated — proving both that the coupling is exercised and that the test could
    /// detect its loss.
    /// </summary>
    [TestMethod]
    public void Test_ScenarioA_QnCoupling_DispersionPinAndCounterPin()
    {
        // Arrange
        var oracle = RunOracleA();
        var coupled = new double[OracleRealizations];
        var decoupled = new double[OracleRealizations];
        for (int r = 0; r < OracleRealizations; r++)
        {
            coupled[r] = oracle[r].Excess;
            decoupled[r] = oracle[r].ExcessDecoupled;
        }
        var (_, coupledSigma) = MeanSigma(coupled);
        var (_, decoupledSigma) = MeanSigma(decoupled);

        // Act
        var analysis = BuildScenarioA(SamplingScheme.LatinHypercube);
        analysis.RunAsync().GetAwaiter().GetResult();
        var excessMeans = new double[EngineRealizations];
        for (int i = 0; i < EngineRealizations; i++) excessMeans[i] = analysis.RiskResults![i]!.Excess.Mean;
        var (_, engineSigma) = MeanSigma(excessMeans);

        // Assert — the construction separates the hypotheses decisively, and the engine sits on
        // the coupled side. σ standard errors ≈ σ/√(2N) (near-Gaussian ensemble outputs).
        Assert.IsTrue(decoupledSigma > 1.3d * coupledSigma,
            $"Construction sanity: the decoupled dispersion ({decoupledSigma:G4}) must clearly exceed the coupled ({coupledSigma:G4}).");
        double tolerance = K * (coupledSigma / Math.Sqrt(2d * OracleRealizations) + engineSigma / Math.Sqrt(2d * EngineRealizations));
        Assert.AreEqual(coupledSigma, engineSigma, tolerance,
            "The engine's excess dispersion must match the coupled oracle.");
        Assert.IsTrue(Math.Abs(engineSigma - decoupledSigma) > 4d * tolerance,
            $"The engine's excess dispersion ({engineSigma:G4}) must reject the decoupled hypothesis ({decoupledSigma:G4}).");

        Console.WriteLine($"Coupling dispersion: coupled {coupledSigma:G6}, engine {engineSigma:G6}, decoupled {decoupledSigma:G6}");
    }

    /// <summary>
    /// Scheme agreement: the Monte Carlo knowledge-sampling scheme reproduces the Latin
    /// hypercube grand mean within the combined outer-sampling error (variance-reduction
    /// quantification is the LHS family's job; this pins that the scheme option changes efficiency,
    /// not the estimand).
    /// </summary>
    [TestMethod]
    public void Test_ScenarioA_SchemeAgreement_LhsVsMc()
    {
        // Arrange / Act
        var lhs = BuildScenarioA(SamplingScheme.LatinHypercube);
        lhs.RunAsync().GetAwaiter().GetResult();
        var mc = BuildScenarioA(SamplingScheme.MonteCarlo);
        mc.RunAsync().GetAwaiter().GetResult();

        double lhsGrand = 0d, mcGrand = 0d;
        var spread = new double[EngineRealizations];
        for (int i = 0; i < EngineRealizations; i++)
        {
            lhsGrand += lhs.RiskResults![i]!.Total.Mean;
            spread[i] = mc.RiskResults![i]!.Total.Mean;
            mcGrand += spread[i];
        }
        lhsGrand /= EngineRealizations;
        mcGrand /= EngineRealizations;
        var (_, mcSigma) = MeanSigma(spread);

        // Assert — two estimates of the same expectation.
        Assert.AreEqual(lhsGrand, mcGrand, K * mcSigma * Math.Sqrt(2d / EngineRealizations),
            "Latin hypercube and Monte Carlo must estimate the same grand mean.");
    }

    #region Scenario B — Posterior Injection

    /// <summary>Builds the deterministic injected posterior: 1,000 Normal (mean, sd) parameter sets from a fixed formula.</summary>
    private static List<ParameterSet> InjectedPosterior()
    {
        var sets = new List<ParameterSet>(EngineRealizations);
        for (int i = 0; i < EngineRealizations; i++)
        {
            double mean = 130d + 20d * (i + 0.5d) / EngineRealizations;
            double sd = 25d + 10d * ((i * 37) % EngineRealizations) / EngineRealizations;
            sets.Add(new ParameterSet(new[] { mean, sd }, 0d));
        }
        return sets;
    }

    /// <summary>Builds the Scenario B engine analysis: the injected-posterior parametric response with deterministic consequences.</summary>
    private static RiskAnalysis BuildScenarioB()
    {
        var (hazardProbabilities, hazardStages) = HazardTable();
        var hazardOrdinates = new UncertainOrdinate[hazardStages.Length];
        for (int i = 0; i < hazardStages.Length; i++)
        {
            hazardOrdinates[i] = new UncertainOrdinate(1d - hazardProbabilities[i], new Deterministic(hazardStages[i]));
        }
        var hazard = new TabularHazard
        {
            Name = "Stage Frequency",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            ProbabilityTransform = Transform.None,
            NoUncertaintyFunction = new UncertainOrderedPairedData(hazardOrdinates,
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
        };

        var response = new ParametricResponse
        {
            Name = "Injected Posterior Fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            ParentDistribution = new Normal(140d, 30d),
        };
        response.Estimate(InjectedPosterior());

        var failure = new TabularConsequence
        {
            Name = "Failure Loss",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Loss",
            ConsequenceUnit = "lives",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(60d, new Deterministic(0d)), new UncertainOrdinate(200d, new Deterministic(1000d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
        var nonFailure = new TabularConsequence
        {
            Name = "Non-Failure Loss",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Loss",
            ConsequenceUnit = "lives",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(60d, new Deterministic(0d)), new UncertainOrdinate(200d, new Deterministic(100d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };

        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = hazard;
        component.AddFailureMode(new FailureMode(null, null, response, failure));
        component.AddFailureMode(new FailureMode(null, null, null, nonFailure));

        var analysis = new RiskAnalysis(new[] { component }) { Name = "Uncertainty B" };
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.Realizations = EngineRealizations;
        // Discipline pin: as in Scenario A, the earlier 1e-6 relaxation was reset by
        // UseDefaults at run time, so the realization-for-realization posterior-injection
        // anchor was captured at 1e-8/MinDepth-2 — pinned explicitly here.
        analysis.Options.UseDefaults = false;
        analysis.Options.Tolerance = 1e-8;
        analysis.Options.EnsembleTolerance = 1e-8;
        analysis.Options.EnsembleMinDepth = 2;
        return analysis;
    }

    /// <summary>Evaluates the failure consequence curve of Scenario B.</summary>
    /// <param name="hazard">The stage.</param>
    private static double FailureConsequenceB(double hazard) => TwoKnot(60d, 0d, 200d, 1000d, hazard);

    /// <summary>
    /// The Scenario B oracle: for one injected parameter set, the failure risk mean by dense
    /// trapezoid integration over probability (20,000 uniform steps; the smooth Normal-CDF
    /// fragility bounds the discretization well below the 0.1% comparison tolerance).
    /// </summary>
    /// <param name="mean">The set's Normal mean.</param>
    /// <param name="sd">The set's Normal standard deviation.</param>
    /// <param name="hazardProbabilities">The shared hazard table probabilities.</param>
    /// <param name="hazardStages">The shared hazard table stages.</param>
    private static double OracleFailMeanB(double mean, double sd, double[] hazardProbabilities, double[] hazardStages)
    {
        const int steps = 20_000;
        double sum = 0d;
        double previous = 0d;
        for (int s = 0; s <= steps; s++)
        {
            double p = (double)s / steps;
            double h;
            if (p <= hazardProbabilities[0]) h = hazardStages[0];
            else if (p >= hazardProbabilities[hazardProbabilities.Length - 1]) h = hazardStages[hazardStages.Length - 1];
            else
            {
                int index = Array.BinarySearch(hazardProbabilities, p);
                if (index < 0) index = ~index;
                double fraction = (p - hazardProbabilities[index - 1]) / (hazardProbabilities[index] - hazardProbabilities[index - 1]);
                h = hazardStages[index - 1] + fraction * (hazardStages[index] - hazardStages[index - 1]);
            }
            double value = Normal.StandardCDF((h - mean) / sd) * FailureConsequenceB(h);
            if (s > 0) sum += (value + previous) / 2d / steps;
            previous = value;
        }
        return sum;
    }

    /// <summary>
    /// The Scenario B pins: the engine's full-uncertainty pass walks the injected posterior by
    /// realization index (the v1.0 D = 0 lookup), so every engine realization is compared
    /// DIRECTLY to the oracle's integral of the same parameter set at 0.1% relative (the
    /// engine's mass-accounting residual plus the oracle's trapezoid discretization), and the
    /// grand mean follows. This is the <c>ParametricResponse</c> posterior-injection
    /// verification anchor.
    /// </summary>
    [TestMethod]
    public void Test_ScenarioB_InjectedPosterior_RealizationForRealization()
    {
        // Arrange
        var (hazardProbabilities, hazardStages) = HazardTable();
        var sets = InjectedPosterior();

        // Act
        var analysis = BuildScenarioB();
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated);
        Assert.AreEqual(EngineRealizations, analysis.RiskResults!.Count);

        // Assert — realization for realization against the matching parameter set.
        double engineGrand = 0d, oracleGrand = 0d;
        for (int i = 0; i < EngineRealizations; i++)
        {
            double engineValue = analysis.RiskResults[i]!.Fail.Mean;
            double oracleValue = OracleFailMeanB(sets[i].Values[0], sets[i].Values[1], hazardProbabilities, hazardStages);
            Assert.AreEqual(oracleValue, engineValue, 1e-3 * oracleValue,
                $"Realization {i} must integrate the injected posterior set ({sets[i].Values[0]:G6}, {sets[i].Values[1]:G6}).");
            engineGrand += engineValue;
            oracleGrand += oracleValue;
        }
        engineGrand /= EngineRealizations;
        oracleGrand /= EngineRealizations;
        Assert.AreEqual(oracleGrand, engineGrand, 1e-3 * oracleGrand, "The posterior grand mean.");

        // The mean-only pass reads the posterior MEAN CURVE (the v1.0 quantile-space mean), so
        // its answer legitimately differs from the ensemble grand mean by the Jensen gap —
        // recorded for the results page, not asserted equal.
        var meanOnly = BuildScenarioB();
        meanOnly.Options.EstimateMeanRiskOnly = true;
        meanOnly.RunAsync().GetAwaiter().GetResult();
        Console.WriteLine(
            $"Scenario B: grand fail {oracleGrand:G8}/{engineGrand:G8} (oracle/engine), mean-only (posterior mean curve) {meanOnly.RiskResults![0]!.Fail.Mean:G8}");
    }

    #endregion
}
