using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics;
using Numerics.Data;
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
/// Joint failure modes — the conversion of the legacy <c>Test_MC_JointFailures</c>
/// family: one system component with 2 or 5 potential failure modes across the four dependency
/// options and all four joint-consequence rules, verified against an independent brute-force
/// Monte Carlo oracle and pinned to the 2024 verification report's published constants.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Scenario</b> (the shared legacy Bucket-1 model): hazard = LnNormal(85, 20) (real-space
/// moments), tabulated on a z-grid of ±8 at step 0.1 (161 knots, exceedance descending);
/// fragilities = Normal CDFs — PFM-1 (140, 30), PFM-2 (160, 10), PFM-3 (150, 20),
/// PFM-4 (130, 35), PFM-5 (160, 15) — each tabulated on its own ±8σ z-grid at step 0.05σ
/// (321 knots); consequences = the exact legacy five-knot curves over stages
/// {60, 100, 140, 200, 250} with flat end clamps. Engine and oracle interpolate the SAME
/// tables, so both sides integrate the identical piecewise-linear model and tabulation error
/// cancels from the engine-versus-oracle asserts.
/// </para>
/// <para>
/// <b>Consolidation:</b> the 32 legacy methods ({2, 5}-PFM × {Independent, Positive, Negative,
/// Correlation} × {Additive, Average, Maximum, Minimum}) share identical sampling within each
/// dependency group — the combination rule only changes how the failing modes' consequences
/// aggregate. Each group test therefore runs ONE oracle pass that accumulates all four rules
/// from the same draws (bit-identical to four separate legacy passes at the same seeds) and
/// asserts each rule against its own mean-only engine run.
/// </para>
/// <para>
/// <b>Oracle mechanics</b> (ported from the legacy bodies): hazard uniforms from
/// <c>MersenneTwister(12345)</c>; correlated capacity draws from
/// <c>MultivariateNormal.GenerateRandomValues(N, 12345)</c> over the group's correlation
/// matrix; mode j fails when Φ(z_j) ≤ P_F,j(h); ALL exceeded modes fail jointly; the
/// incremental draw is fC − nfC with no clamp (the legacy joint convention — inert here
/// because every failure curve pointwise dominates the non-failure curve). N = 1,000,000
/// (the legacy 10M dropped 10× per the conversion policy).
/// </para>
/// <para>
/// <b>Documented deviations from the legacy bodies:</b> the 5-PFM Positive oracles used
/// r = 1 − ε_mach while the 2-PFM used r = 1 − √ε_mach; this port uses r = 1 − √ε_mach for
/// both (the engine's PerfectlyPositive constant; the two are statistically indistinguishable
/// and the r = 1 − ε matrix is numerically singular for Cholesky-based samplers).
/// </para>
/// <para>
/// <b>Tolerances:</b> engine-versus-oracle asserts use k·SE with k = 4 and SEs computed in-run
/// (mean SE = σ̂/√N; σ SE by the delta method √(m₄ − σ⁴)/(2σ√N); probability SEs binomial;
/// value-at-risk density-scaled with a 0.1% relative resolution floor; conditional mean by the
/// ratio-estimator first-order SE). Report pins assert the engine against the 2024 report's
/// 10M-draw Monte Carlo constants at 4·σ̂/√10⁷ + 0.1%·|pin| — the report's own sampling error
/// plus a residual tabulation allowance (the report sampled the exact distributions; this
/// scenario tabulates them on dense grids).
/// </para>
/// </remarks>
[TestClass]
public class JointFailuresVerification
{
    /// <summary>The oracle realization count (legacy 10M dropped 10× per the conversion policy).</summary>
    private const int OracleRealizations = 1_000_000;

    /// <summary>The legacy hazard-stream seed.</summary>
    private const int HazardSeed = 12345;

    /// <summary>The legacy multivariate-normal capacity-draw seed.</summary>
    private const int MvnSeed = 12345;

    /// <summary>The tolerance multiplier on the Monte Carlo standard error.</summary>
    private const double K = 4d;

    /// <summary>The exceedance level for the value-at-risk and conditional value-at-risk asserts (the engine default).</summary>
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

    /// <summary>The joint-consequence rules in engine declaration order, driving every per-rule array.</summary>
    private static readonly JointConsequenceType[] Rules =
    {
        JointConsequenceType.Additive, JointConsequenceType.Average, JointConsequenceType.Maximum, JointConsequenceType.Minimum,
    };

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
    /// The 2024 report's published Monte Carlo constants (tables 61–76), keyed by
    /// (PFM count, dependency, rule) with values ordered {incremental, background, total,
    /// failure, non-failure}. Only Independent and PerfectlyNegative were published.
    /// </summary>
    private static readonly Dictionary<(int Pfm, DependencyType Dependency, JointConsequenceType Rule), double[]> ReportConstants = new()
    {
        [(2, DependencyType.Independent, JointConsequenceType.Additive)] = new[] { 2.138221d, 1.427955d, 3.566176d, 2.597312d, 0.968863d },
        [(2, DependencyType.Independent, JointConsequenceType.Average)] = new[] { 1.665706d, 1.427955d, 3.093661d, 2.124798d, 0.968863d },
        [(2, DependencyType.Independent, JointConsequenceType.Maximum)] = new[] { 1.783835d, 1.427955d, 3.211790d, 2.242926d, 0.968863d },
        [(2, DependencyType.Independent, JointConsequenceType.Minimum)] = new[] { 1.547577d, 1.427955d, 2.975532d, 2.006669d, 0.968863d },
        [(2, DependencyType.PerfectlyNegative, JointConsequenceType.Additive)] = new[] { 2.114628d, 1.427955d, 3.542583d, 2.598992d, 0.943591d },
        [(2, DependencyType.PerfectlyNegative, JointConsequenceType.Average)] = new[] { 1.740963d, 1.427955d, 3.168918d, 2.225327d, 0.943591d },
        [(2, DependencyType.PerfectlyNegative, JointConsequenceType.Maximum)] = new[] { 1.834379d, 1.427955d, 3.262334d, 2.318743d, 0.943591d },
        [(2, DependencyType.PerfectlyNegative, JointConsequenceType.Minimum)] = new[] { 1.647547d, 1.427955d, 3.075501d, 2.131910d, 0.943591d },
        [(5, DependencyType.Independent, JointConsequenceType.Additive)] = new[] { 6.936764d, 1.427955d, 8.364718d, 7.716204d, 0.648515d },
        [(5, DependencyType.Independent, JointConsequenceType.Average)] = new[] { 2.630434d, 1.427955d, 4.058388d, 3.409874d, 0.648515d },
        [(5, DependencyType.Independent, JointConsequenceType.Maximum)] = new[] { 3.874881d, 1.427955d, 5.302836d, 4.654321d, 0.648515d },
        [(5, DependencyType.Independent, JointConsequenceType.Minimum)] = new[] { 1.517102d, 1.427955d, 2.945057d, 2.296542d, 0.648515d },
        [(5, DependencyType.PerfectlyNegative, JointConsequenceType.Additive)] = new[] { 6.890224d, 1.427955d, 8.318179d, 7.710042d, 0.608137d },
        [(5, DependencyType.PerfectlyNegative, JointConsequenceType.Average)] = new[] { 2.797486d, 1.427955d, 4.225441d, 3.617304d, 0.608137d },
        [(5, DependencyType.PerfectlyNegative, JointConsequenceType.Maximum)] = new[] { 4.004253d, 1.427955d, 5.432208d, 4.824072d, 0.608137d },
        [(5, DependencyType.PerfectlyNegative, JointConsequenceType.Minimum)] = new[] { 1.713543d, 1.427955d, 3.141498d, 2.533361d, 0.608137d },
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

    /// <summary>Builds the engine analysis for one joint scenario from the shared tables.</summary>
    /// <param name="pfmCount">The failure-mode count (2 or 5).</param>
    /// <param name="dependency">The failure-mode dependency option.</param>
    /// <param name="userMatrix">The user correlation matrix (correlation-matrix mode only).</param>
    /// <param name="rule">The joint-consequence rule.</param>
    private static RiskAnalysis BuildAnalysis(int pfmCount, DependencyType dependency, double[,]? userMatrix, JointConsequenceType rule)
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

        component.FailureModeMethod = FailureModeMethod.JointFailures;
        component.FailureModeDependency = dependency;
        if (userMatrix != null)
        {
            component.CorrelationMatrix = (double[,])userMatrix.Clone();
        }
        component.JointConsequences = rule;

        var analysis = new RiskAnalysis(new[] { component }) { Name = $"Joint {pfmCount}-PFM {dependency} {rule}" };
        analysis.Options.ConsequenceThreshold = Threshold;
        analysis.Options.Alpha = Alpha;
        // The exceedance probes read the output LEC surface; at the default 200-point thinning
        // the log-log interpolation bias at the curve's conditional-median knee (~1.5%
        // relative) exceeds the binomial 4·SE at N = 10⁶. The maximum output resolution keeps
        // the thinning error an order below the statistical tolerance, so the probes verify
        // the exact-LEC construction rather than the presentation ladder (whose default-200
        // fidelity is a documented engine property).
        analysis.Options.LECOutputLength = 1000;
        return analysis;
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

    /// <summary>One dependency group's oracle output: shared streams plus the per-rule streams.</summary>
    private sealed class JointOracleResult
    {
        /// <summary>The failure-union estimate (annualized failure probability).</summary>
        public double FailureProbability;

        /// <summary>The binomial standard error of the failure union.</summary>
        public double FailureProbabilitySe;

        /// <summary>The number of realizations with at least one failing mode.</summary>
        public long FailureCount;

        /// <summary>The background stream (the non-failure consequence unconditionally).</summary>
        public Moments Background;

        /// <summary>The non-failure stream (the non-failure consequence when no mode fails).</summary>
        public Moments NonFailure;

        /// <summary>The failure risk stream per rule (zero on non-failing draws).</summary>
        public Moments[] Fail = new Moments[Rules.Length];

        /// <summary>The total risk stream per rule.</summary>
        public Moments[] Total = new Moments[Rules.Length];

        /// <summary>The incremental (excess) risk stream per rule (fC − nfC on failing draws, no clamp — the legacy joint convention).</summary>
        public Moments[] Excess = new Moments[Rules.Length];

        /// <summary>The sum of squared combined failure consequences over failing draws, per rule (the ratio-estimator SE input).</summary>
        public double[] ConditionalSumOfSquares = new double[Rules.Length];

        /// <summary>The sorted unconditional failure losses per rule (zeros on non-failing draws).</summary>
        public double[][] SortedFailureLosses = new double[Rules.Length][];
    }

    /// <summary>
    /// The joint oracle: one pass over the shared draws accumulating all four combination
    /// rules — bit-identical to four separate legacy passes because the legacy methods used
    /// the same seeds and differed only in the aggregation of the failing modes' consequences.
    /// Draw order per realization: one hazard uniform from the 12345 stream, then the
    /// realization's row of the multivariate-normal capacity draws (seed 12345).
    /// </summary>
    /// <param name="pfmCount">The failure-mode count (2 or 5).</param>
    /// <param name="correlation">The capacity correlation matrix realized by the oracle's Gaussian copula.</param>
    private static JointOracleResult RunOracle(int pfmCount, double[,] correlation)
    {
        var (hazardProbabilities, hazardStages) = HazardTable();
        var fragilityStages = new double[pfmCount][];
        var fragilityProbabilities = new double[pfmCount][];
        for (int mode = 0; mode < pfmCount; mode++)
        {
            (fragilityStages[mode], fragilityProbabilities[mode]) = FragilityTable(mode);
        }

        var multivariate = new MultivariateNormal(new double[pfmCount], correlation);
        double[,] capacityDraws = multivariate.GenerateRandomValues(OracleRealizations, MvnSeed);
        var hazardStream = new MersenneTwister(HazardSeed);

        var result = new JointOracleResult();
        for (int r = 0; r < Rules.Length; r++)
        {
            result.SortedFailureLosses[r] = new double[OracleRealizations];
        }

        var ruleValues = new double[Rules.Length];
        for (int i = 0; i < OracleRealizations; i++)
        {
            double hazard = Interpolate(hazardProbabilities, hazardStages, hazardStream.NextDouble());
            double nonFailureConsequence = Interpolate(ConsequenceStages, NonFailureValues, hazard);

            int failingCount = 0;
            double sum = 0d, maximum = double.NegativeInfinity, minimum = double.PositiveInfinity;
            for (int mode = 0; mode < pfmCount; mode++)
            {
                double failureProbability = Math.Max(0d, Math.Min(1d, Interpolate(fragilityStages[mode], fragilityProbabilities[mode], hazard)));
                if (Normal.StandardCDF(capacityDraws[i, mode]) <= failureProbability)
                {
                    failingCount++;
                    double consequence = Interpolate(ConsequenceStages, FailureValues[mode], hazard);
                    sum += consequence;
                    maximum = Math.Max(maximum, consequence);
                    minimum = Math.Min(minimum, consequence);
                }
            }

            result.Background.Add(nonFailureConsequence);
            if (failingCount > 0)
            {
                result.FailureCount++;
                result.NonFailure.Add(0d);
                ruleValues[0] = sum;
                ruleValues[1] = sum / failingCount;
                ruleValues[2] = maximum;
                ruleValues[3] = minimum;
                for (int r = 0; r < Rules.Length; r++)
                {
                    double value = ruleValues[r];
                    result.Fail[r].Add(value);
                    result.Total[r].Add(value);
                    result.Excess[r].Add(value - nonFailureConsequence);
                    result.ConditionalSumOfSquares[r] += value * value;
                    result.SortedFailureLosses[r][i] = value;
                }
            }
            else
            {
                result.NonFailure.Add(nonFailureConsequence);
                for (int r = 0; r < Rules.Length; r++)
                {
                    result.Fail[r].Add(0d);
                    result.Total[r].Add(nonFailureConsequence);
                    result.Excess[r].Add(0d);
                    result.SortedFailureLosses[r][i] = 0d;
                }
            }
        }

        for (int r = 0; r < Rules.Length; r++)
        {
            Array.Sort(result.SortedFailureLosses[r]);
        }
        result.FailureProbability = result.FailureCount / (double)OracleRealizations;
        result.FailureProbabilitySe = Math.Sqrt(result.FailureProbability * (1d - result.FailureProbability) / OracleRealizations);
        return result;
    }

    #endregion

    #region Group Driver

    /// <summary>
    /// Runs one dependency group end to end: the consolidated oracle pass, then per rule a
    /// mean-only engine run asserted against the oracle on the five summary means, the failure
    /// union and its complement identity, the failure and total standard deviations, the
    /// conditional mean, the assurance measure, two data-driven loss-exceedance probes, the
    /// value-at-risk, and the conditional value-at-risk — each at its documented k·SE — plus
    /// the 2024 report constant pins where the scenario was published.
    /// </summary>
    /// <param name="pfmCount">The failure-mode count (2 or 5).</param>
    /// <param name="dependency">The engine dependency option.</param>
    /// <param name="oracleCorrelation">The oracle's capacity correlation matrix.</param>
    /// <param name="engineMatrix">The engine user matrix (correlation-matrix mode only).</param>
    private static void RunGroup(int pfmCount, DependencyType dependency, double[,] oracleCorrelation, double[,]? engineMatrix)
    {
        var oracle = RunOracle(pfmCount, oracleCorrelation);

        for (int r = 0; r < Rules.Length; r++)
        {
            var rule = Rules[r];
            var analysis = BuildAnalysis(pfmCount, dependency, engineMatrix, rule);
            analysis.RunAsync().GetAwaiter().GetResult();
            Assert.IsTrue(analysis.IsEstimated, $"{pfmCount}-PFM {dependency} {rule}: the analysis must estimate.");
            var summary = analysis.RiskResults![0]!;
            var failCurve = analysis.MeanRiskResults!.Curves.Fail;
            string label = $"{pfmCount}-PFM {dependency} {rule}";

            // The five summary means (v1.0-parity per the means-versus-tails policy).
            Assert.AreEqual(oracle.Fail[r].Mean, summary.Fail.Mean, K * oracle.Fail[r].MeanSe, $"{label}: failure risk mean.");
            Assert.AreEqual(oracle.NonFailure.Mean, summary.NonFail.Mean, K * oracle.NonFailure.MeanSe, $"{label}: non-failure risk mean.");
            Assert.AreEqual(oracle.Total[r].Mean, summary.Total.Mean, K * oracle.Total[r].MeanSe, $"{label}: total risk mean.");
            Assert.AreEqual(oracle.Excess[r].Mean, summary.Excess.Mean, K * oracle.Excess[r].MeanSe, $"{label}: incremental (excess) risk mean.");
            Assert.AreEqual(oracle.Background.Mean, summary.Background.Mean, K * oracle.Background.MeanSe, $"{label}: background risk mean.");

            // The failure union and its complement identity.
            Assert.AreEqual(oracle.FailureProbability, summary.Fail.TotalProbability, K * oracle.FailureProbabilitySe,
                $"{label}: annualized failure probability.");
            Assert.AreEqual(1d - oracle.FailureProbability, summary.NonFail.TotalProbability, K * oracle.FailureProbabilitySe,
                $"{label}: the non-failure stream's total probability must complement the failure union.");

            // Dispersion (Monte-Carlo-parity per the means-versus-tails policy).
            Assert.AreEqual(oracle.Fail[r].Sigma, summary.Fail.StandardDeviation, K * oracle.Fail[r].SigmaSe,
                $"{label}: failure risk standard deviation.");
            Assert.AreEqual(oracle.Total[r].Sigma, summary.Total.StandardDeviation, K * oracle.Total[r].SigmaSe,
                $"{label}: total risk standard deviation.");

            // The conditional mean by the first-order ratio-estimator standard error.
            double conditionalMean = oracle.Fail[r].Mean * OracleRealizations / oracle.FailureCount;
            double conditionalSe = Math.Sqrt(Math.Max(0d, oracle.ConditionalSumOfSquares[r] - conditionalMean * conditionalMean * oracle.FailureCount)) / oracle.FailureCount;
            Assert.AreEqual(conditionalMean, summary.Fail.ConditionalMean, K * conditionalSe, $"{label}: conditional mean loss given failure.");

            // The assurance measure and two data-driven loss-exceedance probes on the failure LEC.
            double[] losses = oracle.SortedFailureLosses[r];
            AssertExceedance(losses, Threshold, summary.Fail.ConsequenceThresholdProbability, $"{label}: assurance P(C > {Threshold}).");
            double probe1 = losses[OracleRealizations - (int)Math.Round(0.5d * oracle.FailureCount)];
            double probe2 = losses[OracleRealizations - (int)Math.Round(0.05d * oracle.FailureCount)];
            AssertExceedance(losses, probe1, failCurve.LEC.GetYFromX(probe1, Transform.Logarithmic, Transform.Logarithmic), $"{label}: exceedance at the conditional median loss {probe1:G6}.");
            AssertExceedance(losses, probe2, failCurve.LEC.GetYFromX(probe2, Transform.Logarithmic, Transform.Logarithmic), $"{label}: exceedance at the conditional 95th-percentile loss {probe2:G6}.");

            // Value-at-risk (density-scaled SE with the output-resolution floor) and the tail mean.
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

            // The 2024 report constant pins (published scenarios only): the report sampled the
            // exact distributions at 10M draws, so the tolerance is the report's own sampling
            // error (4·σ̂/√10⁷, σ̂ from this oracle's matching stream) plus a 0.1% relative
            // tabulation-and-integration allowance.
            if (ReportConstants.TryGetValue((pfmCount, dependency, rule), out var pins))
            {
                Assert.AreEqual(pins[0], summary.Excess.Mean, K * oracle.Excess[r].Sigma / Math.Sqrt(1e7) + 1e-3 * pins[0], $"{label}: report incremental pin.");
                Assert.AreEqual(pins[1], summary.Background.Mean, K * oracle.Background.Sigma / Math.Sqrt(1e7) + 1e-3 * pins[1], $"{label}: report background pin.");
                Assert.AreEqual(pins[2], summary.Total.Mean, K * oracle.Total[r].Sigma / Math.Sqrt(1e7) + 1e-3 * pins[2], $"{label}: report total pin.");
                Assert.AreEqual(pins[3], summary.Fail.Mean, K * oracle.Fail[r].Sigma / Math.Sqrt(1e7) + 1e-3 * pins[3], $"{label}: report failure pin.");
                Assert.AreEqual(pins[4], summary.NonFail.Mean, K * oracle.NonFailure.Sigma / Math.Sqrt(1e7) + 1e-3 * pins[4], $"{label}: report non-failure pin.");
            }

            Console.WriteLine(
                $"{label}: mean fail {oracle.Fail[r].Mean:G6}/{summary.Fail.Mean:G6}, total {oracle.Total[r].Mean:G6}/{summary.Total.Mean:G6}, " +
                $"excess {oracle.Excess[r].Mean:G6}/{summary.Excess.Mean:G6}, background {oracle.Background.Mean:G6}/{summary.Background.Mean:G6}, " +
                $"nonfail {oracle.NonFailure.Mean:G6}/{summary.NonFail.Mean:G6}, APF {oracle.FailureProbability:G6}/{summary.Fail.TotalProbability:G6}, " +
                $"σF {oracle.Fail[r].Sigma:G6}/{summary.Fail.StandardDeviation:G6}, VaR {valueAtRisk:G6}/{summary.Fail.ValueAtRisk:G6}, " +
                $"CVaR {tailMean:G6}/{summary.Fail.ConditionalValueAtRisk:G6} (oracle/engine)");
        }
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

    /// <summary>2-PFM, independent capacities — all four joint-consequence rules (legacy methods at lines 13, 145, 276, 407).</summary>
    [TestMethod]
    public void Test_2PFM_Independent_AllRules_VsOracle()
    {
        RunGroup(2, DependencyType.Independent, Equicorrelated(2, 0d), null);
    }

    /// <summary>2-PFM, perfectly positive capacities (r = 1 − √ε) — all four rules (legacy lines 539, 672, 804, 936).</summary>
    [TestMethod]
    public void Test_2PFM_PerfectlyPositive_AllRules_VsOracle()
    {
        RunGroup(2, DependencyType.PerfectlyPositive, Equicorrelated(2, 1d - Math.Sqrt(Tools.DoubleMachineEpsilon)), null);
    }

    /// <summary>2-PFM, perfectly negative capacities (r = −1 + √ε) — all four rules (legacy lines 1069, 1201, 1334, 1467).</summary>
    [TestMethod]
    public void Test_2PFM_PerfectlyNegative_AllRules_VsOracle()
    {
        RunGroup(2, DependencyType.PerfectlyNegative, Equicorrelated(2, -1d + Math.Sqrt(Tools.DoubleMachineEpsilon)), null);
    }

    /// <summary>2-PFM, user correlation r = 0.5 — all four rules (legacy lines 1600, 1733, 1866, 1999).</summary>
    [TestMethod]
    public void Test_2PFM_CorrelationMatrix_AllRules_VsOracle()
    {
        var matrix = Equicorrelated(2, 0.5d);
        RunGroup(2, DependencyType.CorrelationMatrix, matrix, matrix);
    }

    /// <summary>5-PFM, independent capacities — all four rules (legacy lines 2131, 2271, 2411, 2551).</summary>
    [TestMethod]
    public void Test_5PFM_Independent_AllRules_VsOracle()
    {
        RunGroup(5, DependencyType.Independent, Equicorrelated(5, 0d), null);
    }

    /// <summary>
    /// 5-PFM, perfectly positive capacities — all four rules (legacy lines 2691, 2831, 2971,
    /// 3111). The oracle uses r = 1 − √ε rather than the legacy 1 − ε (documented deviation:
    /// statistically indistinguishable, matches the engine constant, and Cholesky-stable).
    /// </summary>
    [TestMethod]
    public void Test_5PFM_PerfectlyPositive_AllRules_VsOracle()
    {
        RunGroup(5, DependencyType.PerfectlyPositive, Equicorrelated(5, 1d - Math.Sqrt(Tools.DoubleMachineEpsilon)), null);
    }

    /// <summary>
    /// 5-PFM, perfectly negative capacities — all four rules (legacy lines 3251, 3391, 3531,
    /// 3671). The most negative exchangeable equicorrelation at D = 5 is −1/4, so the legacy
    /// bodies (and the engine's automatic mode) use r = −0.25 + √ε.
    /// </summary>
    [TestMethod]
    public void Test_5PFM_PerfectlyNegative_AllRules_VsOracle()
    {
        RunGroup(5, DependencyType.PerfectlyNegative, Equicorrelated(5, -0.25d + Math.Sqrt(Tools.DoubleMachineEpsilon)), null);
    }

    /// <summary>5-PFM, the legacy full user correlation matrix — all four rules (legacy lines 3811, 3950, 4089, 4228).</summary>
    [TestMethod]
    public void Test_5PFM_CorrelationMatrix_AllRules_VsOracle()
    {
        RunGroup(5, DependencyType.CorrelationMatrix, FiveModeCorrelation, FiveModeCorrelation);
    }

    /// <summary>
    /// The reproducibility pins on the correlation-matrix scenario (the configuration with the
    /// most serialized state): renaming every function and the component and round-tripping the
    /// component through XML (including the G17 correlation matrix) are bit-identical on the
    /// mean-only numeric results (the realization JSON embeds names, so the compare surface is
    /// the numeric one — bit-level scalars plus the exact LEC arrays); reordering the failure
    /// modes is equal to 1e-9 relative only — the declared order legitimately reorders the
    /// exclusive-pathway summation, so the comparison tolerates floating-point reassociation
    /// without accepting any real change.
    /// </summary>
    [TestMethod]
    public void Test_Reproducibility_RenameRoundTripReorder()
    {
        // Arrange — the baseline 2-PFM correlation-matrix additive run.
        var matrix = Equicorrelated(2, 0.5d);
        var baseline = BuildAnalysis(2, DependencyType.CorrelationMatrix, matrix, JointConsequenceType.Additive);
        baseline.RunAsync().GetAwaiter().GetResult();
        double baselineTotal = baseline.RiskResults![0]!.Total.Mean;
        double baselineUnion = baseline.RiskResults[0]!.Fail.TotalProbability;

        // Act / Assert — metadata renames are bit-identical on the numeric surface.
        var renamed = BuildAnalysis(2, DependencyType.CorrelationMatrix, matrix, JointConsequenceType.Additive);
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

        // A serialization round-trip (the G17 correlation matrix included) is bit-identical.
        var restored = new RiskAnalysis(new[] { new SystemComponent(baseline.Components[0].ToXElement()) });
        restored.Options.ConsequenceThreshold = Threshold;
        restored.Options.Alpha = Alpha;
        restored.Options.LECOutputLength = baseline.Options.LECOutputLength;
        restored.RunAsync().GetAwaiter().GetResult();
        AssertBitIdentical(baseline, restored, "round-trip");

        // Reordering the failure modes reassociates the pathway sums only.
        var reordered = BuildReordered(matrix);
        reordered.RunAsync().GetAwaiter().GetResult();
        Assert.AreEqual(baselineTotal, reordered.RiskResults![0]!.Total.Mean, 1e-9 * baselineTotal,
            "Reordering failure modes must only reassociate floating-point sums.");
        Assert.AreEqual(baselineUnion, reordered.RiskResults[0]!.Fail.TotalProbability, 1e-9 * baselineUnion,
            "The failure union must be order-invariant.");
    }

    /// <summary>
    /// Asserts two runs bit-identical on the mean-only numeric surface: the total and failure
    /// summary scalars at the 64-bit level and the exact failure/total LEC arrays.
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
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(expectedSummary.Fail.StandardDeviation), BitConverter.DoubleToInt64Bits(actualSummary.Fail.StandardDeviation),
            $"{label}: the failure standard deviation must be bit-identical.");
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(expectedSummary.Fail.ConditionalValueAtRisk), BitConverter.DoubleToInt64Bits(actualSummary.Fail.ConditionalValueAtRisk),
            $"{label}: the conditional value-at-risk must be bit-identical.");
        CollectionAssert.AreEqual(expected.MeanRiskResults!.Curves.Fail.LECConsequences, actual.MeanRiskResults!.Curves.Fail.LECConsequences,
            $"{label}: the failure LEC consequences must be bit-identical.");
        CollectionAssert.AreEqual(expected.MeanRiskResults.Curves.Fail.LECProbabilities, actual.MeanRiskResults.Curves.Fail.LECProbabilities,
            $"{label}: the failure LEC probabilities must be bit-identical.");
        CollectionAssert.AreEqual(expected.MeanRiskResults.Curves.Total.LECProbabilities, actual.MeanRiskResults.Curves.Total.LECProbabilities,
            $"{label}: the total LEC probabilities must be bit-identical.");
    }

    /// <summary>
    /// Builds the 2-PFM correlation additive scenario with the failure-mode declaration order
    /// swapped (the equicorrelated matrix is permutation-invariant, so the model is unchanged).
    /// </summary>
    /// <param name="matrix">The user correlation matrix.</param>
    private static RiskAnalysis BuildReordered(double[,] matrix)
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
        foreach (int mode in new[] { 1, 0 })
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
        component.FailureModeMethod = FailureModeMethod.JointFailures;
        component.FailureModeDependency = DependencyType.CorrelationMatrix;
        component.CorrelationMatrix = (double[,])matrix.Clone();
        component.JointConsequences = JointConsequenceType.Additive;

        var analysis = new RiskAnalysis(new[] { component }) { Name = "Joint reordered" };
        analysis.Options.ConsequenceThreshold = Threshold;
        analysis.Options.Alpha = Alpha;
        return analysis;
    }
}
