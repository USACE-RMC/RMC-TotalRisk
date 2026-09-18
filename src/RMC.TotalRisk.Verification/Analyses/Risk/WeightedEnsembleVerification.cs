using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using Numerics.Sampling;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.Risk;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Verification.Analyses.Risk;

/// <summary>
/// Weighted-ensemble verification: the optional epistemic realization weights and the weighted
/// reductions they gate — verified against an independent re-implementation of the symmetric
/// weighted percentile and the weighted moment formulas, the integer-weight replication
/// identity, the full-engine weighted run (weights never move a sampled realization; the
/// published band scalars and summary equal the independent reduction of the stored
/// per-realization values), the weighted results round trip, and the weighted tolerable-risk
/// confidence against independent exact weight-fraction counts.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Oracle independence:</b> the library reduces weighted ensembles through the Numerics
/// weighted statistics, so the oracle here re-implements those reductions from their
/// definitions — the symmetric weighted percentile places each positive-weight point at
/// p(i) = A(i)/(A(i) + B(i)) with A the weight strictly below and B the weight strictly above,
/// interpolating linearly between adjacent positions; the weighted mean is Σw·x/Σw; the
/// indicator standard error is √(V/N_eff) with the reliability-weighted unbiased variance
/// V = Σw·(x − m)² / (W − Σw²/W) and the Kish effective sample size N_eff = (Σw)²/Σw². No
/// oracle path calls the library's weighted reductions.
/// </para>
/// <para>
/// <b>Tolerances:</b> the oracle and the engine compute the same real-valued reduction through
/// different arrangements (independent prefix sums and a linear bracket search here; the
/// library's prepared-sample form there), so agreement is bounded by accumulated rounding over
/// at most a few hundred summands, not by any statistical error — asserted at 1e-12 relative.
/// The integer-weight replication identity holds exactly at the weighted plotting positions
/// and within the replicated sample's local interpolation gaps between them (the documented
/// upstream contract: no percentile estimator can be replication-exact everywhere while also
/// reproducing the unweighted method at equal weights), so the percentile slots are asserted
/// within the largest adjacent gap of the replicated sorted sample and the means at 1e-13
/// relative (Σw·x versus the replicated sequential sum associate differently). Engine-level
/// bit assertions (per-realization invariance under weights, the stored-weight and
/// manifest-fingerprint round trips) are exact by contract.
/// </para>
/// <para>
/// <b>Engine scenario:</b> the trivial uncertain fixture — stage frequency (0.999 → 0 ft,
/// 0.5 → 10 ft, 0.001 → 30 ft), a triangular-ordinate uncertain fragility rising from 10 ft to
/// 20 ft, linear failure consequences (0 → 0, 30 → 300) and non-failure consequences
/// (30 → 60) — at 100 realizations with deterministic index-varying weights
/// w(i) = 0.25 + ((37·i) mod 11). Coverage note: the per-ordinate weighted band assembly and
/// the scalar band assembly share the identical upstream weighted-percentile call, and the
/// per-ordinate inputs (the interpolated per-realization curves) are not retained after a run,
/// so the independent scalar-reduction oracle plus the fast suite's unit-weight band identity
/// carry the curve-band evidence.
/// </para>
/// </remarks>
[TestClass]
public class WeightedEnsembleVerification
{
    /// <summary>The synthetic ensemble size of the oracle scenarios.</summary>
    private const int SyntheticCount = 50;

    /// <summary>The fixed seed of the synthetic oracle scenarios.</summary>
    private const int SyntheticSeed = 24680;

    /// <summary>The engine scenario's realization count (the options floor).</summary>
    private const int EngineRealizations = 100;

    /// <summary>The rounding tolerance between the oracle and library reductions.</summary>
    private const double RoundingTolerance = 1e-12;

    #region Independent oracle reductions

    /// <summary>
    /// The independent symmetric weighted percentile: positive-weight points sorted by value,
    /// plotting positions p(i) = A(i)/(A(i) + B(i)), linear interpolation between adjacent
    /// positions, the first and last points at 0 and 1.
    /// </summary>
    /// <param name="values">The sample values.</param>
    /// <param name="weights">The aligned non-negative weights.</param>
    /// <param name="level">The percentile level in [0, 1].</param>
    /// <returns>The weighted percentile.</returns>
    private static double OracleWeightedPercentile(IReadOnlyList<double> values, IReadOnlyList<double> weights, double level)
    {
        int count = 0;
        for (int i = 0; i < weights.Count; i++)
        {
            if (weights[i] > 0d) count++;
        }
        var x = new double[count];
        var w = new double[count];
        int j = 0;
        for (int i = 0; i < values.Count; i++)
        {
            if (weights[i] > 0d)
            {
                x[j] = values[i];
                w[j] = weights[i];
                j++;
            }
        }
        Array.Sort(x, w);
        if (count == 1 || level <= 0d) return x[0];
        if (level >= 1d) return x[count - 1];

        double total = 0d;
        for (int i = 0; i < count; i++) total += w[i];
        var positions = new double[count];
        double below = 0d;
        for (int i = 0; i < count; i++)
        {
            double above = total - below - w[i];
            positions[i] = below / (below + above);
            below += w[i];
        }
        for (int i = 1; i < count; i++)
        {
            if (level <= positions[i])
            {
                double fraction = (level - positions[i - 1]) / (positions[i] - positions[i - 1]);
                return x[i - 1] + fraction * (x[i] - x[i - 1]);
            }
        }
        return x[count - 1];
    }

    /// <summary>
    /// The independent weighted mean Σw·x/Σw.
    /// </summary>
    /// <param name="values">The sample values.</param>
    /// <param name="weights">The aligned non-negative weights.</param>
    /// <returns>The weighted mean.</returns>
    private static double OracleWeightedMean(IReadOnlyList<double> values, IReadOnlyList<double> weights)
    {
        double sum = 0d, total = 0d;
        for (int i = 0; i < values.Count; i++)
        {
            sum += weights[i] * values[i];
            total += weights[i];
        }
        return sum / total;
    }

    /// <summary>
    /// The independent Kish effective sample size (Σw)²/Σw².
    /// </summary>
    /// <param name="weights">The non-negative weights.</param>
    /// <returns>The effective sample size.</returns>
    private static double OracleEffectiveCount(IReadOnlyList<double> weights)
    {
        double total = 0d, sumOfSquares = 0d;
        for (int i = 0; i < weights.Count; i++)
        {
            total += weights[i];
            sumOfSquares += weights[i] * weights[i];
        }
        return total * total / sumOfSquares;
    }

    /// <summary>
    /// The independent weighted standard error √(V/N_eff) with the reliability-weighted
    /// unbiased variance V = Σw·(x − m)²/(W − Σw²/W).
    /// </summary>
    /// <param name="values">The sample values.</param>
    /// <param name="weights">The aligned non-negative weights.</param>
    /// <returns>The weighted standard error.</returns>
    private static double OracleWeightedStandardError(IReadOnlyList<double> values, IReadOnlyList<double> weights)
    {
        double mean = OracleWeightedMean(values, weights);
        double total = 0d, sumOfSquares = 0d, centralSum = 0d;
        for (int i = 0; i < values.Count; i++)
        {
            total += weights[i];
            sumOfSquares += weights[i] * weights[i];
            double delta = values[i] - mean;
            centralSum += weights[i] * delta * delta;
        }
        double variance = centralSum / (total - sumOfSquares / total);
        double effectiveCount = total * total / sumOfSquares;
        return Math.Sqrt(variance / effectiveCount);
    }

    #endregion

    #region Fixtures

    /// <summary>
    /// Builds the deterministic index-varying engine weights w(i) = 0.25 + ((37·i) mod 11).
    /// </summary>
    /// <param name="count">The realization count.</param>
    /// <returns>The weights.</returns>
    private static double[] EngineWeights(int count)
    {
        var weights = new double[count];
        for (int i = 0; i < count; i++)
        {
            weights[i] = 0.25d + ((37 * i) % 11);
        }
        return weights;
    }

    /// <summary>
    /// Builds the trivial uncertain single-component scenario at the engine realization count.
    /// </summary>
    /// <returns>The analysis, ready to run.</returns>
    private static RiskAnalysis EngineScenario()
    {
        var hazard = new TabularHazard
        {
            Name = "Stage Frequency",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            NoUncertaintyFunction = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0.999d, new Deterministic(0d)),
                    new UncertainOrdinate(0.5d, new Deterministic(10d)),
                    new UncertainOrdinate(0.001d, new Deterministic(30d)),
                },
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
        };
        var fragility = new TabularResponse
        {
            Name = "Fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(10d, new Triangular(0d, 0.05d, 0.1d)),
                    new UncertainOrdinate(20d, new Triangular(0.7d, 0.9d, 1d)),
                },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Triangular),
        };
        TabularConsequence Consequence(string name, double valueAtThirty) => new TabularConsequence
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(30d, new Deterministic(valueAtThirty)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };

        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = hazard;
        component.AddFailureMode(new FailureMode(null, null, fragility, Consequence("Failure Loss", 300d)));
        component.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Loss", 60d)));

        var analysis = new RiskAnalysis(new[] { component });
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.Realizations = EngineRealizations;
        return analysis;
    }

    /// <summary>
    /// Builds a synthetic ensemble from the fixed-seed generator: Total.Mean uniform on
    /// (0, 10), Fail.TotalProbability uniform on (0, 0.1), Total.ValueAtRisk uniform on
    /// (0, 100) with NaN injected at every seventh slot, and per-realization diagnostics.
    /// </summary>
    /// <param name="values">Receives the Total.Mean values.</param>
    /// <param name="failureProbabilities">Receives the Fail.TotalProbability values.</param>
    /// <param name="valuesAtRisk">Receives the Total.ValueAtRisk values (with the NaN slots).</param>
    /// <returns>The ensemble.</returns>
    private static EnsembleResults SyntheticEnsemble(out double[] values, out double[] failureProbabilities, out double[] valuesAtRisk)
    {
        var prng = new MersenneTwister(SyntheticSeed);
        var ensemble = new EnsembleResults(SyntheticCount);
        values = new double[SyntheticCount];
        failureProbabilities = new double[SyntheticCount];
        valuesAtRisk = new double[SyntheticCount];
        for (int i = 0; i < SyntheticCount; i++)
        {
            values[i] = 10d * prng.NextDouble();
            failureProbabilities[i] = 0.1d * prng.NextDouble();
            valuesAtRisk[i] = i % 7 == 3 ? double.NaN : 100d * prng.NextDouble();
            var realization = new SystemRiskResults();
            realization.Total.Mean = values[i];
            realization.Fail.TotalProbability = failureProbabilities[i];
            realization.Total.ValueAtRisk = valuesAtRisk[i];
            realization.FunctionEvaluations = 100d + i;
            realization.StandardError = 1e-4d * (i + 1);
            ensemble[i] = realization;
        }
        return ensemble;
    }

    /// <summary>
    /// Filters a value/weight pair set to the finite values, in order.
    /// </summary>
    /// <param name="values">The values.</param>
    /// <param name="weights">The aligned weights.</param>
    /// <param name="filteredValues">Receives the finite values.</param>
    /// <param name="filteredWeights">Receives their weights.</param>
    private static void FilterFinite(IReadOnlyList<double> values, IReadOnlyList<double> weights,
        out double[] filteredValues, out double[] filteredWeights)
    {
        var keptValues = new List<double>(values.Count);
        var keptWeights = new List<double>(values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            if (double.IsNaN(values[i])) continue;
            keptValues.Add(values[i]);
            keptWeights.Add(weights[i]);
        }
        filteredValues = keptValues.ToArray();
        filteredWeights = keptWeights.ToArray();
    }

    #endregion

    /// <summary>
    /// Verifies the weighted summary reduction against the independent oracle on a synthetic
    /// fixed-seed ensemble: every percentile slot, the mean slot, the NaN-pairwise filtering,
    /// the zero-weight exclusion, the indicator standard error, and the Kish effective count.
    /// </summary>
    [TestMethod]
    public void Test_WeightedSummary_MatchesIndependentOracle()
    {
        // Arrange — fixed-seed values with NaN injection, and weights carrying exact zeros.
        var ensemble = SyntheticEnsemble(out var values, out var failureProbabilities, out var valuesAtRisk);
        var prng = new MersenneTwister(SyntheticSeed + 1);
        var weights = new double[SyntheticCount];
        for (int i = 0; i < SyntheticCount; i++)
        {
            weights[i] = i % 11 == 5 ? 0d : prng.NextDouble();
        }
        ensemble.SetRealizationWeights(weights);

        // Act
        double width = 0.9d;
        double tail = (1d - width) / 2d;
        var summary = EnsembleSummary.Compute(ensemble, width)!;

        // Assert — the fully-populated measures against the oracle on all four slots.
        Assert.AreEqual(OracleWeightedPercentile(values, weights, tail), summary.Lower.Total.Mean,
            Math.Abs(summary.Lower.Total.Mean) * RoundingTolerance);
        Assert.AreEqual(OracleWeightedPercentile(values, weights, 1d - tail), summary.Upper.Total.Mean,
            Math.Abs(summary.Upper.Total.Mean) * RoundingTolerance);
        Assert.AreEqual(OracleWeightedPercentile(values, weights, 0.5d), summary.Median.Total.Mean,
            Math.Abs(summary.Median.Total.Mean) * RoundingTolerance);
        Assert.AreEqual(OracleWeightedMean(values, weights), summary.Mean.Total.Mean,
            Math.Abs(summary.Mean.Total.Mean) * RoundingTolerance);
        Assert.AreEqual(OracleWeightedPercentile(failureProbabilities, weights, 0.5d), summary.Median.Fail.TotalProbability,
            Math.Abs(summary.Median.Fail.TotalProbability) * RoundingTolerance);

        // The NaN-injected measure reduces over the surviving pairs only.
        FilterFinite(valuesAtRisk, weights, out var finiteValues, out var finiteWeights);
        Assert.AreEqual(OracleWeightedPercentile(finiteValues, finiteWeights, 1d - tail), summary.Upper.Total.ValueAtRisk,
            Math.Abs(summary.Upper.Total.ValueAtRisk) * RoundingTolerance);
        Assert.AreEqual(OracleWeightedMean(finiteValues, finiteWeights), summary.Mean.Total.ValueAtRisk,
            Math.Abs(summary.Mean.Total.ValueAtRisk) * RoundingTolerance);

        // The failure-probability indicator: weighted mean and √(V/N_eff).
        var indicator = summary.Convergence.Indicators[0];
        Assert.AreEqual(OracleWeightedMean(failureProbabilities, weights), indicator.Mean,
            Math.Abs(indicator.Mean) * RoundingTolerance);
        Assert.AreEqual(OracleWeightedStandardError(failureProbabilities, weights), indicator.EnsembleStandardError,
            Math.Abs(indicator.EnsembleStandardError) * RoundingTolerance);

        // The self-description: the Kish effective count of the stored weights.
        Assert.AreEqual(OracleEffectiveCount(weights), summary.EffectiveRealizationCount!.Value,
            summary.EffectiveRealizationCount.Value * RoundingTolerance);

        // The effort aggregates are deliberately unweighted: Σ of 100 + i over fifty slots.
        double expectedEvaluations = 0d;
        for (int i = 0; i < SyntheticCount; i++) expectedEvaluations += 100d + i;
        Assert.AreEqual(expectedEvaluations, summary.Convergence.TotalFunctionEvaluations, 0d);
    }

    /// <summary>
    /// Verifies the integer-weight replication identity: the weighted mean slots match the
    /// replicated-ensemble mean slots to summation rounding, and the weighted percentile slots
    /// sit within the replicated sample's largest local interpolation gap of the replicated
    /// slots (exact at the weighted plotting positions per the upstream contract).
    /// </summary>
    [TestMethod]
    public void Test_IntegerWeights_MatchReplicatedEnsemble()
    {
        // Arrange — fixed-seed values with integer weights 1..4, and the replicated twin.
        var prng = new MersenneTwister(SyntheticSeed + 2);
        var values = new double[SyntheticCount];
        var weights = new double[SyntheticCount];
        var replicatedValues = new List<double>();
        for (int i = 0; i < SyntheticCount; i++)
        {
            values[i] = 10d * prng.NextDouble();
            weights[i] = 1d + ((13 * i) % 4);
            for (int r = 0; r < (int)weights[i]; r++) replicatedValues.Add(values[i]);
        }

        static EnsembleResults Build(IReadOnlyList<double> totals)
        {
            var ensemble = new EnsembleResults(totals.Count);
            for (int i = 0; i < totals.Count; i++)
            {
                var realization = new SystemRiskResults();
                realization.Total.Mean = totals[i];
                ensemble[i] = realization;
            }
            return ensemble;
        }
        var weighted = Build(values);
        weighted.SetRealizationWeights(weights);
        var replicated = Build(replicatedValues);

        // Act
        var weightedSummary = EnsembleSummary.Compute(weighted, 0.9d)!;
        var replicatedSummary = EnsembleSummary.Compute(replicated, 0.9d)!;

        // Assert — means to summation rounding.
        Assert.AreEqual(replicatedSummary.Mean.Total.Mean, weightedSummary.Mean.Total.Mean,
            Math.Abs(replicatedSummary.Mean.Total.Mean) * 1e-13);

        // Percentile slots within the replicated sample's largest adjacent gap.
        var sorted = replicatedValues.ToArray();
        Array.Sort(sorted);
        double maxGap = 0d;
        for (int i = 1; i < sorted.Length; i++)
        {
            maxGap = Math.Max(maxGap, sorted[i] - sorted[i - 1]);
        }
        Assert.AreEqual(replicatedSummary.Lower.Total.Mean, weightedSummary.Lower.Total.Mean, maxGap);
        Assert.AreEqual(replicatedSummary.Median.Total.Mean, weightedSummary.Median.Total.Mean, maxGap);
        Assert.AreEqual(replicatedSummary.Upper.Total.Mean, weightedSummary.Upper.Total.Mean, maxGap);
    }

    /// <summary>
    /// Verifies the weighted engine run end to end: weights never move a sampled realization
    /// (every per-realization summary bit-identical to the unweighted run of the identical
    /// model), the stored ensemble carries the weights and the manifest their fingerprint while
    /// the analysis content identity stays put, and the published band-tree scalars and summary
    /// equal the independent oracle reduction of the stored per-realization values.
    /// </summary>
    [TestMethod]
    public async Task Test_WeightedEngineRun_MatchesIndependentReduction()
    {
        // Arrange — the identical model run unweighted and weighted.
        var weights = EngineWeights(EngineRealizations);
        var unweighted = EngineScenario();
        var weighted = EngineScenario();
        weighted.RealizationWeights = weights;

        // Act
        await unweighted.RunAsync();
        await weighted.RunAsync();

        // Assert — per-realization bit invariance and the stored per-realization values.
        var failureProbabilities = new double[EngineRealizations];
        var totalMeans = new double[EngineRealizations];
        for (int i = 0; i < EngineRealizations; i++)
        {
            var baseline = unweighted.RiskResults![i]!;
            var candidate = weighted.RiskResults![i]!;
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(baseline.Fail.TotalProbability),
                BitConverter.DoubleToInt64Bits(candidate.Fail.TotalProbability), $"Realization {i} failure probability moved under weights.");
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(baseline.Total.Mean),
                BitConverter.DoubleToInt64Bits(candidate.Total.Mean), $"Realization {i} total mean moved under weights.");
            failureProbabilities[i] = candidate.Fail.TotalProbability;
            totalMeans[i] = candidate.Total.Mean;
        }

        // The stored weights, the manifest fingerprint, and the unmoved analysis identity.
        CollectionAssert.AreEqual(weights, weighted.RiskResults!.RealizationWeights);
        Assert.IsNotNull(weighted.RiskResults.Manifest!.RealizationWeightsHash);
        Assert.IsNull(unweighted.RiskResults!.Manifest!.RealizationWeightsHash);
        Assert.AreEqual(unweighted.RiskResults.Manifest.AnalysisContentHash, weighted.RiskResults.Manifest.AnalysisContentHash);

        // The published band-tree scalars against the independent oracle over the stored
        // per-realization values.
        double width = weighted.Options.ConfidenceIntervalWidth;
        double tail = (1d - width) / 2d;
        Assert.AreEqual(OracleWeightedPercentile(failureProbabilities, weights, 0.5d),
            weighted.MedianRiskResults!.Curves.Fail.TotalProbability,
            Math.Abs(weighted.MedianRiskResults.Curves.Fail.TotalProbability) * RoundingTolerance);
        Assert.AreEqual(OracleWeightedPercentile(totalMeans, weights, tail),
            weighted.LowerRiskResults!.Curves.Total.Mean,
            Math.Abs(weighted.LowerRiskResults.Curves.Total.Mean) * RoundingTolerance);
        Assert.AreEqual(OracleWeightedPercentile(totalMeans, weights, 1d - tail),
            weighted.UpperRiskResults!.Curves.Total.Mean,
            Math.Abs(weighted.UpperRiskResults.Curves.Total.Mean) * RoundingTolerance);
        Assert.AreEqual(OracleWeightedMean(totalMeans, weights),
            weighted.MeanRiskResults!.Curves.Total.Mean,
            Math.Abs(weighted.MeanRiskResults.Curves.Total.Mean) * RoundingTolerance);

        // The published summary against the same oracle, plus the effective count.
        var summary = weighted.RiskResults.Summary!;
        Assert.AreEqual(OracleWeightedPercentile(failureProbabilities, weights, 0.5d), summary.Median.Fail.TotalProbability,
            Math.Abs(summary.Median.Fail.TotalProbability) * RoundingTolerance);
        Assert.AreEqual(OracleWeightedMean(totalMeans, weights), summary.Mean.Total.Mean,
            Math.Abs(summary.Mean.Total.Mean) * RoundingTolerance);
        Assert.AreEqual(OracleEffectiveCount(weights), summary.EffectiveRealizationCount!.Value,
            summary.EffectiveRealizationCount.Value * RoundingTolerance);
    }

    /// <summary>
    /// Verifies the weighted results round trip at engine scale: the stored weights, the
    /// manifest fingerprint, and the weighted summary survive the JSON and compressed-byte
    /// round trips bit-faithfully, and the unweighted payload of the same scenario carries no
    /// weight fields at all.
    /// </summary>
    [TestMethod]
    public async Task Test_WeightedResults_RoundTripAtScale()
    {
        // Arrange — one weighted run.
        var weights = EngineWeights(EngineRealizations);
        var analysis = EngineScenario();
        analysis.RealizationWeights = weights;
        await analysis.RunAsync();
        var results = analysis.RiskResults!;

        // Act
        var fromJson = EnsembleResults.FromJson(results.ToJson());
        var fromBytes = EnsembleResults.FromCompressedBytes(results.ToCompressedBytes());

        // Assert — bit-faithful weights, fingerprint, and weighted summary on both paths.
        foreach (var restored in new[] { fromJson, fromBytes })
        {
            Assert.AreEqual(0, restored.LoadDiagnostics.Count);
            Assert.AreEqual(EngineRealizations, restored.RealizationWeights!.Length);
            for (int i = 0; i < EngineRealizations; i++)
            {
                Assert.AreEqual(BitConverter.DoubleToInt64Bits(weights[i]),
                    BitConverter.DoubleToInt64Bits(restored.RealizationWeights[i]));
            }
            Assert.AreEqual(results.Manifest!.RealizationWeightsHash, restored.Manifest!.RealizationWeightsHash);
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(results.Summary!.EffectiveRealizationCount!.Value),
                BitConverter.DoubleToInt64Bits(restored.Summary!.EffectiveRealizationCount!.Value));
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(results.Summary.Median.Fail.TotalProbability),
                BitConverter.DoubleToInt64Bits(restored.Summary.Median.Fail.TotalProbability));
        }

        // The unweighted payload of the same scenario carries no weight fields.
        var unweighted = EngineScenario();
        await unweighted.RunAsync();
        string unweightedJson = unweighted.RiskResults!.ToJson();
        StringAssert.DoesNotMatch(unweightedJson, new System.Text.RegularExpressions.Regex("RealizationWeights"));
        StringAssert.DoesNotMatch(unweightedJson, new System.Text.RegularExpressions.Regex("EffectiveRealizationCount"));
    }

    /// <summary>
    /// Verifies the weighted tolerable-risk confidence behind the full engine: configured
    /// criteria move the analysis content identity (they are hashed options content) while
    /// every sampled realization stays bit-identical, and each published exceedance
    /// probability equals the independent weight-fraction count Σw·1[x &gt; c]/Σw over the
    /// stored per-realization measures — exact, no tolerance.
    /// </summary>
    [TestMethod]
    public async Task Test_WeightedTolerableRiskConfidence_MatchesIndependentCounts()
    {
        // Arrange — a criteria-free weighted reference run fixes the per-realization measures.
        var weights = EngineWeights(EngineRealizations);
        var reference = EngineScenario();
        reference.RealizationWeights = weights;
        await reference.RunAsync();
        var excessMeans = new double[EngineRealizations];
        var failureProbabilities = new double[EngineRealizations];
        for (int i = 0; i < EngineRealizations; i++)
        {
            excessMeans[i] = reference.RiskResults![i]!.Excess.Mean;
            failureProbabilities[i] = reference.RiskResults[i]!.Fail.TotalProbability;
        }
        double excessThreshold = excessMeans[EngineRealizations / 2];
        double failureThreshold = failureProbabilities[EngineRealizations / 3];

        // Act — the criteria'd weighted run of the identical model and seed.
        var analysis = EngineScenario();
        analysis.RealizationWeights = weights;
        analysis.Options.TolerableRiskCriteria.Add(
            new TolerableRiskCriterion(RiskMeasure.Mean, RiskType.Excess, 0, excessThreshold));
        analysis.Options.TolerableRiskCriteria.Add(
            new TolerableRiskCriterion(RiskMeasure.TotalProbability, RiskType.Fail, 0, failureThreshold));
        await analysis.RunAsync();

        // Assert — criteria move the analysis identity but never a sampled realization.
        Assert.AreNotEqual(reference.RiskResults!.Manifest!.AnalysisContentHash,
            analysis.RiskResults!.Manifest!.AnalysisContentHash,
            "Configured criteria are hashed options content — the analysis identity must move.");
        for (int i = 0; i < EngineRealizations; i++)
        {
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(excessMeans[i]),
                BitConverter.DoubleToInt64Bits(analysis.RiskResults[i]!.Excess.Mean),
                $"Realization {i} moved under configured criteria.");
        }

        // The independent weight-fraction counts, exact against the published block.
        var block = analysis.RiskResults.Summary!.TolerableRiskConfidence!;
        Assert.AreEqual(2, block.Count);
        double exceedingExcess = 0d, exceedingFailure = 0d, totalWeight = 0d;
        for (int i = 0; i < EngineRealizations; i++)
        {
            totalWeight += weights[i];
            if (excessMeans[i] > excessThreshold) exceedingExcess += weights[i];
            if (failureProbabilities[i] > failureThreshold) exceedingFailure += weights[i];
        }
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(exceedingExcess / totalWeight),
            BitConverter.DoubleToInt64Bits(block[0].ExceedanceProbability));
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(exceedingFailure / totalWeight),
            BitConverter.DoubleToInt64Bits(block[1].ExceedanceProbability));
        Assert.AreEqual("Mean", block[0].Measure);
        Assert.AreEqual("TotalProbability", block[1].Measure);
    }
}
