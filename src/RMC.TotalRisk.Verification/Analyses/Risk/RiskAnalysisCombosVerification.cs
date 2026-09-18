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
using RMC.TotalRisk.Results.Risk;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Verification.Analyses.Risk;

/// <summary>
/// The conversion of the legacy <c>Test_RiskAnalysis</c> N-element/N-failure-mode
/// combination oracles: the 3- and 4-failure-mode single-component joint groups (the two
/// failure-mode counts the 2/5-PFM joint families did not cover), the 3- and 4-component
/// system groups (the component counts the <c>Test_MC_SystemRisk</c> matrix did not cover),
/// and the negative-correlation average-rule system scenario — each verified against an
/// independent brute-force Monte Carlo oracle at the legacy seeds and realization counts.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Legacy method disposition</b> (every <c>Test_RiskAnalysis</c> member accounted for; port
/// from bodies — several names are mislabeled):
/// <list type="bullet">
/// <item><description><c>Test_1Element_3PFM</c> / <c>Test_1Element_4PFM</c> — converted here
/// (1 component, 3/4 perfectly negative joint failure modes, N = 10⁶ — the legacy counts);
/// the consolidated oracle accumulates all four consequence rules from the legacy additive
/// draws.</description></item>
/// <item><description><c>Test_3Element_1PFM</c> / <c>Test_4Element_1PFM</c> — converted here
/// (3/4 perfectly negative components, additive rule, N = 10⁶, hazard-draw seed 12345).
/// </description></item>
/// <item><description><c>Test_5Element_1PFM_2</c> — converted here as the 2-component
/// r = −0.25 average-rule scenario its body computes: only the first two columns of its
/// 5-dimensional negative hazard draw participate, so the realized cross-component hazard
/// correlation is the 5-dimensional equicorrelation −1/4.</description></item>
/// <item><description><c>Test_1Element_2PFM</c> (negative/minimum body) and
/// <c>Test_1Element_5PFM</c> (independent/additive body) — stream-identical duplicates of
/// <c>JointFailuresVerification</c> scenarios (same seeds
/// <c>MersenneTwister(12345)</c> + <c>MultivariateNormal(…, 12345)</c>, same tables); not
/// re-ported.</description></item>
/// <item><description><c>Test_2Element_2PFM</c> — byte-for-byte the
/// <c>Test_MC_SystemRisk</c> 2-component 2-failure-mode independent additive body (seeds
/// 78910/12345/45678); covered by <c>SystemRiskMatrixVerification</c>.</description></item>
/// <item><description><c>Test_2Element_1PFM</c>, <c>Test_2Element_1PFM_New</c>,
/// <c>Test_5Element_1PFM</c> — the same scenarios as the system matrix's 2-component
/// independent additive / independent minimum / 5-component negative additive groups (the
/// last at identical seeds; the first two at alternate seed layouts of the same model);
/// covered by <c>SystemRiskMatrixVerification</c>.</description></item>
/// <item><description><c>Test_1Element_2PFM_Adaptive</c> / <c>Test_5Element_1PFM_Adaptive</c>
/// and the <c>TotalRisk_*_Sum</c> functions — inert integration workbenches (mostly
/// commented out, printing to the debugger, several referencing the legacy engine's own
/// types); not oracles, not ported. The exact conditional-mean integrand they exercised is
/// pinned by the mean-parity and consistency families.</description></item>
/// <item><description><c>Test_Composite</c>, <c>Test_Composite_Uncertainty</c>,
/// <c>Test_Composite_Consequence_Mixture</c> — the engine-level composite oracles, converted
/// in <c>CompositeEngineVerification</c>, <c>CompositeHazardVerification</c>, and
/// <c>CompositeConsequenceVerification</c>.</description></item>
/// <item><description><c>Test_EAD</c> — converted in <c>EadVerification</c>.
/// </description></item>
/// <item><description><c>Test_NFIP_Assurance_TOL_50/55/70</c> — converted in
/// <c>NfipAssuranceVerification</c>, whose bootstrap-hazard ensembles carry TOL 60/65; the
/// FDA variant is retired as obsolete.
/// </description></item>
/// </list>
/// </para>
/// <para>
/// <b>Documented deviations from the legacy bodies:</b> (1) the legacy negative
/// equicorrelations use −1/(D−1) + ε_mach; this port uses the engine constant
/// −1/(D−1) + √ε_mach (statistically indistinguishable, Cholesky-stable — a deliberately
/// accepted deviation class). (2) The 3-/4-element system bodies accumulate the RUNNING failure total
/// into the increment (<c>iC += fC − nfC(j)</c> — a typo the 2- and 5-element bodies do not
/// have); this port uses the per-component excess convention (failed component's consequence
/// minus its non-failure consequence, combined under the rule) that every other legacy system
/// body and the engine use. (3) The r = −0.25 scenario's engine matrix is the exact −0.25
/// (the oracle realizes −0.25 + √ε through its 5-dimensional draw — identical to floating
/// precision at the assert tolerances).
/// </para>
/// <para>
/// <b>Tolerances</b> (docs/verification.md): k·SE with k = 4 and oracle standard errors
/// computed in-run, exactly the joint-family assert catalog on the deterministic single-component engine
/// runs (means, union, dispersion, conditional mean, assurance, curve probes, value-at-risk,
/// conditional value-at-risk), and the joint-method convention on the system runs
/// (means with the reported VEGAS error added, probability asserts combined binomially at the
/// recorded evaluation count, the exhaustive mass balance, and the additive-rule
/// component-mean identity).
/// </para>
/// </remarks>
[TestClass]
public class RiskAnalysisCombosVerification
{
    /// <summary>The oracle realization count (the legacy 3/4-PFM and 3/4-element bodies ran at 10⁶ natively).</summary>
    private const int OracleRealizations = 1_000_000;

    /// <summary>The legacy hazard-stream seed of the single-component groups.</summary>
    private const int HazardSeed = 12345;

    /// <summary>The legacy capacity/multivariate seed (every stream in this legacy class).</summary>
    private const int SharedSeed = 12345;

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
    /// <param name="xValues">The abscissae, ascending.</param>
    /// <param name="yValues">The ordinates.</param>
    /// <param name="x">The lookup abscissa.</param>
    /// <returns>The interpolated ordinate.</returns>
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

    /// <summary>The most negative exchangeable equicorrelation at a dimension, offset by the engine's √ε guard.</summary>
    /// <param name="dimension">The dimension.</param>
    private static double PerfectlyNegativeOffDiagonal(int dimension)
    {
        return -1d / (dimension - 1) + Math.Sqrt(Tools.DoubleMachineEpsilon);
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

    /// <summary>Builds the shared z-grid tabular hazard.</summary>
    /// <param name="name">The function name.</param>
    private static TabularHazard Hazard(string name)
    {
        var (hazardProbabilities, hazardStages) = HazardTable();
        var hazardOrdinates = new UncertainOrdinate[hazardStages.Length];
        for (int i = 0; i < hazardStages.Length; i++)
        {
            hazardOrdinates[i] = new UncertainOrdinate(1d - hazardProbabilities[i], new Deterministic(hazardStages[i]));
        }
        return new TabularHazard
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            ProbabilityTransform = Transform.None,
            NoUncertaintyFunction = new UncertainOrderedPairedData(hazardOrdinates,
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds one z-grid tabular fragility.</summary>
    /// <param name="name">The function name.</param>
    /// <param name="mode">The zero-based legacy PFM index.</param>
    private static TabularResponse Fragility(string name, int mode)
    {
        var (fragilityStages, fragilityProbabilities) = FragilityTable(mode);
        var fragilityOrdinates = new UncertainOrdinate[fragilityStages.Length];
        for (int i = 0; i < fragilityStages.Length; i++)
        {
            fragilityOrdinates[i] = new UncertainOrdinate(fragilityStages[i], new Deterministic(fragilityProbabilities[i]));
        }
        return new TabularResponse
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(fragilityOrdinates,
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds one system component carrying a set of legacy PFMs plus the shared non-failure mode.</summary>
    /// <param name="name">The component name.</param>
    /// <param name="modes">The zero-based legacy PFM indices.</param>
    /// <param name="dependency">The within-component failure-mode dependency.</param>
    /// <param name="rule">The within-component joint-consequence rule.</param>
    private static SystemComponent BuildComponent(string name, int[] modes, DependencyType dependency, JointConsequenceType rule)
    {
        var component = new SystemComponent { Name = name };
        component.HazardFunction = Hazard($"{name} Stage Frequency");
        foreach (int mode in modes)
        {
            component.AddFailureMode(new FailureMode(null, null,
                Fragility($"{name} PFM-{mode + 1} Fragility", mode),
                Consequence($"{name} PFM-{mode + 1} Loss", FailureValues[mode])));
        }
        component.AddFailureMode(new FailureMode(null, null, null, Consequence($"{name} Non-Failure Loss", NonFailureValues)));
        component.FailureModeMethod = FailureModeMethod.JointFailures;
        component.FailureModeDependency = dependency;
        component.JointConsequences = rule;
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

    /// <summary>One rule's accumulated oracle streams.</summary>
    private sealed class RuleStreams
    {
        /// <summary>The failure risk stream (zero on non-failing draws).</summary>
        public Moments Fail;

        /// <summary>The non-failure risk stream.</summary>
        public Moments NonFail;

        /// <summary>The total risk stream.</summary>
        public Moments Total;

        /// <summary>The incremental (excess) risk stream.</summary>
        public Moments Excess;

        /// <summary>The background risk stream.</summary>
        public Moments Background;

        /// <summary>The sum of squared combined failure consequences over failing draws (the ratio-estimator SE input).</summary>
        public double ConditionalSumOfSquares;

        /// <summary>The sorted unconditional failure losses (zeros on non-failing draws).</summary>
        public double[] SortedFail = Array.Empty<double>();

        /// <summary>The sorted total losses.</summary>
        public double[] SortedTotal = Array.Empty<double>();
    }

    /// <summary>One consolidated oracle pass's output.</summary>
    private sealed class OracleResult
    {
        /// <summary>The failure-union estimate.</summary>
        public double FailureUnion;

        /// <summary>The binomial standard error of the failure union.</summary>
        public double FailureUnionSe;

        /// <summary>The number of realizations with at least one failure.</summary>
        public long UnionCount;

        /// <summary>The per-rule streams, indexed like <see cref="Rules"/>.</summary>
        public RuleStreams[] ByRule = Array.Empty<RuleStreams>();
    }

    /// <summary>Allocates an oracle result with per-rule sorted arrays.</summary>
    private static OracleResult NewOracleResult()
    {
        var result = new OracleResult { ByRule = new RuleStreams[Rules.Length] };
        for (int r = 0; r < Rules.Length; r++)
        {
            result.ByRule[r] = new RuleStreams
            {
                SortedFail = new double[OracleRealizations],
                SortedTotal = new double[OracleRealizations],
            };
        }
        return result;
    }

    /// <summary>Sorts the per-rule arrays and finalizes the union estimate.</summary>
    /// <param name="result">The result to finalize.</param>
    private static void FinalizeOracle(OracleResult result)
    {
        for (int r = 0; r < Rules.Length; r++)
        {
            Array.Sort(result.ByRule[r].SortedFail);
            Array.Sort(result.ByRule[r].SortedTotal);
        }
        result.FailureUnion = result.UnionCount / (double)OracleRealizations;
        result.FailureUnionSe = Math.Sqrt(result.FailureUnion * (1d - result.FailureUnion) / OracleRealizations);
    }

    /// <summary>
    /// The single-component joint oracle (the legacy 3/4-PFM bodies, consolidated across rules):
    /// one hazard uniform per realization from the 12345 stream, the realization's row of the
    /// perfectly negative multivariate capacity draws (seed 12345), all exceeded modes fail
    /// jointly, and the incremental draw is the combined failure consequence minus the
    /// non-failure consequence (the legacy joint convention, single subtraction).
    /// </summary>
    /// <param name="pfmCount">The failure-mode count (3 or 4).</param>
    /// <returns>The consolidated oracle result.</returns>
    private static OracleResult RunSingleComponentOracle(int pfmCount)
    {
        var (hazardProbabilities, hazardStages) = HazardTable();
        var fragilityStages = new double[pfmCount][];
        var fragilityProbabilities = new double[pfmCount][];
        for (int mode = 0; mode < pfmCount; mode++)
        {
            (fragilityStages[mode], fragilityProbabilities[mode]) = FragilityTable(mode);
        }

        var multivariate = new MultivariateNormal(new double[pfmCount], Equicorrelated(pfmCount, PerfectlyNegativeOffDiagonal(pfmCount)));
        double[,] capacityDraws = multivariate.GenerateRandomValues(OracleRealizations, SharedSeed);
        var hazardStream = new MersenneTwister(HazardSeed);

        var result = NewOracleResult();
        var ruleValues = new double[Rules.Length];
        for (int i = 0; i < OracleRealizations; i++)
        {
            double hazard = Interpolate(hazardProbabilities, hazardStages, hazardStream.NextDouble());
            double nonFailureConsequence = Interpolate(ConsequenceStages, NonFailureValues, hazard);

            int failingCount = 0;
            double sum = 0d, maximum = double.NegativeInfinity, minimum = double.PositiveInfinity;
            for (int mode = 0; mode < pfmCount; mode++)
            {
                double failureProbability = Math.Max(0d, Math.Min(1d,
                    Interpolate(fragilityStages[mode], fragilityProbabilities[mode], hazard)));
                if (Normal.StandardCDF(capacityDraws[i, mode]) <= failureProbability)
                {
                    failingCount++;
                    double consequence = Interpolate(ConsequenceStages, FailureValues[mode], hazard);
                    sum += consequence;
                    maximum = Math.Max(maximum, consequence);
                    minimum = Math.Min(minimum, consequence);
                }
            }

            bool failed = failingCount > 0;
            if (failed) result.UnionCount++;
            ruleValues[0] = failed ? sum : 0d;
            ruleValues[1] = failed ? sum / failingCount : 0d;
            ruleValues[2] = failed ? maximum : 0d;
            ruleValues[3] = failed ? minimum : 0d;
            for (int r = 0; r < Rules.Length; r++)
            {
                double value = ruleValues[r];
                var streams = result.ByRule[r];
                streams.Background.Add(nonFailureConsequence);
                if (failed)
                {
                    streams.Fail.Add(value);
                    streams.NonFail.Add(0d);
                    streams.Total.Add(value);
                    streams.Excess.Add(value - nonFailureConsequence);
                    streams.ConditionalSumOfSquares += value * value;
                    streams.SortedFail[i] = value;
                    streams.SortedTotal[i] = value;
                }
                else
                {
                    streams.Fail.Add(0d);
                    streams.NonFail.Add(nonFailureConsequence);
                    streams.Total.Add(nonFailureConsequence);
                    streams.Excess.Add(0d);
                    streams.SortedFail[i] = 0d;
                    streams.SortedTotal[i] = nonFailureConsequence;
                }
            }
        }

        FinalizeOracle(result);
        return result;
    }

    /// <summary>
    /// The system oracle (the legacy 3/4-element and negative-average bodies, consolidated
    /// across rules): each participating component reads its own column of a correlated
    /// hazard draw and its own column of a shared capacity stream, the failed and surviving
    /// sets combine under each rule, and Total = Fail + NonFail per event.
    /// </summary>
    /// <param name="drawDimension">The dimension of the legacy latent draws (may exceed the component count).</param>
    /// <param name="componentColumns">The draw column read by each participating component (also its legacy PFM index).</param>
    /// <param name="offDiagonal">The equicorrelation of the hazard draw.</param>
    /// <returns>The consolidated oracle result.</returns>
    private static OracleResult RunSystemOracle(int drawDimension, int[] componentColumns, double offDiagonal)
    {
        int componentCount = componentColumns.Length;
        var (hazardProbabilities, hazardStages) = HazardTable();
        var fragilityStages = new double[FragilityMeans.Length][];
        var fragilityProbabilities = new double[FragilityMeans.Length][];
        for (int mode = 0; mode < FragilityMeans.Length; mode++)
        {
            (fragilityStages[mode], fragilityProbabilities[mode]) = FragilityTable(mode);
        }

        var multivariate = new MultivariateNormal(new double[drawDimension], Equicorrelated(drawDimension, offDiagonal));
        double[,] hazardDraws = multivariate.GenerateRandomValues(OracleRealizations, SharedSeed);
        double[,] capacityDraws = new MersenneTwister(SharedSeed).NextDoubles(OracleRealizations, drawDimension);

        var result = NewOracleResult();
        var hazardLevels = new double[componentCount];
        var nonFailureValues = new double[componentCount];
        var componentFailed = new bool[componentCount];
        var componentFail = new double[componentCount];
        var componentExcess = new double[componentCount];

        for (int i = 0; i < OracleRealizations; i++)
        {
            bool anyFailed = false;
            for (int c = 0; c < componentCount; c++)
            {
                int column = componentColumns[c];
                hazardLevels[c] = Interpolate(hazardProbabilities, hazardStages, Normal.StandardCDF(hazardDraws[i, column]));
                nonFailureValues[c] = Interpolate(ConsequenceStages, NonFailureValues, hazardLevels[c]);
                double failureProbability = Math.Max(0d, Math.Min(1d,
                    Interpolate(fragilityStages[column], fragilityProbabilities[column], hazardLevels[c])));
                componentFailed[c] = capacityDraws[i, column] <= failureProbability;
                componentFail[c] = componentFailed[c]
                    ? Interpolate(ConsequenceStages, FailureValues[column], hazardLevels[c])
                    : 0d;
                componentExcess[c] = componentFail[c] - nonFailureValues[c];
                if (componentFailed[c]) anyFailed = true;
            }

            if (anyFailed) result.UnionCount++;
            for (int r = 0; r < Rules.Length; r++)
            {
                var rule = Rules[r];
                double failCombined = CombineSubset(componentFail, componentFailed, wantFailed: true, rule);
                double excessCombined = CombineSubset(componentExcess, componentFailed, wantFailed: true, rule);
                double nonFailCombined = CombineSubset(nonFailureValues, componentFailed, wantFailed: false, rule);
                double backgroundCombined = CombineAllValues(nonFailureValues, rule);
                double total = failCombined + nonFailCombined;

                var streams = result.ByRule[r];
                streams.Fail.Add(failCombined);
                streams.NonFail.Add(nonFailCombined);
                streams.Total.Add(total);
                streams.Excess.Add(anyFailed ? excessCombined : 0d);
                streams.Background.Add(backgroundCombined);
                if (anyFailed) streams.ConditionalSumOfSquares += failCombined * failCombined;
                streams.SortedFail[i] = failCombined;
                streams.SortedTotal[i] = total;
            }
        }

        FinalizeOracle(result);
        return result;
    }

    /// <summary>Combines the values of the failed (or surviving) components under a rule; an empty subset yields zero.</summary>
    /// <param name="values">The per-component values.</param>
    /// <param name="failed">The per-component failure indicators.</param>
    /// <param name="wantFailed">True to combine the failed subset; false for the surviving subset.</param>
    /// <param name="rule">The combination rule.</param>
    /// <returns>The combined value.</returns>
    private static double CombineSubset(double[] values, bool[] failed, bool wantFailed, JointConsequenceType rule)
    {
        double combined = 0d;
        int count = 0;
        for (int c = 0; c < values.Length; c++)
        {
            if (failed[c] != wantFailed) continue;
            if (count == 0)
            {
                combined = values[c];
            }
            else
            {
                combined = rule switch
                {
                    JointConsequenceType.Additive or JointConsequenceType.Average => combined + values[c],
                    JointConsequenceType.Maximum => Math.Max(combined, values[c]),
                    _ => Math.Min(combined, values[c]),
                };
            }
            count++;
        }
        if (count == 0) return 0d;
        return rule == JointConsequenceType.Average ? combined / count : combined;
    }

    /// <summary>Combines every component's value under a rule.</summary>
    /// <param name="values">The per-component values.</param>
    /// <param name="rule">The combination rule.</param>
    /// <returns>The combined value.</returns>
    private static double CombineAllValues(double[] values, JointConsequenceType rule)
    {
        double combined = values[0];
        for (int c = 1; c < values.Length; c++)
        {
            combined = rule switch
            {
                JointConsequenceType.Additive or JointConsequenceType.Average => combined + values[c],
                JointConsequenceType.Maximum => Math.Max(combined, values[c]),
                _ => Math.Min(combined, values[c]),
            };
        }
        return rule == JointConsequenceType.Average ? combined / values.Length : combined;
    }

    #endregion

    #region Assert Drivers

    /// <summary>
    /// Asserts one loss-exceedance ordinate against the oracle's empirical exceedance,
    /// combining the oracle's binomial standard error with an engine-side standard error in
    /// quadrature, after verifying the probe carries at least 100 exceedances.
    /// </summary>
    /// <param name="sortedLosses">The oracle's sorted unconditional losses.</param>
    /// <param name="level">The consequence level probed.</param>
    /// <param name="engineExceedance">The engine's exceedance ordinate at the level.</param>
    /// <param name="engineCount">The engine-side effective sample count (zero for a deterministic engine path).</param>
    /// <param name="message">The assert label.</param>
    private static void AssertExceedance(double[] sortedLosses, double level, double engineExceedance,
        double engineCount, string message)
    {
        int exceeding = 0;
        for (int i = sortedLosses.Length - 1; i >= 0 && sortedLosses[i] > level; i--) exceeding++;
        Assert.IsTrue(exceeding >= 100, $"{message} — the probe must carry at least 100 exceedances (found {exceeding}).");
        double probability = exceeding / (double)sortedLosses.Length;
        double oracleSe = Math.Sqrt(probability * (1d - probability) / sortedLosses.Length);
        double engineSe = engineCount > 0d ? Math.Sqrt(probability * (1d - probability) / engineCount) : 0d;
        Assert.AreEqual(probability, engineExceedance, K * Math.Sqrt(oracleSe * oracleSe + engineSe * engineSe), message);
    }

    /// <summary>
    /// Runs one single-component group end to end: per rule a mean-only engine run asserted
    /// against the consolidated oracle on the joint-family assert catalog — the five summary means, the
    /// failure union and its complement, the failure and total standard deviations, the
    /// conditional mean, the assurance measure, two data-driven loss-exceedance probes, the
    /// value-at-risk, and the conditional value-at-risk.
    /// </summary>
    /// <param name="pfmCount">The failure-mode count (3 or 4).</param>
    private static void RunSingleComponentGroup(int pfmCount)
    {
        var oracle = RunSingleComponentOracle(pfmCount);

        for (int r = 0; r < Rules.Length; r++)
        {
            var rule = Rules[r];
            var streams = oracle.ByRule[r];
            var component = BuildComponent("Dam", ModeIndices(pfmCount), DependencyType.PerfectlyNegative, rule);
            var analysis = new RiskAnalysis(new[] { component }) { Name = $"Joint {pfmCount}-PFM PerfectlyNegative {rule}" };
            analysis.Options.ConsequenceThreshold = Threshold;
            analysis.Options.Alpha = Alpha;
            analysis.Options.LECOutputLength = 1000;
            string label = $"{pfmCount}-PFM PerfectlyNegative {rule}";
            Run(analysis, label);
            var summary = analysis.RiskResults![0]!;
            var failCurve = analysis.MeanRiskResults!.Curves.Fail;

            // The five summary means (v1.0-parity per the means-versus-tails policy).
            Assert.AreEqual(streams.Fail.Mean, summary.Fail.Mean, K * streams.Fail.MeanSe, $"{label}: failure risk mean.");
            Assert.AreEqual(streams.NonFail.Mean, summary.NonFail.Mean, K * streams.NonFail.MeanSe, $"{label}: non-failure risk mean.");
            Assert.AreEqual(streams.Total.Mean, summary.Total.Mean, K * streams.Total.MeanSe, $"{label}: total risk mean.");
            Assert.AreEqual(streams.Excess.Mean, summary.Excess.Mean, K * streams.Excess.MeanSe, $"{label}: incremental (excess) risk mean.");
            Assert.AreEqual(streams.Background.Mean, summary.Background.Mean, K * streams.Background.MeanSe, $"{label}: background risk mean.");

            // The failure union and its complement identity.
            Assert.AreEqual(oracle.FailureUnion, summary.Fail.TotalProbability, K * oracle.FailureUnionSe,
                $"{label}: annualized failure probability.");
            Assert.AreEqual(1d - oracle.FailureUnion, summary.NonFail.TotalProbability, K * oracle.FailureUnionSe,
                $"{label}: the non-failure stream's total probability must complement the failure union.");

            // Dispersion (Monte-Carlo-parity per the means-versus-tails policy).
            Assert.AreEqual(streams.Fail.Sigma, summary.Fail.StandardDeviation, K * streams.Fail.SigmaSe,
                $"{label}: failure risk standard deviation.");
            Assert.AreEqual(streams.Total.Sigma, summary.Total.StandardDeviation, K * streams.Total.SigmaSe,
                $"{label}: total risk standard deviation.");

            // The conditional mean by the first-order ratio-estimator standard error.
            double conditionalMean = streams.Fail.Mean * OracleRealizations / oracle.UnionCount;
            double conditionalSe = Math.Sqrt(Math.Max(0d,
                streams.ConditionalSumOfSquares - conditionalMean * conditionalMean * oracle.UnionCount)) / oracle.UnionCount;
            Assert.AreEqual(conditionalMean, summary.Fail.ConditionalMean, K * conditionalSe,
                $"{label}: conditional mean loss given failure.");

            // The assurance measure and two data-driven loss-exceedance probes on the failure LEC.
            AssertExceedance(streams.SortedFail, Threshold, summary.Fail.ConsequenceThresholdProbability, 0d,
                $"{label}: assurance P(C > {Threshold}).");
            double probe1 = streams.SortedFail[OracleRealizations - (int)Math.Round(0.5d * oracle.UnionCount)];
            double probe2 = streams.SortedFail[OracleRealizations - (int)Math.Round(0.05d * oracle.UnionCount)];
            AssertExceedance(streams.SortedFail, probe1,
                failCurve.LEC.GetYFromX(probe1, Transform.Logarithmic, Transform.Logarithmic), 0d,
                $"{label}: exceedance at the conditional median loss {probe1:G6}.");
            AssertExceedance(streams.SortedFail, probe2,
                failCurve.LEC.GetYFromX(probe2, Transform.Logarithmic, Transform.Logarithmic), 0d,
                $"{label}: exceedance at the conditional 95th-percentile loss {probe2:G6}.");

            // Value-at-risk (density-scaled SE with the output-resolution floor) and the tail mean.
            double[] losses = streams.SortedFail;
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

            Console.WriteLine(
                $"{label}: mean fail {streams.Fail.Mean:G6}/{summary.Fail.Mean:G6}, total {streams.Total.Mean:G6}/{summary.Total.Mean:G6}, " +
                $"excess {streams.Excess.Mean:G6}/{summary.Excess.Mean:G6}, background {streams.Background.Mean:G6}/{summary.Background.Mean:G6}, " +
                $"nonfail {streams.NonFail.Mean:G6}/{summary.NonFail.Mean:G6}, APF {oracle.FailureUnion:G6}/{summary.Fail.TotalProbability:G6}, " +
                $"σF {streams.Fail.Sigma:G6}/{summary.Fail.StandardDeviation:G6}, VaR {valueAtRisk:G6}/{summary.Fail.ValueAtRisk:G6}, " +
                $"CVaR {tailMean:G6}/{summary.Fail.ConditionalValueAtRisk:G6} (oracle/engine)");
        }
    }

    /// <summary>The first <paramref name="count"/> legacy PFM indices.</summary>
    /// <param name="count">The mode count.</param>
    private static int[] ModeIndices(int count)
    {
        var modes = new int[count];
        for (int i = 0; i < count; i++) modes[i] = i;
        return modes;
    }

    /// <summary>
    /// Runs one joint-method system scenario end to end: the oracle pass for the requested
    /// rule, the engine run, and the joint-method assert catalog — the five summary
    /// means with the reported VEGAS error added, the failure union and two curve probes
    /// combined binomially at the recorded evaluation count, the exhaustive mass balance, and
    /// the additive-rule component-mean identity.
    /// </summary>
    /// <param name="oracle">The consolidated oracle result.</param>
    /// <param name="ruleIndex">The across-component rule index into <see cref="Rules"/>.</param>
    /// <param name="componentModes">The per-component legacy PFM indices.</param>
    /// <param name="dependency">The engine dependency option.</param>
    /// <param name="matrix">The engine correlation matrix (correlation-matrix mode only).</param>
    /// <param name="label">The assert label.</param>
    private static void AssertJointSystem(OracleResult oracle, int ruleIndex, int[] componentModes,
        DependencyType dependency, double[,]? matrix, string label)
    {
        var rule = Rules[ruleIndex];
        var streams = oracle.ByRule[ruleIndex];
        var components = new SystemComponent[componentModes.Length];
        for (int c = 0; c < componentModes.Length; c++)
        {
            components[c] = BuildComponent($"Component {c + 1}", new[] { componentModes[c] },
                DependencyType.Independent, JointConsequenceType.Additive);
        }
        var analysis = new RiskAnalysis(components) { Name = label };
        analysis.Options.SystemRiskMethod = SystemRiskType.JointRiskMethod;
        analysis.Options.ComponentHazardDependency = dependency;
        if (matrix != null) analysis.Options.HazardCorrelationMatrix = matrix;
        analysis.Options.JointConsequences = rule;
        analysis.Options.ConsequenceThreshold = Threshold;
        analysis.Options.Alpha = Alpha;
        analysis.Options.VegasTailFocusMode = VegasTailFocusMode.None;
        analysis.Options.LECOutputLength = 1000;
        Run(analysis, label);

        var summary = analysis.RiskResults![0]!;
        var curves = analysis.MeanRiskResults!.Curves;
        double recordedEvaluations = 5d * analysis.Options.FinalEvaluations;
        double engineSe = summary.StandardError;

        Assert.AreEqual(streams.Fail.Mean, summary.Fail.Mean, K * (streams.Fail.MeanSe + engineSe), $"{label}: failure risk mean.");
        Assert.AreEqual(streams.NonFail.Mean, summary.NonFail.Mean, K * (streams.NonFail.MeanSe + engineSe), $"{label}: non-failure risk mean.");
        Assert.AreEqual(streams.Total.Mean, summary.Total.Mean, K * (streams.Total.MeanSe + engineSe), $"{label}: total risk mean.");
        Assert.AreEqual(streams.Excess.Mean, summary.Excess.Mean, K * (streams.Excess.MeanSe + engineSe), $"{label}: incremental (excess) risk mean.");
        Assert.AreEqual(streams.Background.Mean, summary.Background.Mean, K * (streams.Background.MeanSe + engineSe), $"{label}: background risk mean.");

        double unionEngineSe = Math.Sqrt(oracle.FailureUnion * (1d - oracle.FailureUnion) / recordedEvaluations);
        Assert.AreEqual(oracle.FailureUnion, summary.Fail.TotalProbability,
            K * Math.Sqrt(oracle.FailureUnionSe * oracle.FailureUnionSe + unionEngineSe * unionEngineSe),
            $"{label}: system annualized failure probability.");

        double failProbe = streams.SortedFail[OracleRealizations - (int)Math.Round(0.5d * oracle.UnionCount)];
        AssertExceedance(streams.SortedFail, failProbe,
            curves.Fail.LEC.GetYFromX(failProbe, Transform.Logarithmic, Transform.Logarithmic), recordedEvaluations,
            $"{label}: failure exceedance at the conditional median loss {failProbe:G6}.");
        double totalProbe = streams.SortedTotal[OracleRealizations - (int)Math.Round(0.01d * OracleRealizations)];
        AssertExceedance(streams.SortedTotal, totalProbe,
            curves.Total.LEC.GetYFromX(totalProbe, Transform.Logarithmic, Transform.Logarithmic), recordedEvaluations,
            $"{label}: total exceedance at the 1% loss {totalProbe:G6}.");

        // Exact at D = 2; above that the documented Numerics IndependentExclusive convergence
        // shortcut can drift the budget by up to its 1e-4 tolerance (observed ~1e-6 at D = 5 in
        // the system matrix family) — the engine's honest mass-balance witness.
        double massTolerance = componentModes.Length <= 2 ? 1e-9 : 1e-4;
        Assert.AreEqual(1d, curves.Total.MassBalance, massTolerance, $"{label}: the joint Total budget must self-normalize to one.");

        if (rule == JointConsequenceType.Additive)
        {
            double componentMeanSum = 0d;
            for (int c = 0; c < summary.ComponentResults.Count; c++)
            {
                componentMeanSum += summary.ComponentResults[c].Total.Mean;
            }
            // Exact at D = 2; the IndependentExclusive convergence shortcut drifts higher
            // dimensions by the dropped combination mass (observed ≤ ~5e-6 relative; bounded by
            // its 1e-4 tolerance — the same truncation the mass-balance witness surfaces).
            double identityTolerance = (componentModes.Length <= 2 ? 1e-9 : 1e-4) * componentMeanSum;
            Assert.AreEqual(componentMeanSum, summary.Total.Mean, identityTolerance,
                $"{label}: the combination enumeration must preserve the additive-combine mean identity.");
        }

        Console.WriteLine(
            $"{label}: mean fail {streams.Fail.Mean:G6}/{summary.Fail.Mean:G6}, total {streams.Total.Mean:G6}/{summary.Total.Mean:G6}, " +
            $"excess {streams.Excess.Mean:G6}/{summary.Excess.Mean:G6}, background {streams.Background.Mean:G6}/{summary.Background.Mean:G6}, " +
            $"nonfail {streams.NonFail.Mean:G6}/{summary.NonFail.Mean:G6}, union {oracle.FailureUnion:G6}/{summary.Fail.TotalProbability:G6}, " +
            $"VEGAS SE {summary.StandardError:G3} (oracle/engine)");
    }

    #endregion

    /// <summary>
    /// 1 component / 3 perfectly negative joint failure modes — all four consequence rules from
    /// the consolidated legacy draws (legacy <c>Test_1Element_3PFM</c>, additive; N = 10⁶
    /// native). The 3-PFM count sits between the joint family's 2- and 5-PFM scenarios.
    /// </summary>
    [TestMethod]
    public void Test_1Comp3Pfm_PerfectlyNegative_AllRules_VsOracle()
    {
        RunSingleComponentGroup(3);
    }

    /// <summary>
    /// 1 component / 4 perfectly negative joint failure modes — all four consequence rules from
    /// the consolidated legacy draws (legacy <c>Test_1Element_4PFM</c>, additive; N = 10⁶
    /// native).
    /// </summary>
    [TestMethod]
    public void Test_1Comp4Pfm_PerfectlyNegative_AllRules_VsOracle()
    {
        RunSingleComponentGroup(4);
    }

    /// <summary>
    /// 3 perfectly negative system components (r = −1/2 + √ε), one failure mode each, additive
    /// rule (legacy <c>Test_3Element_1PFM</c>; hazard-draw seed 12345; increment-accumulation
    /// typo corrected per the class remarks) — plus the average rule from the same consolidated
    /// draws.
    /// </summary>
    [TestMethod]
    public void Test_3Comp1Pfm_PerfectlyNegative_VsOracle()
    {
        var oracle = RunSystemOracle(3, new[] { 0, 1, 2 }, PerfectlyNegativeOffDiagonal(3));
        AssertJointSystem(oracle, 0, new[] { 0, 1, 2 }, DependencyType.PerfectlyNegative, null,
            "3C1P PerfectlyNegative Additive (joint method)");
        AssertJointSystem(oracle, 1, new[] { 0, 1, 2 }, DependencyType.PerfectlyNegative, null,
            "3C1P PerfectlyNegative Average (joint method)");
    }

    /// <summary>
    /// 4 perfectly negative system components (r = −1/3 + √ε), one failure mode each, additive
    /// rule (legacy <c>Test_4Element_1PFM</c>; hazard-draw seed 12345; increment-accumulation
    /// typo corrected per the class remarks) — plus the maximum rule from the same consolidated
    /// draws.
    /// </summary>
    [TestMethod]
    public void Test_4Comp1Pfm_PerfectlyNegative_VsOracle()
    {
        var oracle = RunSystemOracle(4, new[] { 0, 1, 2, 3 }, PerfectlyNegativeOffDiagonal(4));
        AssertJointSystem(oracle, 0, new[] { 0, 1, 2, 3 }, DependencyType.PerfectlyNegative, null,
            "4C1P PerfectlyNegative Additive (joint method)");
        AssertJointSystem(oracle, 2, new[] { 0, 1, 2, 3 }, DependencyType.PerfectlyNegative, null,
            "4C1P PerfectlyNegative Maximum (joint method)");
    }

    /// <summary>
    /// 2 system components under a NEGATIVE user correlation matrix (r = −0.25), average rule —
    /// the scenario the mislabeled legacy <c>Test_5Element_1PFM_2</c> body computes: its
    /// 5-dimensional equicorrelated draw (r = −1/4) drives only the first two components, so the
    /// realized pair correlation is −1/4. The only negative-correlation-matrix scenario in the
    /// legacy suite.
    /// </summary>
    [TestMethod]
    public void Test_2Comp_NegativeQuarterCorrelation_Average_VsOracle()
    {
        var oracle = RunSystemOracle(5, new[] { 0, 1 }, PerfectlyNegativeOffDiagonal(5));
        AssertJointSystem(oracle, 1, new[] { 0, 1 }, DependencyType.CorrelationMatrix,
            new[,] { { 1d, -0.25d }, { -0.25d, 1d } },
            "2C1P r=-0.25 Average (joint method)");
    }
}
