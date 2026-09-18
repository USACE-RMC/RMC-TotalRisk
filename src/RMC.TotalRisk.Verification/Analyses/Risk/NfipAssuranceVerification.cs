using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics;
using Numerics.Data;
using Numerics.Distributions;
using Numerics.Mathematics.Optimization;
using Numerics.Sampling;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Transforms;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Verification.Analyses.Risk;

/// <summary>
/// NFIP assurance — the conversion of the legacy
/// <c>Test_NFIP_Assurance_TOL_50/55/60/65/70</c> annual-probability-of-inundation oracles (LP3 flow
/// frequency → log-interpolated rating transform → prior-to-overtopping fragility → API), the
/// 2024 verification report's Table 104 pins, and the full-uncertainty assurance computation
/// the technical report's NFIP appendix specifies (the probability that the target annual
/// exceedance probability is contained, evaluated over the knowledge-uncertainty ensemble).
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>The API model</b> (technical report Appendix I, equations 257–260): the annual
/// probability of inundation is the union of levee failure and overtopping-without-failure,
/// API = ∫ f(h)·P_F(h) dh + ∫_{h &gt; h_T} f(h)·(1 − P_F(h)) dh. In the engine that is the
/// component's Fail stream total probability plus the NonFail stream's hazard-threshold
/// exceedance — exactly the surface the v1.0 assurance diagnostic reads. The engine runs in
/// reliability mode (no consequence functions); the component carries the LP3 flow-frequency
/// hazard, the failure mode chains the rating transform (logarithmic flow interpolation) into
/// the fragility response, a response-free non-failure mode supplies the NonFail stream, and
/// <c>SystemComponent.HazardThreshold</c> carries the top-of-levee threshold expressed in FLOW
/// units — the rating's stage ordinates are the integers 0–100, so each TOL stage maps to an
/// exact rating knot and the flow-space threshold is algebraically identical to the stage-space
/// one for the strictly increasing rating (so the fixture needs no profile-axis remap).
/// </para>
/// <para>
/// <b>Oracle</b> (ported from the legacy bodies at N = 1,000,000; legacy 10M dropped 10× per
/// the conversion policy): <c>MersenneTwister(45678)</c>; per realization q = LP3⁻¹(u), h =
/// rating(q), and the leveed area floods when h &gt; TOL or a second uniform falls below the
/// fragility — the legacy short-circuit draw order preserved (no failure uniform is consumed
/// on an overtopping draw). The oracle interpolates with the same Numerics
/// <see cref="Linear"/> objects the legacy used. An exact quadrature reference (composite
/// Simpson on the flood-probability integral with the overtopping crossing split out)
/// cross-checks oracle and engine against roundoff-level truth.
/// </para>
/// <para>
/// <b>Engine variants:</b> every TOL scenario runs twice — with the exact
/// <see cref="ParametricUnivariateHazard"/> (deterministic LP3, the legacy model) and with a
/// dense z-grid <see cref="TabularHazard"/> tabulation of the same LP3 (normal-z probability
/// interpolation, logarithmic flow interpolation) — anchoring both hazard types plus the
/// tabular transform and response on the same oracle family. The engine fragility drops the
/// legacy tables' redundant trailing 1.0 knots (flat extrapolation reproduces them exactly;
/// the sampled empirical fragility requires strictly increasing ordinates).
/// </para>
/// <para>
/// <b>Assurance</b> (technical report Appendix I; the v1.0 assurance diagnostic): assurance at
/// the 0.01 AEP target is P(API ≤ 0.01) over the knowledge-uncertainty ensemble — the risk
/// analysis must run with full uncertainty. The assurance test injects a deterministic
/// 300-set LP3 parameter posterior into the parametric hazard
/// (<c>Estimate(IList&lt;ParameterSet&gt;)</c> — the BestFit import surface), runs the
/// reliability ensemble, and verifies the engine's per-realization API against an exact
/// per-parameter-set quadrature oracle realization-for-realization (the parametric hazard's
/// D = 0 sampling walks the injected posterior by index), then the ensemble mean and the
/// assurance fraction itself.
/// </para>
/// <para>
/// <b>Tolerances</b> (docs/verification.md): oracle-versus-engine at k·SE with k = 4 and the
/// binomial standard error at N = 10⁶; engine-versus-exact at a documented deterministic
/// allowance of 2e-4 relative plus 1e-6 absolute covering the adaptive integrator tolerance,
/// the quadrature-mesh resolution of the API's threshold-mass term, and the threshold read's
/// log-log interpolation between recorded profile nodes (observed worst case 1.32e-4); the
/// tabular-hazard variant carries an additional 5e-4 relative tabulation allowance (641-knot
/// normal-z grid). Report pins: the oracle against Table 104's Monte Carlo column at the
/// combined 1M/10M binomial error; the engine against Table 104's RMC-TotalRisk column at a
/// 0.5% v1.0-parity band (the report's own engine-versus-MC differences reach 0.3%).
/// </para>
/// </remarks>
[TestClass]
public class NfipAssuranceVerification
{
    /// <summary>The oracle realization count (legacy 10M dropped 10× per the conversion policy).</summary>
    private const int OracleRealizations = 1_000_000;

    /// <summary>The legacy oracle seed.</summary>
    private const int OracleSeed = 45678;

    /// <summary>The tolerance multiplier on the Monte Carlo standard error.</summary>
    private const double K = 4d;

    /// <summary>The NFIP accreditation target annual exceedance probability.</summary>
    private const double TargetAep = 0.01d;

    /// <summary>
    /// The deterministic engine-versus-exact relative allowance (integrator + quadrature-mesh
    /// resolution of the threshold term + profile interpolation). The API is not a pure integral:
    /// its second term is the non-failure mass above the hazard threshold, a discontinuous
    /// functional whose value is resolved only to the spacing of the quadrature nodes that
    /// straddle the threshold crossing. That term therefore moves with the mass partition
    /// independently of the integral's own accuracy — measured worst case 1.32e-4 relative
    /// (TOL 50 parametric, 5.0e-6 absolute), the other scenarios well inside 1e-4.
    /// </summary>
    private const double EngineExactRelative = 2e-4;

    /// <summary>
    /// The per-realization engine-versus-exact allowance of the assurance ensemble: relative
    /// term plus a 2e-6 absolute floor. Wider than the deterministic scenario allowance because
    /// every realization re-samples the hazard, so the worst recorded-mass/profile-interpolation
    /// deviation across the ensemble governs (measured envelope: 2.5e-6 absolute ≈ 2.0e-4
    /// relative at API ≈ 0.0125 across the ensemble).
    /// </summary>
    private const double EnsembleExactRelative = 2e-4;

    /// <summary>The absolute floor of the ensemble per-realization allowance.</summary>
    private const double EnsembleExactAbsolute = 2e-6;

    /// <summary>The additional tabular-hazard tabulation relative allowance (641-knot normal-z grid).</summary>
    private const double TabulationRelative = 5e-4;

    /// <summary>The assurance-ensemble realization count (the injected posterior size).</summary>
    private const int AssuranceRealizations = 300;

    /// <summary>
    /// The number of source-ordered bootstrap distributions used by each active legacy
    /// TOL-60/TOL-65 configuration. The original workbench used one million single-event
    /// draws; this verification instead checks every retained bootstrap distribution against
    /// deterministic conditional quadrature, so 128 distributions exercise the configuration
    /// without substituting Monte Carlo noise for an oracle.
    /// </summary>
    private const int BootstrapRealizations = 128;

    /// <summary>The LP3 mean of log10 flow.</summary>
    private const double Lp3Mean = 4.232d;

    /// <summary>The LP3 standard deviation of log10 flow.</summary>
    private const double Lp3Sd = 0.153d;

    /// <summary>The LP3 skew of log10 flow.</summary>
    private const double Lp3Skew = 0.401d;

    /// <summary>The shared rating stage ordinates (feet): the integers 0–100.</summary>
    private static readonly double[] RatingStages = BuildRatingStages();

    /// <summary>Builds the shared 0–100 stage ordinates.</summary>
    private static double[] BuildRatingStages()
    {
        var stages = new double[101];
        for (int i = 0; i < stages.Length; i++) stages[i] = i;
        return stages;
    }

    #region Legacy Scenario Tables

    /// <summary>The TOL-50 rating flows (cfs), one per stage 0–100 (legacy table, verbatim).</summary>
    private static readonly double[] RatingFlows50 =
    {
        3.29688234826824E-43d, 1.15791363983133d, 7.35229331982632d, 21.6770168763387d, 46.6841526010843d,
        84.6440005546358d, 137.64047757213d, 207.620839703465d, 296.425891796821d, 405.810109228457d,
        537.455902092903d, 692.984195834673d, 873.962555565617d, 1081.91159684518d, 1318.31015742321d,
        1584.59954644885d, 1882.18708987554d, 2212.44912776551d, 2576.73357715433d, 2976.36214525915d,
        3412.63225747517d, 3886.81874996067d, 4400.17536586119d, 4953.93608619596d, 5549.3163203427d,
        6187.51397637637d, 6869.71042787455d, 7597.07139093336d, 8370.74772285584d, 9191.87615214199d,
        10061.5799479249d, 10980.9695357853d, 11951.1430658776d, 12973.186938477d, 14048.1762913644d,
        15177.1754528896d, 16361.2383640663d, 17601.4089726365d, 18898.7216016908d, 20254.2012951293d,
        21668.8641419868d, 23273.9179860605d, 24878.9718301343d, 26484.025674208d, 28089.0795182818d,
        29694.1333623555d, 31299.1872064293d, 32904.2410505031d, 34509.2948945768d, 36114.3487386506d,
        37719.4025827243d, 55478.5535397698d, 59598.4514349212d, 63860.7242090757d, 68261.4344852933d,
        72797.0214909155d, 77464.2411507383d, 82260.1189645211d, 87181.9123308739d, 92227.0799922373d,
        97393.2569428815d, 102678.233593175d, 108079.938295798d, 113596.422560347d, 119225.848441635d,
        124966.477703255d, 130816.662444247d, 136774.836941712d, 142839.510511694d, 149009.261228807d,
        155282.730374801d, 161658.617509597d, 168135.676076877d, 174712.7094711d, 181388.567504775d,
        188162.14322452d, 195032.370032334d, 201998.21907505d, 209058.696870302d, 216212.843141833d,
        223459.72884072d, 230798.454332226d, 238228.147730669d, 245747.963366931d, 253357.080375168d,
        261054.701386906d, 268840.051322133d, 276712.376268195d, 284670.942438367d, 292715.035202874d,
        300843.95818591d, 309057.032422936d, 317353.59557309d, 325733.001182121d, 334194.617991681d,
        342737.829291256d, 351362.032309365d, 360066.637640968d, 368851.068708337d, 377714.761252874d,
        386657.162855601d,
    };

    /// <summary>The TOL-50 fragility stages (legacy table, verbatim).</summary>
    private static readonly double[] FragilityStages50 =
    {
        40d, 40.6d, 41.2d, 41.8d, 42.4d, 43d, 43.6d, 44.2d, 44.8d, 45.4d, 46d,
        46.6d, 47.2d, 47.8d, 48.4d, 49d, 49.6d, 50.2d, 50.8d, 51.4d, 52d,
    };

    /// <summary>The TOL-50 fragility probabilities (legacy table, verbatim).</summary>
    private static readonly double[] FragilityProbabilities50 =
    {
        0d, 0.000187824d, 0.001499953d, 0.005052371d, 0.011949557d, 0.023281257d, 0.040118416d,
        0.06350798d, 0.094466077d, 0.133968916d, 0.182940319d, 0.242234237d, 0.312609493d,
        0.394692099d, 0.488916839d, 0.595432397d, 0.71393827d, 0.843383167d, 0.981350078d, 1d, 1d,
    };

    /// <summary>The TOL-55 rating flows (cfs), one per stage 0–100 (legacy table, verbatim).</summary>
    private static readonly double[] RatingFlows55 =
    {
        3.29688234826824E-43d, 1.15791363983133d, 7.35229331982632d, 21.6770168763387d, 46.6841526010843d,
        84.6440005546358d, 137.64047757213d, 207.620839703465d, 296.425891796821d, 405.810109228457d,
        537.455902092903d, 692.984195834673d, 873.962555565617d, 1081.91159684518d, 1318.31015742321d,
        1584.59954644885d, 1882.18708987554d, 2212.44912776551d, 2576.73357715433d, 2976.36214525915d,
        3412.63225747517d, 3886.81874996067d, 4400.17536586119d, 4953.93608619596d, 5549.3163203427d,
        6187.51397637637d, 6869.71042787455d, 7597.07139093336d, 8370.74772285584d, 9191.87615214199d,
        10061.5799479249d, 10980.9695357853d, 11951.1430658776d, 12973.186938477d, 14048.1762913644d,
        15177.1754528896d, 16361.2383640663d, 17601.4089726365d, 18898.7216016908d, 20254.2012951293d,
        21668.8641419868d, 23273.9179860605d, 24878.9718301343d, 26484.025674208d, 28089.0795182818d,
        29694.1333623555d, 31299.1872064293d, 32904.2410505031d, 34509.2948945768d, 36114.3487386506d,
        37719.4025827243d, 39324.4564267981d, 40929.5102708718d, 42534.5641149456d, 44139.6179590193d,
        45744.6718030931d, 77464.2411507383d, 82260.1189645211d, 87181.9123308739d, 92227.0799922373d,
        97393.2569428815d, 102678.233593175d, 108079.938295798d, 113596.422560347d, 119225.848441635d,
        124966.477703255d, 130816.662444247d, 136774.836941712d, 142839.510511694d, 149009.261228807d,
        155282.730374801d, 161658.617509597d, 168135.676076877d, 174712.7094711d, 181388.567504775d,
        188162.14322452d, 195032.370032334d, 201998.21907505d, 209058.696870302d, 216212.843141833d,
        223459.72884072d, 230798.454332226d, 238228.147730669d, 245747.963366931d, 253357.080375168d,
        261054.701386906d, 268840.051322133d, 276712.376268195d, 284670.942438367d, 292715.035202874d,
        300843.95818591d, 309057.032422936d, 317353.59557309d, 325733.001182121d, 334194.617991681d,
        342737.829291256d, 351362.032309365d, 360066.637640968d, 368851.068708337d, 377714.761252874d,
        386657.162855601d,
    };

    /// <summary>The TOL-55 fragility stages (legacy table, verbatim).</summary>
    private static readonly double[] FragilityStages55 =
    {
        40d, 40.85d, 41.7d, 42.55d, 43.4d, 44.25d, 45.1d, 45.95d, 46.8d, 47.65d, 48.5d,
        49.35d, 50.2d, 51.05d, 51.9d, 52.75d, 53.6d, 54.45d, 55.3d, 56.15d, 57d,
    };

    /// <summary>The TOL-55 fragility probabilities (legacy table, verbatim).</summary>
    private static readonly double[] FragilityProbabilities55 =
    {
        0d, 0.000164643d, 0.001315461d, 0.004433322d, 0.01049172d, 0.020454674d, 0.035274058d,
        0.055886161d, 0.083207166d, 0.118127048d, 0.161501166d, 0.214138372d, 0.276783668d,
        0.350092095d, 0.434587794d, 0.530596772d, 0.638129875d, 0.756663572d, 0.88468745d, 1d, 1d,
    };

    /// <summary>
    /// The active legacy TOL-60 rating table. Its first 61 knots and last 40 knots are exact
    /// slices of the verbatim scenario tables above; the legacy source contains the same values.
    /// </summary>
    private static double[] RatingFlows60 => RatingFlows70.Take(61).Concat(RatingFlows55.Skip(61)).ToArray();

    /// <summary>The active legacy TOL-60 two-knot workbench fragility.</summary>
    private static readonly double[] FragilityStages60 = { 40d, 60d };

    /// <summary>The active legacy TOL-60 two-knot workbench fragility probabilities.</summary>
    private static readonly double[] FragilityProbabilities60 = { 0d, 0.5d };

    /// <summary>
    /// The active legacy TOL-65 rating table. Its first 66 knots and last 35 knots are exact
    /// slices of the verbatim scenario tables above; the legacy source contains the same values.
    /// </summary>
    private static double[] RatingFlows65 => RatingFlows70.Take(66).Concat(RatingFlows55.Skip(66)).ToArray();

    /// <summary>The active legacy TOL-65 fragility stages.</summary>
    private static readonly double[] FragilityStages65 =
    {
        40d, 41.35d, 42.7d, 44.05d, 45.4d, 46.75d, 48.1d, 49.45d, 50.8d, 52.15d, 53.5d,
        54.85d, 56.2d, 57.55d, 58.9d, 60.25d, 61.6d, 62.95d, 64.3d, 65.65d, 67d,
    };

    /// <summary>The active legacy TOL-65 fragility probabilities.</summary>
    private static readonly double[] FragilityProbabilities65 =
    {
        0d, 0.000147735d, 0.001180906d, 0.00398186d, 0.009428646d, 0.018393757d, 0.03174254d,
        0.050331131d, 0.075003715d, 0.106588811d, 0.145894109d, 0.193699105d, 0.250744254d,
        0.317714469d, 0.395212932d, 0.483717513d, 0.583503724d, 0.694497905d, 0.815968191d,
        0.945777748d, 1d,
    };

    /// <summary>The TOL-70 rating flows (cfs), one per stage 0–100 (legacy table, verbatim).</summary>
    private static readonly double[] RatingFlows70 =
    {
        3.29688234826824E-43d, 1.15791363983133d, 7.35229331982632d, 21.6770168763387d, 46.6841526010843d,
        84.6440005546358d, 137.64047757213d, 207.620839703465d, 296.425891796821d, 405.810109228457d,
        537.455902092903d, 692.984195834673d, 873.962555565617d, 1081.91159684518d, 1318.31015742321d,
        1584.59954644885d, 1882.18708987554d, 2212.44912776551d, 2576.73357715433d, 2976.36214525915d,
        3412.63225747517d, 3886.81874996067d, 4400.17536586119d, 4953.93608619596d, 5549.3163203427d,
        6187.51397637637d, 6869.71042787455d, 7597.07139093336d, 8370.74772285584d, 9191.87615214199d,
        10061.5799479249d, 10980.9695357853d, 11951.1430658776d, 12973.186938477d, 14048.1762913644d,
        15177.1754528896d, 16361.2383640663d, 17601.4089726365d, 18898.7216016908d, 20254.2012951293d,
        21668.8641419868d, 23273.9179860605d, 24878.9718301343d, 26484.025674208d, 28089.0795182818d,
        29694.1333623555d, 31299.1872064293d, 32904.2410505031d, 34509.2948945768d, 36114.3487386506d,
        37719.4025827243d, 39324.4564267981d, 40929.5102708718d, 42534.5641149456d, 44139.6179590193d,
        45744.6718030931d, 47349.7256471669d, 48954.7794912406d, 50559.8333353144d, 52164.8871793881d,
        53769.9410234619d, 55374.9948675356d, 56980.0487116094d, 58585.1025556831d, 60190.1563997569d,
        61795.2102438306d, 63400.2640879044d, 65005.3179319781d, 66610.3717760519d, 68215.4256201257d,
        69820.4794641994d, 161658.617509597d, 168135.676076877d, 174712.7094711d, 181388.567504775d,
        188162.14322452d, 195032.370032334d, 201998.21907505d, 209058.696870302d, 216212.843141833d,
        223459.72884072d, 230798.454332226d, 238228.147730669d, 245747.963366931d, 253357.080375168d,
        261054.701386906d, 268840.051322133d, 276712.376268195d, 284670.942438367d, 292715.035202874d,
        300843.95818591d, 309057.032422936d, 317353.59557309d, 325733.001182121d, 334194.617991681d,
        342737.829291256d, 351362.032309365d, 360066.637640968d, 368851.068708337d, 377714.761252874d,
        386657.162855601d,
    };

    /// <summary>The TOL-70 fragility stages (legacy table, verbatim).</summary>
    private static readonly double[] FragilityStages70 =
    {
        40d, 41.6d, 43.2d, 44.8d, 46.4d, 48d, 49.6d, 51.2d, 52.8d, 54.4d, 56d,
        57.6d, 59.2d, 60.8d, 62.4d, 64d, 65.6d, 67.2d, 68.8d, 70.4d, 72d,
    };

    /// <summary>The TOL-70 fragility probabilities (legacy table, verbatim).</summary>
    private static readonly double[] FragilityProbabilities70 =
    {
        0d, 0.00014373d, 0.001149032d, 0.003874924d, 0.009176862d, 0.017905698d, 0.03090635d,
        0.049016081d, 0.073062205d, 0.103858974d, 0.142203238d, 0.188868244d, 0.244594472d,
        0.310075655d, 0.385936506d, 0.472695498d, 0.570698756d, 0.679993339d, 0.800058709d,
        0.929152488d, 1d,
    };

    #endregion

    #region Scenario

    /// <summary>One TOL scenario: its tables, threshold, and report pins.</summary>
    private sealed class Scenario
    {
        /// <summary>The top-of-levee stage (feet).</summary>
        public double Tol;

        /// <summary>The rating flows, one per stage 0–100.</summary>
        public double[] RatingFlows = Array.Empty<double>();

        /// <summary>The fragility stages.</summary>
        public double[] FragilityStages = Array.Empty<double>();

        /// <summary>The fragility probabilities.</summary>
        public double[] FragilityProbabilities = Array.Empty<double>();

        /// <summary>The report Table 104 Monte Carlo pin (10M draws, seed 45678).</summary>
        public double ReportMonteCarlo;

        /// <summary>The report Table 104 RMC-TotalRisk (v1.0 engine) pin.</summary>
        public double ReportEngine;

        /// <summary>The threshold flow: the rating knot at the TOL stage (the stage ordinates are the integers 0–100).</summary>
        public double ThresholdFlow => RatingFlows[(int)Tol];
    }

    /// <summary>The TOL-50 scenario.</summary>
    private static Scenario Tol50() => new()
    {
        Tol = 50d,
        RatingFlows = RatingFlows50,
        FragilityStages = FragilityStages50,
        FragilityProbabilities = FragilityProbabilities50,
        ReportMonteCarlo = 0.038197d,
        ReportEngine = 0.038212d,
    };

    /// <summary>The TOL-55 scenario.</summary>
    private static Scenario Tol55() => new()
    {
        Tol = 55d,
        RatingFlows = RatingFlows55,
        FragilityStages = FragilityStages55,
        FragilityProbabilities = FragilityProbabilities55,
        ReportMonteCarlo = 0.018829d,
        ReportEngine = 0.018807d,
    };


    /// <summary>The active legacy TOL-60 bootstrap-hazard scenario.</summary>
    private static Scenario Tol60() => new()
    {
        Tol = 60d,
        RatingFlows = RatingFlows60,
        FragilityStages = FragilityStages60,
        FragilityProbabilities = FragilityProbabilities60,
    };

    /// <summary>The active legacy TOL-65 bootstrap-hazard scenario.</summary>
    private static Scenario Tol65() => new()
    {
        Tol = 65d,
        RatingFlows = RatingFlows65,
        FragilityStages = FragilityStages65,
        FragilityProbabilities = FragilityProbabilities65,
    };
    /// <summary>The TOL-70 scenario.</summary>
    private static Scenario Tol70() => new()
    {
        Tol = 70d,
        RatingFlows = RatingFlows70,
        FragilityStages = FragilityStages70,
        FragilityProbabilities = FragilityProbabilities70,
        ReportMonteCarlo = 0.003820d,
        ReportEngine = 0.003809d,
    };

    #endregion

    #region Oracle and Exact Reference

    /// <summary>
    /// The legacy Monte Carlo API oracle: per realization, draw the flow from the LP3 through
    /// the first uniform, rate it to a stage, and flood when the stage overtops or a second
    /// uniform falls below the fragility — with the legacy short-circuit preserved (an
    /// overtopping draw consumes no failure uniform).
    /// </summary>
    /// <param name="scenario">The TOL scenario.</param>
    /// <param name="lp3">The flow-frequency distribution.</param>
    /// <returns>The API estimate and its binomial standard error.</returns>
    private static (double Api, double Se) RunApiOracle(Scenario scenario, LogPearsonTypeIII lp3)
    {
        var rating = new Linear(scenario.RatingFlows, RatingStages) { XTransform = Transform.Logarithmic };
        var fragility = new Linear(scenario.FragilityStages, scenario.FragilityProbabilities);

        var prng = new MersenneTwister(OracleSeed);
        long flooded = 0;
        for (int i = 0; i < OracleRealizations; i++)
        {
            double flow = lp3.InverseCDF(prng.NextDouble());
            double stage = rating.Interpolate(flow);
            double failureProbability = fragility.Interpolate(stage);
            if (stage > scenario.Tol || prng.NextDouble() <= failureProbability)
            {
                flooded++;
            }
        }
        double api = flooded / (double)OracleRealizations;
        return (api, Math.Sqrt(api * (1d - api) / OracleRealizations));
    }

    /// <summary>
    /// The exact API reference: API = (1 − u*) + ∫ p(h(u)) du over [u₄₀, u*], where u* is the
    /// non-exceedance probability of the threshold flow (overtopping is certain inundation
    /// above it), u₄₀ is the non-exceedance probability of the stage-40 flow (the fragility is
    /// identically zero below), and the integrand interpolates the same Numerics objects the
    /// oracle uses. Composite Simpson; the integrand is piecewise smooth between table knots,
    /// so the truncation error is far below the assert tolerances at the chosen resolution.
    /// </summary>
    /// <param name="scenario">The TOL scenario.</param>
    /// <param name="lp3">The flow-frequency distribution.</param>
    /// <param name="intervals">The (even) Simpson interval count.</param>
    /// <returns>The API to quadrature accuracy.</returns>
    private static double ExactApi(Scenario scenario, LogPearsonTypeIII lp3, int intervals)
    {
        var rating = new Linear(scenario.RatingFlows, RatingStages) { XTransform = Transform.Logarithmic };
        var fragility = new Linear(scenario.FragilityStages, scenario.FragilityProbabilities);

        double uStar = lp3.CDF(scenario.ThresholdFlow);
        double uLow = lp3.CDF(scenario.RatingFlows[40]);

        double Integrand(double u) => fragility.Interpolate(rating.Interpolate(lp3.InverseCDF(u)));

        double step = (uStar - uLow) / intervals;
        double sum = Integrand(uLow) + Integrand(uStar);
        for (int i = 1; i < intervals; i++)
        {
            sum += (i % 2 == 1 ? 4d : 2d) * Integrand(uLow + i * step);
        }
        return (1d - uStar) + sum * step / 3d;
    }

    #endregion

    #region Engine Builders

    /// <summary>Trims the legacy fragility's redundant trailing 1.0 knots (flat extrapolation reproduces them).</summary>
    /// <param name="stages">The fragility stages.</param>
    /// <param name="probabilities">The fragility probabilities.</param>
    /// <returns>The strictly increasing knot set ending at the first 1.0 ordinate.</returns>
    private static (double[] Stages, double[] Probabilities) TrimFragility(double[] stages, double[] probabilities)
    {
        int firstCertain = Array.IndexOf(probabilities, 1d);
        int keep = firstCertain >= 0 ? firstCertain + 1 : probabilities.Length;
        return (stages.Take(keep).ToArray(), probabilities.Take(keep).ToArray());
    }

    /// <summary>Builds the exact-LP3 parametric flow-frequency hazard (deterministic — no knowledge uncertainty).</summary>
    /// <param name="lp3">The parent distribution.</param>
    private static ParametricUnivariateHazard ParametricHazard(LogPearsonTypeIII lp3)
    {
        var hazard = new ParametricUnivariateHazard
        {
            Name = "Flow Frequency (LP3)",
            SpecifiedHazard = "Flow",
            HazardUnit = "cfs",
            ParentDistribution = (UnivariateDistributionBase)lp3.Clone(),
            IsUncertain = false,
        };
        hazard.Estimate();
        return hazard;
    }

    /// <summary>
    /// Builds the dense tabular flow-frequency hazard: LP3 quantiles on a ±8 z-grid at step
    /// 0.025 (641 knots), normal-z probability interpolation, logarithmic flow interpolation.
    /// </summary>
    /// <param name="lp3">The tabulated distribution.</param>
    private static TabularHazard TabulatedHazard(LogPearsonTypeIII lp3)
    {
        const double zStep = 0.025d;
        const double zRange = 8d;
        int count = (int)Math.Round(2d * zRange / zStep) + 1;
        var ordinates = new UncertainOrdinate[count];
        for (int i = 0; i < count; i++)
        {
            double p = Normal.StandardCDF(-zRange + i * zStep);
            ordinates[i] = new UncertainOrdinate(1d - p, new Deterministic(lp3.InverseCDF(p)));
        }
        return new TabularHazard
        {
            Name = "Flow Frequency (tabulated LP3)",
            SpecifiedHazard = "Flow",
            HazardUnit = "cfs",
            HazardTransform = Transform.Logarithmic,
            ProbabilityTransform = Transform.NormalZ,
            NoUncertaintyFunction = new UncertainOrderedPairedData(ordinates,
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>
    /// Builds the reliability-mode levee component: the flow hazard, the failure mode chaining
    /// the rating transform into the fragility response, the response-free non-failure mode,
    /// and the top-of-levee hazard threshold in flow units.
    /// </summary>
    /// <param name="scenario">The TOL scenario.</param>
    /// <param name="hazard">The flow-frequency hazard function.</param>
    private static SystemComponent BuildComponent(Scenario scenario, IHazardFunction hazard)
    {
        var ratingOrdinates = new UncertainOrdinate[scenario.RatingFlows.Length];
        for (int i = 0; i < scenario.RatingFlows.Length; i++)
        {
            ratingOrdinates[i] = new UncertainOrdinate(scenario.RatingFlows[i], new Deterministic(RatingStages[i]));
        }
        var rating = new TabularTransform
        {
            Name = "Rating Curve",
            SpecifiedHazard = "Flow",
            HazardUnit = "cfs",
            TransformedHazard = "Stage",
            TransformedHazardUnit = "ft",
            HazardTransform = Transform.Logarithmic,
            UncertainOrderedPairedData = new UncertainOrderedPairedData(ratingOrdinates,
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };

        var (fragilityStages, fragilityProbabilities) = TrimFragility(scenario.FragilityStages, scenario.FragilityProbabilities);
        var fragilityOrdinates = new UncertainOrdinate[fragilityStages.Length];
        for (int i = 0; i < fragilityStages.Length; i++)
        {
            fragilityOrdinates[i] = new UncertainOrdinate(fragilityStages[i], new Deterministic(fragilityProbabilities[i]));
        }
        var fragility = new TabularResponse
        {
            Name = "Prior to Overtopping Fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(fragilityOrdinates,
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };

        var component = new SystemComponent { Name = $"Levee TOL {scenario.Tol}" };
        component.HazardFunction = hazard;
        component.AddFailureMode(new FailureMode(
            new List<ITransformFunction> { rating }, null, fragility, null));
        component.AddFailureMode(new FailureMode(null, null, null, null));
        component.HazardThreshold = scenario.ThresholdFlow;
        return component;
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

    /// <summary>
    /// Runs a mean-only reliability analysis on a component and reads the API — the Fail stream
    /// total probability plus the NonFail stream's hazard-threshold exceedance (technical
    /// report equation 260; the v1.0 assurance diagnostic's read).
    /// </summary>
    /// <param name="component">The levee component.</param>
    /// <param name="label">The assert label.</param>
    /// <returns>The engine API.</returns>
    private static double EngineApi(SystemComponent component, string label)
    {
        var analysis = new RiskAnalysis(new[] { component }) { Name = label };
        analysis.Options.Mode = RiskAnalysisMode.Reliability;
        Run(analysis, label);
        var componentResults = analysis.RiskResults![0]!.ComponentResults[0];
        return componentResults.Fail.TotalProbability + componentResults.NonFail.HazardThresholdProbability;
    }

    #endregion

    /// <summary>
    /// Runs one TOL scenario end to end: the Monte Carlo oracle against the exact quadrature,
    /// the parametric-hazard engine against the exact reference and the oracle, the
    /// tabular-hazard engine against the exact reference, and the Table 104 report pins.
    /// </summary>
    /// <param name="scenario">The TOL scenario.</param>
    private static void RunScenario(Scenario scenario)
    {
        var lp3 = new LogPearsonTypeIII(Lp3Mean, Lp3Sd, Lp3Skew);
        string label = $"TOL {scenario.Tol}";

        // Arrange / Act — oracle, exact reference, and the two engine variants.
        var (oracleApi, oracleSe) = RunApiOracle(scenario, lp3);
        double exactApi = ExactApi(scenario, lp3, 1 << 18);
        double parametricApi = EngineApi(BuildComponent(scenario, ParametricHazard(lp3)), $"{label} parametric");
        double tabularApi = EngineApi(BuildComponent(scenario, TabulatedHazard(lp3)), $"{label} tabular");

        // Assert — the oracle agrees with the exact integral (self-consistency of the port).
        Assert.AreEqual(exactApi, oracleApi, K * oracleSe, $"{label}: the Monte Carlo oracle must agree with the exact quadrature.");

        // The parametric engine against the exact reference (deterministic allowance) and the oracle (k·SE).
        Assert.AreEqual(exactApi, parametricApi, EngineExactRelative * exactApi + 1e-6,
            $"{label}: the parametric-hazard engine API must match the exact quadrature.");
        Assert.AreEqual(oracleApi, parametricApi, K * oracleSe,
            $"{label}: the parametric-hazard engine API must match the legacy oracle.");

        // The tabular-hazard engine carries the additional tabulation allowance.
        Assert.AreEqual(exactApi, tabularApi, (EngineExactRelative + TabulationRelative) * exactApi + 1e-6,
            $"{label}: the tabular-hazard engine API must match the exact quadrature within the tabulation allowance.");

        // The Table 104 report pins: the oracle against the 10M Monte Carlo column at the
        // combined binomial error; the engine against the v1.0 engine column at the 0.5%
        // parity band.
        double combinedSe = Math.Sqrt(scenario.ReportMonteCarlo * (1d - scenario.ReportMonteCarlo) * (1d / OracleRealizations + 1e-7));
        Assert.AreEqual(scenario.ReportMonteCarlo, oracleApi, K * combinedSe,
            $"{label}: the oracle must reproduce the report Table 104 Monte Carlo value.");
        Assert.AreEqual(scenario.ReportEngine, parametricApi, 5e-3 * scenario.ReportEngine,
            $"{label}: the engine must match the report Table 104 RMC-TotalRisk value (v1.0 parity).");

        Console.WriteLine(
            $"{label}: oracle {oracleApi:G6} (±{oracleSe:G3}), exact {exactApi:G8}, parametric engine {parametricApi:G8} " +
            $"(Δexact {parametricApi - exactApi:G3}), tabular engine {tabularApi:G8} (Δexact {tabularApi - exactApi:G3}), " +
            $"report MC {scenario.ReportMonteCarlo:G6} / engine {scenario.ReportEngine:G6}");
    }
    /// <summary>
    /// Verifies one active legacy bootstrap-hazard configuration. Bootstrap seeds and
    /// short-circuit event draws follow the source order. The engine receives the resulting
    /// parameter sets and every realization is checked against independent conditional
    /// quadrature; the source-style single-event estimate is retained as a corroborating
    /// Monte Carlo check.
    /// </summary>
    /// <param name="scenario">The active TOL-60 or TOL-65 scenario.</param>
    private static void RunBootstrapScenario(Scenario scenario)
    {
        var parent = new LogPearsonTypeIII(Lp3Mean, Lp3Sd, Lp3Skew);
        var generator = new MersenneTwister(OracleSeed);
        var rating = new Linear(scenario.RatingFlows, RatingStages) { XTransform = Transform.Logarithmic };
        var fragility = new Linear(scenario.FragilityStages, scenario.FragilityProbabilities);
        var sets = new List<ParameterSet>(BootstrapRealizations);
        var exactApis = new double[BootstrapRealizations];
        int flooded = 0;

        for (int k = 0; k < BootstrapRealizations; k++)
        {
            var bootstrap = (LogPearsonTypeIII)parent.Bootstrap(
                ParameterEstimationMethod.MethodOfMoments, 114, generator.Next());
            sets.Add(new ParameterSet(bootstrap.GetParameters, 0d));
            exactApis[k] = ExactApi(scenario, bootstrap, 1 << 13);

            double flow = bootstrap.InverseCDF(generator.NextDouble());
            double stage = rating.Interpolate(flow);
            if (stage > scenario.Tol || generator.NextDouble() <= fragility.Interpolate(stage))
            {
                flooded++;
            }
        }

        var hazard = new ParametricUnivariateHazard
        {
            Name = $"TOL {scenario.Tol} bootstrap LP3",
            SpecifiedHazard = "Flow",
            HazardUnit = "cfs",
            ParentDistribution = (UnivariateDistributionBase)parent.Clone(),
        };
        hazard.Estimate(sets);

        var analysis = new RiskAnalysis(new[] { BuildComponent(scenario, hazard) })
        {
            Name = $"TOL {scenario.Tol} bootstrap-hazard verification",
        };
        analysis.Options.Mode = RiskAnalysisMode.Reliability;
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.Realizations = BootstrapRealizations;
        analysis.Options.UseDefaults = false;
        analysis.Options.EnsembleTolerance = 1e-8;
        analysis.Options.EnsembleMinDepth = 2;
        Run(analysis, analysis.Name);

        Assert.AreEqual(BootstrapRealizations, analysis.RiskResults!.Count);
        var engineApis = new double[BootstrapRealizations];
        double maxDeviation = 0d;
        for (int k = 0; k < BootstrapRealizations; k++)
        {
            var componentResults = analysis.RiskResults[k]!.ComponentResults[0];
            engineApis[k] = componentResults.Fail.TotalProbability +
                componentResults.NonFail.HazardThresholdProbability;
            maxDeviation = Math.Max(maxDeviation, Math.Abs(exactApis[k] - engineApis[k]));
        }

        double exactMean = exactApis.Average();
        double engineMean = engineApis.Average();
        Assert.AreEqual(exactMean, engineMean,
            EnsembleExactRelative * exactMean + EnsembleExactAbsolute,
            $"TOL {scenario.Tol}: the bootstrap-hazard ensemble mean must match conditional quadrature.");

        double sourceStyleApi = flooded / (double)BootstrapRealizations;
        double sourceStyleSe = Math.Sqrt(sourceStyleApi * (1d - sourceStyleApi) / BootstrapRealizations);
        Assert.AreEqual(exactMean, sourceStyleApi, K * sourceStyleSe + 1d / BootstrapRealizations,
            $"TOL {scenario.Tol}: the source-ordered single-event oracle must corroborate the conditional mean.");

        Console.WriteLine(
            $"TOL {scenario.Tol} bootstrap: exact mean {exactMean:G6}, engine {engineMean:G6}, " +
            $"source-order MC {sourceStyleApi:G6} (?{sourceStyleSe:G3}), max realization delta {maxDeviation:G3}.");
    }

    /// <summary>TOL 50: the legacy oracle, exact reference, both engine hazard variants, and the Table 104 pins.</summary>
    [TestMethod]
    public void Test_Tol50_Api_VsOracleExactAndReport()
    {
        RunScenario(Tol50());
    }

    /// <summary>TOL 55: the legacy oracle, exact reference, both engine hazard variants, and the Table 104 pins.</summary>
    [TestMethod]
    public void Test_Tol55_Api_VsOracleExactAndReport()
    {
        RunScenario(Tol55());
    }

    /// <summary>TOL 70: the legacy oracle, exact reference, both engine hazard variants, and the Table 104 pins.</summary>
    [TestMethod]
    public void Test_Tol70_Api_VsOracleExactAndReport()
    {
        RunScenario(Tol70());
    }
    /// <summary>
    /// TOL 60: the active legacy two-knot workbench fragility with per-realization LP3 bootstrap.
    /// </summary>
    [TestMethod]
    public void Test_Tol60_BootstrapHazard_VsConditionalQuadrature()
    {
        RunBootstrapScenario(Tol60());
    }

    /// <summary>
    /// TOL 65: the active legacy 21-knot fragility with per-realization LP3 bootstrap.
    /// </summary>
    [TestMethod]
    public void Test_Tol65_BootstrapHazard_VsConditionalQuadrature()
    {
        RunBootstrapScenario(Tol65());
    }

    /// <summary>
    /// The assurance computation under knowledge uncertainty (technical report Appendix I; the
    /// v1.0 assurance diagnostic): a deterministic 300-set LP3 parameter posterior is injected
    /// into the parametric hazard, the reliability analysis runs with full uncertainty, and the
    /// engine's per-realization API — Fail total probability plus NonFail threshold exceedance,
    /// read per realization exactly as the assurance diagnostic reads it — is verified
    /// realization-for-realization against an exact per-parameter-set quadrature oracle. The
    /// ensemble mean API and the assurance fraction P(API ≤ 0.01) then follow, with a
    /// borderline guard proving no realization sits within the comparison tolerance of the
    /// target (so the two ensembles' assurance counts must agree exactly).
    /// </summary>
    [TestMethod]
    public void Test_Assurance_FullUncertainty_Tol70_VsExactEnsemble()
    {
        // Arrange — the deterministic posterior: LP3 parameter triples perturbed through fixed
        // seed 45678 (mean-of-log ±0.0153 normal, sd-of-log ×exp(0.10 normal) — positive by
        // construction, skew ±0.15 normal — magnitudes representative of a ~100-year record).
        var scenario = Tol70();
        var generator = new MersenneTwister(OracleSeed);
        var parameters = new double[AssuranceRealizations][];
        var sets = new List<ParameterSet>(AssuranceRealizations);
        for (int k = 0; k < AssuranceRealizations; k++)
        {
            double mean = Lp3Mean + 0.0153d * Normal.StandardZ(generator.NextDouble());
            double sd = Lp3Sd * Math.Exp(0.10d * Normal.StandardZ(generator.NextDouble()));
            double skew = Lp3Skew + 0.15d * Normal.StandardZ(generator.NextDouble());
            parameters[k] = new[] { mean, sd, skew };
            sets.Add(new ParameterSet(parameters[k], 0d));
        }

        var hazard = new ParametricUnivariateHazard
        {
            Name = "Flow Frequency (LP3 posterior)",
            SpecifiedHazard = "Flow",
            HazardUnit = "cfs",
            ParentDistribution = new LogPearsonTypeIII(Lp3Mean, Lp3Sd, Lp3Skew),
        };
        hazard.Estimate(sets);
        var component = BuildComponent(scenario, hazard);

        var analysis = new RiskAnalysis(new[] { component }) { Name = "TOL 70 assurance" };
        analysis.Options.Mode = RiskAnalysisMode.Reliability;
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.Realizations = AssuranceRealizations;
        // The ensemble discipline is pinned to full quadrature rigor: this test
        // compares each realization's API to an exact per-parameter-set quadrature oracle and
        // pins the assurance fraction exactly, so the engine's relaxed ensemble default (a
        // deliberate accuracy split whose relaxed path the MultiConsequence ensemble family
        // exercises) is set aside here. UseDefaults must be false or the run-time reset would
        // re-apply the relaxed ensemble defaults over these pins.
        analysis.Options.UseDefaults = false;
        analysis.Options.EnsembleTolerance = 1e-8;
        analysis.Options.EnsembleMinDepth = 2;

        // Act — the engine ensemble and the exact per-parameter-set oracle ensemble.
        Run(analysis, "TOL 70 assurance");
        Assert.AreEqual(AssuranceRealizations, analysis.RiskResults!.Count,
            "The ensemble must carry one entry per injected posterior set.");

        var engineApis = new double[AssuranceRealizations];
        var oracleApis = new double[AssuranceRealizations];
        double maxDeviation = 0d;
        for (int k = 0; k < AssuranceRealizations; k++)
        {
            var componentResults = analysis.RiskResults[k]!.ComponentResults[0];
            engineApis[k] = componentResults.Fail.TotalProbability + componentResults.NonFail.HazardThresholdProbability;
            oracleApis[k] = ExactApi(scenario, new LogPearsonTypeIII(parameters[k][0], parameters[k][1], parameters[k][2]), 1 << 13);
            maxDeviation = Math.Max(maxDeviation, Math.Abs(engineApis[k] - oracleApis[k]));
        }

        // Assert — realization-for-realization parity at the ensemble allowance (the
        // parametric hazard's D = 0 sampling walks the injected posterior by index).
        for (int k = 0; k < AssuranceRealizations; k++)
        {
            Assert.AreEqual(oracleApis[k], engineApis[k], EnsembleExactRelative * oracleApis[k] + EnsembleExactAbsolute,
                $"Realization {k}: the engine API must match the exact quadrature for its injected parameter set.");
        }

        // The ensemble mean API.
        double engineMean = engineApis.Average();
        double oracleMean = oracleApis.Average();
        Assert.AreEqual(oracleMean, engineMean, EnsembleExactRelative * oracleMean + EnsembleExactAbsolute,
            "The ensemble mean API must match the exact ensemble.");

        // The assurance fraction: no realization may sit within twice the comparison
        // tolerance of the target (borderline guard), so the counts must agree exactly.
        double borderline = 2d * (EnsembleExactRelative * TargetAep + EnsembleExactAbsolute);
        int borderlineCount = oracleApis.Count(api => Math.Abs(api - TargetAep) <= borderline);
        Assert.AreEqual(0, borderlineCount,
            $"No oracle API may sit within {borderline:G3} of the {TargetAep} target (found {borderlineCount}) — the assurance counts could otherwise legitimately differ.");
        double engineAssurance = engineApis.Count(api => api <= TargetAep) / (double)AssuranceRealizations;
        double oracleAssurance = oracleApis.Count(api => api <= TargetAep) / (double)AssuranceRealizations;
        Assert.AreEqual(oracleAssurance, engineAssurance, 0d,
            "The assurance fraction P(API ≤ 0.01) must match the exact ensemble exactly.");

        // Scenario health: the posterior spread must make assurance a discriminating measure
        // (neither degenerate at 0 nor saturated at 1).
        Assert.IsTrue(engineAssurance is > 0.05d and < 0.995d,
            $"The assurance level ({engineAssurance:G4}) must sit strictly inside (0.05, 0.995) for the scenario to discriminate.");

        Console.WriteLine(
            $"TOL 70 assurance: mean API {engineMean:G6}/{oracleMean:G6} (engine/exact), assurance {engineAssurance:P1}, " +
            $"max per-realization deviation {maxDeviation:G3}, borderline margin {borderline:G3}");
    }

    /// <summary>
    /// The NFIP reproducibility pins: renaming the component and every function (with fresh
    /// ids) and round-tripping the component through XML — the parametric hazard's estimate
    /// travels in its serialized results — are bit-identical on the API.
    /// </summary>
    [TestMethod]
    public void Test_Reproducibility_RenameAndRoundTrip()
    {
        // Arrange / Act — baseline, renamed, and round-tripped TOL-55 parametric runs.
        var lp3 = new LogPearsonTypeIII(Lp3Mean, Lp3Sd, Lp3Skew);
        var baseline = BuildComponent(Tol55(), ParametricHazard(lp3));
        double baselineApi = EngineApi(baseline, "TOL 55 baseline");

        var renamed = BuildComponent(Tol55(), ParametricHazard(lp3));
        renamed.Name = "Renamed Levee";
        foreach (var function in renamed.GetReferencedFunctions())
        {
            function.Name = $"Renamed {function.Name}";
            function.AssignNewId();
        }
        double renamedApi = EngineApi(renamed, "TOL 55 renamed");

        var restored = new SystemComponent(baseline.ToXElement());
        double restoredApi = EngineApi(restored, "TOL 55 round-tripped");

        // Assert — bit-identical APIs.
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(baselineApi), BitConverter.DoubleToInt64Bits(renamedApi),
            "Renaming every function and the component must be bit-inert on the API.");
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(baselineApi), BitConverter.DoubleToInt64Bits(restoredApi),
            "An XML round-trip must be bit-inert on the API.");
    }
}
