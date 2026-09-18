using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
/// Mutually exclusive failure modes — the conversion of the legacy
/// <c>Test_MC_MutuallyExclusive</c> family: one system component with 2 or 5 potential failure
/// modes treated as exclusive events, their marginal probabilities normalized whenever the sum
/// exceeds one, verified against an independent Monte Carlo oracle.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Scenario:</b> the shared legacy Bucket-1 model — LnNormal(85, 20) hazard tabulated on a
/// ±8 z-grid at step 0.1; Normal-CDF fragilities tabulated on ±8σ z-grids at step 0.05σ; the
/// exact legacy five-knot consequence curves. Engine and oracle interpolate the SAME tables.
/// </para>
/// <para>
/// <b>Oracle mechanics</b> (ported from the legacy bodies): hazard uniforms from
/// <c>MersenneTwister(12345)</c>; one selection uniform per realization from
/// <c>MersenneTwister(45678)</c>; per realization the marginals are scaled by
/// <c>Probability.MutuallyExclusiveAdjustment</c> (1 while Σp ≤ 1, 1/Σp above) and one mode is
/// selected by the cumulative adjusted probabilities; the incremental draw is
/// max(0, fC − nfC). The legacy bodies declared multivariate objects that were never used —
/// the method has no dependence model (the engine coerces the dependency to Independent), so
/// none is ported. N = 1,000,000. No constants were published for this family, so the asserts
/// are engine-versus-oracle only. The high-hazard region drives Σp above one by construction
/// (both fragilities saturate), so the engine's normalization warning must surface — asserted.
/// </para>
/// <para>
/// <b>Tolerances:</b> k·SE with k = 4 and SEs computed in-run exactly as the joint family
/// (mean SE = σ̂/√N; σ SE by the delta method; probability SEs binomial; value-at-risk
/// density-scaled with the 0.1% relative output-resolution floor; conditional mean by the
/// ratio-estimator first-order SE).
/// </para>
/// </remarks>
[TestClass]
public class MutuallyExclusiveVerification
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

    /// <summary>Builds the engine analysis for one mutually-exclusive scenario from the shared tables.</summary>
    /// <param name="pfmCount">The failure-mode count (2 or 5).</param>
    private static RiskAnalysis BuildAnalysis(int pfmCount)
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
        component.FailureModeMethod = FailureModeMethod.MutuallyExclusive;

        var analysis = new RiskAnalysis(new[] { component }) { Name = $"Mutually exclusive {pfmCount}-PFM" };
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

    /// <summary>One group's oracle output.</summary>
    private sealed class MutuallyExclusiveOracleResult
    {
        /// <summary>The failure probability estimate (the capped exclusive sum realized by selection).</summary>
        public double FailureProbability;

        /// <summary>The binomial standard error of the failure probability.</summary>
        public double FailureProbabilitySe;

        /// <summary>The number of realizations with a selected failure.</summary>
        public long FailureCount;

        /// <summary>The background stream.</summary>
        public Moments Background;

        /// <summary>The non-failure stream.</summary>
        public Moments NonFailure;

        /// <summary>The failure risk stream (zero on non-failing draws).</summary>
        public Moments Fail;

        /// <summary>The total risk stream.</summary>
        public Moments Total;

        /// <summary>The incremental (excess) risk stream.</summary>
        public Moments Excess;

        /// <summary>The sum of squared selected consequences over failing draws (the ratio-estimator SE input).</summary>
        public double ConditionalSumOfSquares;

        /// <summary>The sorted unconditional failure losses (zeros on non-failing draws).</summary>
        public double[] SortedFailureLosses = Array.Empty<double>();
    }

    /// <summary>
    /// The mutually-exclusive oracle: the marginals are scaled by the mutually-exclusive
    /// normalization (1 while Σp ≤ 1, 1/Σp above) and one mode is selected by the cumulative
    /// adjusted probabilities against a single 45678-stream uniform (the exact legacy
    /// selection). Draw order per realization: one hazard uniform, then one selection uniform.
    /// </summary>
    /// <param name="pfmCount">The failure-mode count (2 or 5).</param>
    private static MutuallyExclusiveOracleResult RunOracle(int pfmCount)
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

        var result = new MutuallyExclusiveOracleResult { SortedFailureLosses = new double[OracleRealizations] };
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
                double factor = Probability.MutuallyExclusiveAdjustment(marginals);
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
    /// Runs one group end to end: the oracle pass, one mean-only engine run, the full assert
    /// catalog (five means, failure probability and its complement identity, failure and total
    /// standard deviations, conditional mean, assurance, two data-driven exceedance probes,
    /// value-at-risk, conditional value-at-risk), and the normalization-warning surface pin —
    /// the fragilities saturate at high hazard, so Σp exceeds one by construction and the
    /// engine must report the mutually-exclusive normalization.
    /// </summary>
    /// <param name="pfmCount">The failure-mode count (2 or 5).</param>
    private static void RunGroup(int pfmCount)
    {
        var oracle = RunOracle(pfmCount);

        var analysis = BuildAnalysis(pfmCount);
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated, $"{pfmCount}-PFM mutually exclusive: the analysis must estimate.");
        var summary = analysis.RiskResults![0]!;
        var failCurve = analysis.MeanRiskResults!.Curves.Fail;
        string label = $"{pfmCount}-PFM MutuallyExclusive";

        Assert.AreEqual(oracle.Fail.Mean, summary.Fail.Mean, K * oracle.Fail.MeanSe, $"{label}: failure risk mean.");
        Assert.AreEqual(oracle.NonFailure.Mean, summary.NonFail.Mean, K * oracle.NonFailure.MeanSe, $"{label}: non-failure risk mean.");
        Assert.AreEqual(oracle.Total.Mean, summary.Total.Mean, K * oracle.Total.MeanSe, $"{label}: total risk mean.");
        Assert.AreEqual(oracle.Excess.Mean, summary.Excess.Mean, K * oracle.Excess.MeanSe, $"{label}: incremental (excess) risk mean.");
        Assert.AreEqual(oracle.Background.Mean, summary.Background.Mean, K * oracle.Background.MeanSe, $"{label}: background risk mean.");

        Assert.AreEqual(oracle.FailureProbability, summary.Fail.TotalProbability, K * oracle.FailureProbabilitySe,
            $"{label}: annualized failure probability (the capped exclusive sum).");
        Assert.AreEqual(1d - oracle.FailureProbability, summary.NonFail.TotalProbability, K * oracle.FailureProbabilitySe,
            $"{label}: the non-failure stream's total probability must complement the failure probability.");

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

        Assert.IsTrue(analysis.ComputationWarnings.Any(w => w.Contains("Mutually exclusive failure mode probabilities summed above one")),
            $"{label}: the normalization warning must surface (Σp exceeds one at high hazard by construction).");

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

    /// <summary>2-PFM mutually exclusive (legacy <c>Test_1_Component_2_PFM_ME</c>, line 11).</summary>
    [TestMethod]
    public void Test_2PFM_VsOracle()
    {
        RunGroup(2);
    }

    /// <summary>5-PFM mutually exclusive (legacy <c>Test_1_Component_5_PFM_ME</c>, line 150).</summary>
    [TestMethod]
    public void Test_5PFM_VsOracle()
    {
        RunGroup(5);
    }

    /// <summary>
    /// The reproducibility pins: renames and the XML round-trip are bit-identical on the
    /// mean-only numeric surface (the round-trip also pins that the method's coerced
    /// Independent dependency restores correctly).
    /// </summary>
    [TestMethod]
    public void Test_Reproducibility_RenameAndRoundTrip()
    {
        // Arrange — the baseline 2-PFM run.
        var baseline = BuildAnalysis(2);
        baseline.RunAsync().GetAwaiter().GetResult();

        // Act / Assert — metadata renames are bit-identical.
        var renamed = BuildAnalysis(2);
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
            $"{label}: the failure probability must be bit-identical.");
        CollectionAssert.AreEqual(expected.MeanRiskResults!.Curves.Fail.LECProbabilities, actual.MeanRiskResults!.Curves.Fail.LECProbabilities,
            $"{label}: the failure LEC probabilities must be bit-identical.");
        CollectionAssert.AreEqual(expected.MeanRiskResults.Curves.Fail.LECConsequences, actual.MeanRiskResults.Curves.Fail.LECConsequences,
            $"{label}: the failure LEC consequences must be bit-identical.");
    }
}
