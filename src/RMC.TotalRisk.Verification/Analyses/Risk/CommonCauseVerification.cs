using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics;
using Numerics.Data;
using Numerics.Data.Statistics;
using Numerics.Distributions;
using Numerics.Sampling;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Verification.Analyses.Risk;

/// <summary>
/// Common cause adjustment — the conversion of the legacy <c>Test_MC_CommonCause</c>
/// family: one system component with 2 or 5 potential failure modes whose marginal
/// probabilities are reapportioned by the common-cause adjustment factor, across the four
/// dependency options, verified against an independent Monte Carlo oracle and pinned to the
/// 2024 verification report's published constants.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Scenario:</b> the shared legacy Bucket-1 model — LnNormal(85, 20) hazard tabulated on a
/// ±8 z-grid at step 0.1; Normal-CDF fragilities (PFM-1..5 means {140, 160, 150, 130, 160},
/// standard deviations {30, 10, 20, 35, 15}) tabulated on ±8σ z-grids at step 0.05σ; the exact
/// legacy five-knot consequence curves. Engine and oracle interpolate the SAME tables.
/// </para>
/// <para>
/// <b>Oracle mechanics</b> (ported from the legacy bodies): hazard uniforms from
/// <c>MersenneTwister(12345)</c>; one selection uniform per realization from
/// <c>MersenneTwister(45678)</c> — no multivariate sampling (the correlation enters only
/// through the adjustment factor, exactly as in the legacy bodies, whose multivariate objects
/// were declared but never used). Per realization the marginal probabilities are scaled by
/// <c>Probability.CommonCauseAdjustment</c> and one mode is selected by the cumulative
/// adjusted probabilities; the incremental draw is max(0, fC − nfC). The adjustment factor is
/// deliberately the shared Numerics kernel — the legacy oracles called the same function; the
/// independent element of the oracle is the Monte Carlo integration, not the adjustment
/// algebra. N = 1,000,000.
/// </para>
/// <para>
/// <b>Legacy method mapping:</b> the two <c>_CCA</c> bodies (the one-argument adjustment) and
/// the two <c>_CommonCause_Independent</c> bodies (the three-argument call with an identity
/// matrix) produce identical realization streams, so both convert into the Independent tests
/// here — four legacy methods, two converted tests, nothing lost. The legacy Negative and
/// Correlation bodies called the two-argument overload (correlation-matrix dependency), the
/// same code path the engine's common-cause factor takes for those options.
/// </para>
/// <para>
/// <b>Tolerances:</b> k·SE with k = 4 and SEs computed in-run exactly as the joint family;
/// report pins (tables 55–58: Independent and PerfectlyNegative) at 4·σ̂/√10⁷ + 0.1%·|pin|.
/// </para>
/// </remarks>
[TestClass]
public class CommonCauseVerification
{
    /// <summary>The oracle realization count (legacy 10M dropped 10× per the conversion policy).</summary>
    private const int OracleRealizations = 1_000_000;

    /// <summary>The legacy hazard-stream seed.</summary>
    private const int HazardSeed = 12345;

    /// <summary>The legacy selection-stream seed.</summary>
    private const int SelectionSeed = 45678;

    /// <summary>The tolerance multiplier on the Monte Carlo standard error.</summary>
    private const double K = 4d;

    /// <summary>The exceedance level for the value-at-risk and conditional value-at-risk asserts.</summary>
    private const double Alpha = 0.01d;

    /// <summary>The consequence threshold behind the assurance-measure assert.</summary>
    private const double Threshold = 100d;

    /// <summary>The hazard z-grid step (±8 range).</summary>
    private const double HazardZStep = 0.1d;

    /// <summary>The fragility z-grid step (±8σ range).</summary>
    private const double FragilityZStep = 0.05d;

    /// <summary>The z-grid half-range of every tabulated curve.</summary>
    private const double ZRange = 8d;

    /// <summary>The legacy fragility means for PFM-1..PFM-5.</summary>
    private static readonly double[] FragilityMeans = { 140d, 160d, 150d, 130d, 160d };

    /// <summary>The legacy fragility standard deviations for PFM-1..PFM-5.</summary>
    private static readonly double[] FragilitySds = { 30d, 10d, 20d, 35d, 15d };

    /// <summary>The legacy consequence-curve stages.</summary>
    private static readonly double[] ConsequenceStages = { 60d, 100d, 140d, 200d, 250d };

    /// <summary>The legacy failure-consequence ordinates for PFM-1..PFM-5.</summary>
    private static readonly double[][] FailureValues =
    {
        new[] { 0d, 5d, 50d, 500d, 750d },
        new[] { 0d, 3d, 30d, 300d, 450d },
        new[] { 0d, 10d, 100d, 1000d, 1500d },
        new[] { 0d, 2d, 20d, 200d, 300d },
        new[] { 0d, 8d, 80d, 800d, 1200d },
    };

    /// <summary>The legacy non-failure-consequence ordinates.</summary>
    private static readonly double[] NonFailureValues = { 0d, 1d, 10d, 100d, 150d };

    /// <summary>The legacy 5-PFM user correlation matrix (symmetric, positive definite).</summary>
    private static readonly double[,] FiveModeCorrelation =
    {
        { 1d, 0.03d, 0.29d, -0.04d, 0.85d },
        { 0.03d, 1d, -0.08d, 0.32d, 0.05d },
        { 0.29d, -0.08d, 1d, 0.59d, -0.18d },
        { -0.04d, 0.32d, 0.59d, 1d, -0.17d },
        { 0.85d, 0.05d, -0.18d, -0.17d, 1d },
    };

    /// <summary>
    /// The 2024 report's published Monte Carlo constants (tables 55–58), keyed by
    /// (PFM count, dependency) with values ordered {incremental, background, total, failure,
    /// non-failure}.
    /// </summary>
    private static readonly Dictionary<(int Pfm, DependencyType Dependency), double[]> ReportConstants = new()
    {
        [(2, DependencyType.Independent)] = new[] { 1.655845d, 1.427955d, 3.083800d, 2.115712d, 0.968088d },
        [(2, DependencyType.PerfectlyNegative)] = new[] { 1.736219d, 1.427955d, 3.164173d, 2.218916d, 0.945257d },
        [(5, DependencyType.Independent)] = new[] { 2.715177d, 1.427955d, 4.143132d, 3.494614d, 0.648518d },
        [(5, DependencyType.PerfectlyNegative)] = new[] { 2.847377d, 1.427955d, 4.275332d, 3.667178d, 0.608154d },
    };

    #region Shared Tables and Builders

    /// <summary>Builds the shared hazard table: non-exceedance probabilities (ascending) and stages from LnNormal(85, 20).</summary>
    private static (double[] Probabilities, double[] Stages) HazardTable()
    {
        var hazard = new LnNormal(85d, 20d);
        int count = (int)Math.Round(2d * ZRange / HazardZStep) + 1;
        var probabilities = new double[count];
        var stages = new double[count];
        for (int i = 0; i < count; i++)
        {
            double z = -ZRange + i * HazardZStep;
            probabilities[i] = Normal.StandardCDF(z);
            stages[i] = hazard.InverseCDF(probabilities[i]);
        }
        return (probabilities, stages);
    }

    /// <summary>Builds one fragility table: stages and failure probabilities from Φ((h − mean)/sd).</summary>
    /// <param name="mode">The zero-based failure-mode index into the legacy parameters.</param>
    private static (double[] Stages, double[] Probabilities) FragilityTable(int mode)
    {
        int count = (int)Math.Round(2d * ZRange / FragilityZStep) + 1;
        var stages = new double[count];
        var probabilities = new double[count];
        for (int i = 0; i < count; i++)
        {
            double z = -ZRange + i * FragilityZStep;
            stages[i] = FragilityMeans[mode] + FragilitySds[mode] * z;
            probabilities[i] = Normal.StandardCDF(z);
        }
        return (stages, probabilities);
    }

    /// <summary>The oracle's own linear interpolator with end clamping (x ascending).</summary>
    private static double Interpolate(double[] xValues, double[] yValues, double x)
    {
        if (x <= xValues[0]) return yValues[0];
        if (x >= xValues[xValues.Length - 1]) return yValues[yValues.Length - 1];
        int index = Array.BinarySearch(xValues, x);
        if (index >= 0) return yValues[index];
        index = ~index;
        double fraction = (x - xValues[index - 1]) / (xValues[index] - xValues[index - 1]);
        return yValues[index - 1] + fraction * (yValues[index] - yValues[index - 1]);
    }

    /// <summary>Builds a uniform equicorrelated matrix with unit diagonal.</summary>
    /// <param name="dimension">The matrix dimension.</param>
    /// <param name="offDiagonal">The shared off-diagonal correlation.</param>
    private static double[,] Equicorrelated(int dimension, double offDiagonal)
    {
        var matrix = new double[dimension, dimension];
        for (int i = 0; i < dimension; i++)
        {
            for (int j = 0; j < dimension; j++)
            {
                matrix[i, j] = i == j ? 1d : offDiagonal;
            }
        }
        return matrix;
    }

    /// <summary>Builds one tabular consequence over the legacy five-knot stages.</summary>
    /// <param name="name">The function name.</param>
    /// <param name="values">The consequence ordinates.</param>
    private static TabularConsequence Consequence(string name, double[] values)
    {
        var ordinates = new UncertainOrdinate[ConsequenceStages.Length];
        for (int i = 0; i < ConsequenceStages.Length; i++)
        {
            ordinates[i] = new UncertainOrdinate(ConsequenceStages[i], new Deterministic(values[i]));
        }
        return new TabularConsequence
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Loss",
            ConsequenceUnit = "lives",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(ordinates,
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds the engine analysis for one common-cause scenario from the shared tables.</summary>
    /// <param name="pfmCount">The failure-mode count (2 or 5).</param>
    /// <param name="dependency">The failure-mode dependency option (set after the method — the v1.0 coercion order).</param>
    /// <param name="userMatrix">The user correlation matrix (correlation-matrix mode only).</param>
    private static RiskAnalysis BuildAnalysis(int pfmCount, DependencyType dependency, double[,]? userMatrix)
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

        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = hazard;
        for (int mode = 0; mode < pfmCount; mode++)
        {
            var (fragilityStages, fragilityProbabilities) = FragilityTable(mode);
            var fragilityOrdinates = new UncertainOrdinate[fragilityStages.Length];
            for (int i = 0; i < fragilityStages.Length; i++)
            {
                fragilityOrdinates[i] = new UncertainOrdinate(fragilityStages[i], new Deterministic(fragilityProbabilities[i]));
            }
            var fragility = new TabularResponse
            {
                Name = $"PFM-{mode + 1} Fragility",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                UncertainOrderedPairedData = new UncertainOrderedPairedData(fragilityOrdinates,
                    true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
            };
            component.AddFailureMode(new FailureMode(null, null, fragility, Consequence($"PFM-{mode + 1} Loss", FailureValues[mode])));
        }
        component.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Loss", NonFailureValues)));

        // The v1.0 build order: selecting the common-cause method coerces the dependency to
        // Independent, so the scenario's dependency is applied afterwards.
        component.FailureModeMethod = FailureModeMethod.CommonCauseFailures;
        component.FailureModeDependency = dependency;
        if (userMatrix != null)
        {
            component.CorrelationMatrix = (double[,])userMatrix.Clone();
        }

        var analysis = new RiskAnalysis(new[] { component }) { Name = $"Common cause {pfmCount}-PFM {dependency}" };
        analysis.Options.ConsequenceThreshold = Threshold;
        analysis.Options.Alpha = Alpha;
        // Probe the output LEC at its maximum resolution so the thinning interpolation stays an
        // order below the binomial 4·SE (the joint family's documented rationale).
        analysis.Options.LECOutputLength = 1000;
        return analysis;
    }

    #endregion

    #region Oracle

    /// <summary>
    /// Online central-moment accumulator (Welford/Pébay updates through the fourth moment) —
    /// numerically stable for the zero-inflated loss streams whose raw power sums cancel.
    /// </summary>
    private struct Moments
    {
        /// <summary>The accumulated count.</summary>
        private long _count;

        /// <summary>The running mean.</summary>
        private double _m1;

        /// <summary>The running second central power sum.</summary>
        private double _m2;

        /// <summary>The running third central power sum.</summary>
        private double _m3;

        /// <summary>The running fourth central power sum.</summary>
        private double _m4;

        /// <summary>Accumulates one draw.</summary>
        /// <param name="value">The draw value.</param>
        public void Add(double value)
        {
            _count++;
            double delta = value - _m1;
            double deltaOverN = delta / _count;
            double deltaOverN2 = deltaOverN * deltaOverN;
            double term1 = delta * deltaOverN * (_count - 1);
            _m1 += deltaOverN;
            _m4 += term1 * deltaOverN2 * ((double)_count * _count - 3d * _count + 3d) + 6d * deltaOverN2 * _m2 - 4d * deltaOverN * _m3;
            _m3 += term1 * deltaOverN * (_count - 2) - 3d * deltaOverN * _m2;
            _m2 += term1;
        }

        /// <summary>The sample mean.</summary>
        public readonly double Mean => _m1;

        /// <summary>The population standard deviation.</summary>
        public readonly double Sigma => Math.Sqrt(_m2 / _count);

        /// <summary>The Monte Carlo standard error of the mean.</summary>
        public readonly double MeanSe => Sigma / Math.Sqrt(_count);

        /// <summary>The delta-method standard error of the standard deviation, √(m₄ − σ⁴)/(2σ√N).</summary>
        public readonly double SigmaSe
        {
            get
            {
                double variance = _m2 / _count;
                double m4 = _m4 / _count;
                return Math.Sqrt(Math.Max(0d, m4 - variance * variance)) / (2d * Math.Sqrt(variance) * Math.Sqrt(_count));
            }
        }
    }

    /// <summary>One dependency group's oracle output.</summary>
    private sealed class CommonCauseOracleResult
    {
        /// <summary>The failure-union estimate (annualized failure probability).</summary>
        public double FailureProbability;

        /// <summary>The binomial standard error of the failure union.</summary>
        public double FailureProbabilitySe;

        /// <summary>The number of realizations with a selected failure.</summary>
        public long FailureCount;

        /// <summary>The background stream (the non-failure consequence unconditionally).</summary>
        public Moments Background;

        /// <summary>The non-failure stream (the non-failure consequence when nothing fails).</summary>
        public Moments NonFailure;

        /// <summary>The failure risk stream (zero on non-failing draws).</summary>
        public Moments Fail;

        /// <summary>The total risk stream.</summary>
        public Moments Total;

        /// <summary>The incremental (excess) risk stream (max(0, fC − nfC) on failing draws — the legacy convention).</summary>
        public Moments Excess;

        /// <summary>The sum of squared selected consequences over failing draws (the ratio-estimator SE input).</summary>
        public double ConditionalSumOfSquares;

        /// <summary>The sorted unconditional failure losses (zeros on non-failing draws).</summary>
        public double[] SortedFailureLosses = Array.Empty<double>();
    }

    /// <summary>
    /// The common-cause oracle: per realization the marginal probabilities are scaled by the
    /// common-cause adjustment factor for the group's dependency and ONE mode is selected by
    /// walking the cumulative adjusted probabilities against a single 45678-stream uniform
    /// (the exact legacy selection). Draw order per realization: one hazard uniform, then one
    /// selection uniform.
    /// </summary>
    /// <param name="pfmCount">The failure-mode count (2 or 5).</param>
    /// <param name="dependency">The dependency option driving the adjustment-factor overload.</param>
    /// <param name="correlation">The correlation matrix for the dependent factor overloads (null for Independent).</param>
    private static CommonCauseOracleResult RunOracle(int pfmCount, DependencyType dependency, double[,]? correlation)
    {
        var (hazardProbabilities, hazardStages) = HazardTable();
        var fragilityStages = new double[pfmCount][];
        var fragilityProbabilities = new double[pfmCount][];
        for (int mode = 0; mode < pfmCount; mode++)
        {
            (fragilityStages[mode], fragilityProbabilities[mode]) = FragilityTable(mode);
        }

        var hazardStream = new MersenneTwister(HazardSeed);
        var selectionStream = new MersenneTwister(SelectionSeed);

        var result = new CommonCauseOracleResult { SortedFailureLosses = new double[OracleRealizations] };
        var marginals = new double[pfmCount];
        for (int i = 0; i < OracleRealizations; i++)
        {
            double hazard = Interpolate(hazardProbabilities, hazardStages, hazardStream.NextDouble());
            double selection = selectionStream.NextDouble();
            double nonFailureConsequence = Interpolate(ConsequenceStages, NonFailureValues, hazard);

            double marginalSum = 0d;
            for (int mode = 0; mode < pfmCount; mode++)
            {
                marginals[mode] = Math.Max(0d, Math.Min(1d, Interpolate(fragilityStages[mode], fragilityProbabilities[mode], hazard)));
                marginalSum += marginals[mode];
            }

            int selected = -1;
            if (marginalSum > 0d)
            {
                double factor = dependency switch
                {
                    DependencyType.Independent => Probability.CommonCauseAdjustment(marginals),
                    DependencyType.PerfectlyPositive => Probability.CommonCauseAdjustment(marginals, correlation, Probability.DependencyType.PerfectlyPositive),
                    _ => Probability.CommonCauseAdjustment(marginals, correlation),
                };
                double cumulative = 0d;
                for (int mode = 0; mode < pfmCount; mode++)
                {
                    cumulative += marginals[mode] * factor;
                    if (selection <= cumulative)
                    {
                        selected = mode;
                        break;
                    }
                }
            }

            result.Background.Add(nonFailureConsequence);
            if (selected >= 0)
            {
                result.FailureCount++;
                double consequence = Interpolate(ConsequenceStages, FailureValues[selected], hazard);
                result.NonFailure.Add(0d);
                result.Fail.Add(consequence);
                result.Total.Add(consequence);
                result.Excess.Add(Math.Max(0d, consequence - nonFailureConsequence));
                result.ConditionalSumOfSquares += consequence * consequence;
                result.SortedFailureLosses[i] = consequence;
            }
            else
            {
                result.NonFailure.Add(nonFailureConsequence);
                result.Fail.Add(0d);
                result.Total.Add(nonFailureConsequence);
                result.Excess.Add(0d);
                result.SortedFailureLosses[i] = 0d;
            }
        }

        Array.Sort(result.SortedFailureLosses);
        result.FailureProbability = result.FailureCount / (double)OracleRealizations;
        result.FailureProbabilitySe = Math.Sqrt(result.FailureProbability * (1d - result.FailureProbability) / OracleRealizations);
        return result;
    }

    #endregion

    #region Group Driver

    /// <summary>
    /// Runs one dependency group end to end: the oracle pass, one mean-only engine run, and the
    /// full assert catalog — the five summary means, the failure union and its complement
    /// identity, the failure and total standard deviations, the conditional mean, the assurance
    /// measure, two data-driven loss-exceedance probes, the value-at-risk, and the conditional
    /// value-at-risk — each at its documented k·SE — plus the report pins where published.
    /// </summary>
    /// <param name="pfmCount">The failure-mode count (2 or 5).</param>
    /// <param name="dependency">The dependency option.</param>
    /// <param name="oracleMatrix">The oracle's adjustment-factor matrix (null for Independent).</param>
    /// <param name="engineMatrix">The engine user matrix (correlation-matrix mode only).</param>
    private static void RunGroup(int pfmCount, DependencyType dependency, double[,]? oracleMatrix, double[,]? engineMatrix)
    {
        var oracle = RunOracle(pfmCount, dependency, oracleMatrix);

        var analysis = BuildAnalysis(pfmCount, dependency, engineMatrix);
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated, $"{pfmCount}-PFM {dependency}: the analysis must estimate.");
        var summary = analysis.RiskResults![0]!;
        var failCurve = analysis.MeanRiskResults!.Curves.Fail;
        string label = $"{pfmCount}-PFM CommonCause {dependency}";

        Assert.AreEqual(oracle.Fail.Mean, summary.Fail.Mean, K * oracle.Fail.MeanSe, $"{label}: failure risk mean.");
        Assert.AreEqual(oracle.NonFailure.Mean, summary.NonFail.Mean, K * oracle.NonFailure.MeanSe, $"{label}: non-failure risk mean.");
        Assert.AreEqual(oracle.Total.Mean, summary.Total.Mean, K * oracle.Total.MeanSe, $"{label}: total risk mean.");
        Assert.AreEqual(oracle.Excess.Mean, summary.Excess.Mean, K * oracle.Excess.MeanSe, $"{label}: incremental (excess) risk mean.");
        Assert.AreEqual(oracle.Background.Mean, summary.Background.Mean, K * oracle.Background.MeanSe, $"{label}: background risk mean.");

        Assert.AreEqual(oracle.FailureProbability, summary.Fail.TotalProbability, K * oracle.FailureProbabilitySe,
            $"{label}: annualized failure probability.");
        Assert.AreEqual(1d - oracle.FailureProbability, summary.NonFail.TotalProbability, K * oracle.FailureProbabilitySe,
            $"{label}: the non-failure stream's total probability must complement the failure union.");

        Assert.AreEqual(oracle.Fail.Sigma, summary.Fail.StandardDeviation, K * oracle.Fail.SigmaSe,
            $"{label}: failure risk standard deviation.");
        Assert.AreEqual(oracle.Total.Sigma, summary.Total.StandardDeviation, K * oracle.Total.SigmaSe,
            $"{label}: total risk standard deviation.");

        double conditionalMean = oracle.Fail.Mean * OracleRealizations / oracle.FailureCount;
        double conditionalSe = Math.Sqrt(Math.Max(0d, oracle.ConditionalSumOfSquares - conditionalMean * conditionalMean * oracle.FailureCount)) / oracle.FailureCount;
        Assert.AreEqual(conditionalMean, summary.Fail.ConditionalMean, K * conditionalSe, $"{label}: conditional mean loss given failure.");

        double[] losses = oracle.SortedFailureLosses;
        AssertExceedance(losses, Threshold, summary.Fail.ConsequenceThresholdProbability, $"{label}: assurance P(C > {Threshold}).");
        double probe1 = losses[OracleRealizations - (int)Math.Round(0.5d * oracle.FailureCount)];
        double probe2 = losses[OracleRealizations - (int)Math.Round(0.05d * oracle.FailureCount)];
        AssertExceedance(losses, probe1, failCurve.LEC.GetYFromX(probe1, Transform.Logarithmic, Transform.Logarithmic), $"{label}: exceedance at the conditional median loss {probe1:G6}.");
        AssertExceedance(losses, probe2, failCurve.LEC.GetYFromX(probe2, Transform.Logarithmic, Transform.Logarithmic), $"{label}: exceedance at the conditional 95th-percentile loss {probe2:G6}.");

        double valueAtRisk = losses[(int)Math.Round((1d - Alpha) * OracleRealizations)];
        double quantileLow = losses[(int)Math.Round((1d - Alpha - 0.001d) * OracleRealizations)];
        double quantileHigh = losses[(int)Math.Round((1d - Alpha + 0.001d) * OracleRealizations)];
        double density = 2d * 0.001d / Math.Max(1e-12, quantileHigh - quantileLow);
        double valueAtRiskSe = Math.Sqrt(Alpha * (1d - Alpha) / OracleRealizations) / density;
        Assert.AreEqual(valueAtRisk, summary.Fail.ValueAtRisk, Math.Max(K * valueAtRiskSe, 1e-3 * valueAtRisk),
            $"{label}: value-at-risk at α = {Alpha}.");

        int tailCount = (int)Math.Round(Alpha * OracleRealizations);
        double tailMean = 0d;
        for (int i = OracleRealizations - tailCount; i < OracleRealizations; i++) tailMean += losses[i];
        tailMean /= tailCount;
        double tailM2 = 0d;
        for (int i = OracleRealizations - tailCount; i < OracleRealizations; i++)
        {
            double delta = losses[i] - tailMean;
            tailM2 += delta * delta;
        }
        double tailMeanSe = Math.Sqrt(tailM2 / tailCount) / Math.Sqrt(tailCount);
        Assert.AreEqual(tailMean, summary.Fail.ConditionalValueAtRisk, Math.Max(K * tailMeanSe, 1e-3 * tailMean),
            $"{label}: conditional value-at-risk at α = {Alpha}.");

        if (ReportConstants.TryGetValue((pfmCount, dependency), out var pins))
        {
            Assert.AreEqual(pins[0], summary.Excess.Mean, K * oracle.Excess.Sigma / Math.Sqrt(1e7) + 1e-3 * pins[0], $"{label}: report incremental pin.");
            Assert.AreEqual(pins[1], summary.Background.Mean, K * oracle.Background.Sigma / Math.Sqrt(1e7) + 1e-3 * pins[1], $"{label}: report background pin.");
            Assert.AreEqual(pins[2], summary.Total.Mean, K * oracle.Total.Sigma / Math.Sqrt(1e7) + 1e-3 * pins[2], $"{label}: report total pin.");
            Assert.AreEqual(pins[3], summary.Fail.Mean, K * oracle.Fail.Sigma / Math.Sqrt(1e7) + 1e-3 * pins[3], $"{label}: report failure pin.");
            Assert.AreEqual(pins[4], summary.NonFail.Mean, K * oracle.NonFailure.Sigma / Math.Sqrt(1e7) + 1e-3 * pins[4], $"{label}: report non-failure pin.");
        }

        Console.WriteLine(
            $"{label}: mean fail {oracle.Fail.Mean:G6}/{summary.Fail.Mean:G6}, total {oracle.Total.Mean:G6}/{summary.Total.Mean:G6}, " +
            $"excess {oracle.Excess.Mean:G6}/{summary.Excess.Mean:G6}, background {oracle.Background.Mean:G6}/{summary.Background.Mean:G6}, " +
            $"nonfail {oracle.NonFailure.Mean:G6}/{summary.NonFail.Mean:G6}, APF {oracle.FailureProbability:G6}/{summary.Fail.TotalProbability:G6}, " +
            $"σF {oracle.Fail.Sigma:G6}/{summary.Fail.StandardDeviation:G6}, VaR {valueAtRisk:G6}/{summary.Fail.ValueAtRisk:G6}, " +
            $"CVaR {tailMean:G6}/{summary.Fail.ConditionalValueAtRisk:G6} (oracle/engine)");
    }

    /// <summary>
    /// Asserts one loss-exceedance ordinate against the oracle's empirical exceedance at the
    /// binomial k·SE, verifying first that the probe carries at least 100 exceedances (the
    /// tolerance-policy floor for curve checks).
    /// </summary>
    /// <param name="sortedLosses">The oracle's sorted unconditional losses.</param>
    /// <param name="level">The consequence level probed.</param>
    /// <param name="engineExceedance">The engine's exceedance ordinate at the level.</param>
    /// <param name="message">The assert label.</param>
    private static void AssertExceedance(double[] sortedLosses, double level, double engineExceedance, string message)
    {
        int exceeding = 0;
        for (int i = sortedLosses.Length - 1; i >= 0 && sortedLosses[i] > level; i--) exceeding++;
        Assert.IsTrue(exceeding >= 100, $"{message} — the probe must carry at least 100 exceedances (found {exceeding}).");
        double probability = exceeding / (double)sortedLosses.Length;
        double se = Math.Sqrt(probability * (1d - probability) / sortedLosses.Length);
        Assert.AreEqual(probability, engineExceedance, K * se, message);
    }

    #endregion

    /// <summary>2-PFM, independent — converts both the legacy <c>_CCA</c> (line 12) and <c>_CommonCause_Independent</c> (line 302) bodies (identical streams).</summary>
    [TestMethod]
    public void Test_2PFM_Independent_VsOracle()
    {
        RunGroup(2, DependencyType.Independent, null, null);
    }

    /// <summary>2-PFM, perfectly positive (legacy line 442).</summary>
    [TestMethod]
    public void Test_2PFM_PerfectlyPositive_VsOracle()
    {
        RunGroup(2, DependencyType.PerfectlyPositive, Equicorrelated(2, 1d - Math.Sqrt(Tools.DoubleMachineEpsilon)), null);
    }

    /// <summary>2-PFM, perfectly negative, r = −1 + √ε (legacy line 582 — the two-argument overload, the engine's else-branch path).</summary>
    [TestMethod]
    public void Test_2PFM_PerfectlyNegative_VsOracle()
    {
        RunGroup(2, DependencyType.PerfectlyNegative, Equicorrelated(2, -1d + Math.Sqrt(Tools.DoubleMachineEpsilon)), null);
    }

    /// <summary>2-PFM, user correlation r = 0.5 (legacy line 722).</summary>
    [TestMethod]
    public void Test_2PFM_CorrelationMatrix_VsOracle()
    {
        var matrix = Equicorrelated(2, 0.5d);
        RunGroup(2, DependencyType.CorrelationMatrix, matrix, matrix);
    }

    /// <summary>5-PFM, independent — converts both the legacy <c>_CCA</c> (line 151) and <c>_CommonCause_Independent</c> (line 862) bodies (identical streams).</summary>
    [TestMethod]
    public void Test_5PFM_Independent_VsOracle()
    {
        RunGroup(5, DependencyType.Independent, null, null);
    }

    /// <summary>5-PFM, perfectly positive (legacy line 1011).</summary>
    [TestMethod]
    public void Test_5PFM_PerfectlyPositive_VsOracle()
    {
        RunGroup(5, DependencyType.PerfectlyPositive, Equicorrelated(5, 1d - Math.Sqrt(Tools.DoubleMachineEpsilon)), null);
    }

    /// <summary>5-PFM, perfectly negative, r = −1/4 + √ε (legacy line 1161).</summary>
    [TestMethod]
    public void Test_5PFM_PerfectlyNegative_VsOracle()
    {
        RunGroup(5, DependencyType.PerfectlyNegative, Equicorrelated(5, -0.25d + Math.Sqrt(Tools.DoubleMachineEpsilon)), null);
    }

    /// <summary>5-PFM, the legacy full user correlation matrix (legacy line 1311).</summary>
    [TestMethod]
    public void Test_5PFM_CorrelationMatrix_VsOracle()
    {
        RunGroup(5, DependencyType.CorrelationMatrix, FiveModeCorrelation, FiveModeCorrelation);
    }

    /// <summary>
    /// The reproducibility pins on the correlation-matrix scenario: renames and the XML
    /// round-trip (G17 correlation matrix included) are bit-identical on the mean-only numeric
    /// surface. The round-trip additionally pins that the common-cause method's coerced
    /// dependency state restores correctly.
    /// </summary>
    [TestMethod]
    public void Test_Reproducibility_RenameAndRoundTrip()
    {
        // Arrange — the baseline 2-PFM correlation-matrix run.
        var matrix = Equicorrelated(2, 0.5d);
        var baseline = BuildAnalysis(2, DependencyType.CorrelationMatrix, matrix);
        baseline.RunAsync().GetAwaiter().GetResult();

        // Act / Assert — metadata renames are bit-identical.
        var renamed = BuildAnalysis(2, DependencyType.CorrelationMatrix, matrix);
        renamed.Name = "Renamed Analysis";
        var component = renamed.Components[0];
        component.Name = "Renamed Dam";
        foreach (var function in component.GetReferencedFunctions())
        {
            function.Name = $"Renamed {function.Name}";
            function.AssignNewId();
        }
        renamed.RunAsync().GetAwaiter().GetResult();
        AssertBitIdentical(baseline, renamed, "rename");

        // The XML round-trip is bit-identical.
        var restored = new RiskAnalysis(new[] { new SystemComponent(baseline.Components[0].ToXElement()) });
        restored.Options.ConsequenceThreshold = Threshold;
        restored.Options.Alpha = Alpha;
        restored.Options.LECOutputLength = baseline.Options.LECOutputLength;
        restored.RunAsync().GetAwaiter().GetResult();
        AssertBitIdentical(baseline, restored, "round-trip");
    }

    /// <summary>
    /// Asserts two runs bit-identical on the mean-only numeric surface: the total and failure
    /// summary scalars at the 64-bit level and the exact failure LEC arrays.
    /// </summary>
    /// <param name="expected">The baseline analysis (already run).</param>
    /// <param name="actual">The comparison analysis (already run).</param>
    /// <param name="label">The pin label.</param>
    private static void AssertBitIdentical(RiskAnalysis expected, RiskAnalysis actual, string label)
    {
        var expectedSummary = expected.RiskResults![0]!;
        var actualSummary = actual.RiskResults![0]!;
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(expectedSummary.Total.Mean), BitConverter.DoubleToInt64Bits(actualSummary.Total.Mean),
            $"{label}: the total mean must be bit-identical.");
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(expectedSummary.Fail.Mean), BitConverter.DoubleToInt64Bits(actualSummary.Fail.Mean),
            $"{label}: the failure mean must be bit-identical.");
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(expectedSummary.Fail.TotalProbability), BitConverter.DoubleToInt64Bits(actualSummary.Fail.TotalProbability),
            $"{label}: the failure union must be bit-identical.");
        CollectionAssert.AreEqual(expected.MeanRiskResults!.Curves.Fail.LECProbabilities, actual.MeanRiskResults!.Curves.Fail.LECProbabilities,
            $"{label}: the failure LEC probabilities must be bit-identical.");
        CollectionAssert.AreEqual(expected.MeanRiskResults.Curves.Fail.LECConsequences, actual.MeanRiskResults.Curves.Fail.LECConsequences,
            $"{label}: the failure LEC consequences must be bit-identical.");
    }
}
