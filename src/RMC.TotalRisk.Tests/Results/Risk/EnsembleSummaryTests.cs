using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data.Statistics;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for <see cref="EnsembleSummary"/> — the scalar-measure percentile
/// reduction: agreement with direct percentile calls, NaN filtering, contribution and
/// diagnostic reduction, the convergence indicators, and the append-only serialization.
/// </summary>
[TestClass]
public class EnsembleSummaryTests
{
    /// <summary>Builds a synthetic ensemble whose system Total means are the given values (with per-realization diagnostics i + 1).</summary>
    private static EnsembleResults Ensemble(params double[] totalMeans)
    {
        var ensemble = new EnsembleResults(totalMeans.Length);
        for (int i = 0; i < totalMeans.Length; i++)
        {
            var realization = new SystemRiskResults();
            realization.Total.Mean = totalMeans[i];
            realization.Total.ValueAtRisk = 10d * totalMeans[i];
            realization.Fail.TotalProbability = 0.01d * (i + 1);
            realization.Fail.HazardThresholdProbability = double.NaN;
            realization.FunctionEvaluations = 100d * (i + 1);
            realization.StandardError = 0.001d * (i + 1);
            ensemble[i] = realization;
        }
        return ensemble;
    }

    /// <summary>
    /// Verifies the reducer against direct percentile calls: every slot equals
    /// <c>Statistics.Percentile</c> over the sorted values at the curve-band convention, the
    /// mean is the sequential mean, an all-NaN measure reduces to NaN, and the convergence
    /// aggregates match hand sums.
    /// </summary>
    [TestMethod]
    public void Test_Compute_ReducerMatchesDirectPercentiles()
    {
        // Arrange — five known values, deliberately unsorted.
        var values = new[] { 5d, 1d, 4d, 2d, 3d };
        var ensemble = Ensemble(values);

        // Act
        var summary = EnsembleSummary.Compute(ensemble, 0.8d)!;

        // Assert — percentile slots vs direct calls at tail = 0.1.
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        Assert.AreEqual(Statistics.Percentile(sorted, 0.1d, true), summary.Lower.Total.Mean, 0d);
        Assert.AreEqual(Statistics.Percentile(sorted, 0.9d, true), summary.Upper.Total.Mean, 0d);
        Assert.AreEqual(Statistics.Percentile(sorted, 0.5d, true), summary.Median.Total.Mean, 0d);
        Assert.AreEqual((5d + 1d + 4d + 2d + 3d) / 5d, summary.Mean.Total.Mean, 0d);

        // A dependent measure reduces on its own axis (VaR = 10 × mean here).
        Assert.AreEqual(Statistics.Percentile(new[] { 10d, 20d, 30d, 40d, 50d }, 0.9d, true), summary.Upper.Total.ValueAtRisk, 0d);

        // The all-NaN measure stays NaN in every slot.
        Assert.IsTrue(double.IsNaN(summary.Lower.Fail.HazardThresholdProbability));
        Assert.IsTrue(double.IsNaN(summary.Mean.Fail.HazardThresholdProbability));

        // Convergence aggregates: Σ and max of the per-realization diagnostics.
        Assert.AreEqual(100d + 200d + 300d + 400d + 500d, summary.Convergence.TotalFunctionEvaluations, 0d);
        Assert.AreEqual(500d, summary.Convergence.MaxFunctionEvaluations, 0d);
        Assert.AreEqual(0.003d, summary.Convergence.MedianStandardError, 1e-15);

        // The APF indicator: mean 0.03, SD/√N of (0.01…0.05).
        var apf = summary.Convergence.Indicators[0];
        Assert.AreEqual("Annualized Failure Probability", apf.Label);
        Assert.AreEqual(0.03d, apf.Mean, 1e-15);
        double sd = Math.Sqrt((0.0004d + 0.0001d + 0d + 0.0001d + 0.0004d) / 4d);
        Assert.AreEqual(sd / Math.Sqrt(5d), apf.EnsembleStandardError, 1e-15);
        Assert.AreEqual(summary.RealizationCount, 5);
    }

    /// <summary>
    /// Verifies the argument and degenerate contracts: an empty ensemble reduces to null,
    /// null-entry realizations are excluded, and an out-of-range width throws.
    /// </summary>
    [TestMethod]
    public void Test_Compute_Contracts()
    {
        // Empty → null.
        Assert.IsNull(EnsembleSummary.Compute(new EnsembleResults(0), 0.9d));

        // Null entries are excluded from the count.
        var sparse = Ensemble(1d, 2d, 3d);
        sparse[1] = null;
        var summary = EnsembleSummary.Compute(sparse, 0.9d)!;
        Assert.AreEqual(2, summary.RealizationCount);
        Assert.AreEqual(2d, summary.Mean.Total.Mean, 0d);

        // Width bounds.
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => EnsembleSummary.Compute(Ensemble(1d), 0d));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => EnsembleSummary.Compute(Ensemble(1d), 1d));
        Assert.ThrowsException<ArgumentNullException>(() => EnsembleSummary.Compute(null!, 0.9d));
    }

    /// <summary>
    /// Verifies the contribution reduction: scopes whose template realization carries a
    /// contribution reduce all three values; scopes without stay null.
    /// </summary>
    [TestMethod]
    public void Test_Compute_ContributionReduction()
    {
        // Arrange — two realizations, one component with one mode carrying contributions.
        var ensemble = new EnsembleResults(2);
        for (int i = 0; i < 2; i++)
        {
            var realization = new SystemRiskResults();
            var component = new ComponentResults
            {
                SystemContribution = new RiskContribution { FailureProbability = 0.1d * (i + 1), FailureMean = 10d * (i + 1), ExcessMean = 5d * (i + 1) },
            };
            component.FailureModeResults.Add(new FailureModeResults
            {
                Contribution = new RiskContribution { FailureProbability = 0.05d * (i + 1), FailureMean = 4d * (i + 1), ExcessMean = 2d * (i + 1) },
            });
            realization.ComponentResults.Add(component);
            ensemble[i] = realization;
        }

        // Act
        var summary = EnsembleSummary.Compute(ensemble, 0.9d)!;

        // Assert
        Assert.AreEqual(0.15d, summary.Mean.ComponentResults[0].SystemContribution!.FailureProbability, 1e-15);
        Assert.AreEqual(15d, summary.Mean.ComponentResults[0].SystemContribution!.FailureMean, 1e-12);
        Assert.AreEqual(6d, summary.Mean.ComponentResults[0].FailureModeResults[0].Contribution!.FailureMean, 1e-12);
        Assert.AreEqual(3d, summary.Mean.ComponentResults[0].FailureModeResults[0].Contribution!.ExcessMean, 1e-12);
    }

    /// <summary>
    /// Verifies the append-only serialization: a stored summary round-trips through the
    /// ensemble JSON, and a payload without the member loads forward as null while
    /// <see cref="EnsembleResults.ComputeSummary"/> rebuilds it from the loaded realizations.
    /// </summary>
    [TestMethod]
    public void Test_Serialization_AppendOnly_AndRecompute()
    {
        // Arrange
        var ensemble = Ensemble(1d, 2d, 3d, 4d);
        ensemble.Summary = ensemble.ComputeSummary(0.8d);

        // Act — round-trip.
        var restored = EnsembleResults.FromJson(ensemble.ToJson())!;

        // Assert
        Assert.IsNotNull(restored.Summary);
        Assert.AreEqual(ensemble.Summary!.Mean.Total.Mean, restored.Summary!.Mean.Total.Mean, 0d);
        Assert.AreEqual(ensemble.Summary.Lower.Total.Mean, restored.Summary.Lower.Total.Mean, 0d);
        Assert.AreEqual(ensemble.Summary.Convergence.TotalFunctionEvaluations, restored.Summary.Convergence.TotalFunctionEvaluations, 0d);

        // The earlier shape (member absent) loads forward as null, and the reduction rebuilds
        // identically from the loaded realizations.
        string legacyJson = System.Text.RegularExpressions.Regex.Replace(ensemble.ToJson(), "\"Summary\":\\{.*\\}(?=,\"|\\}$)", "\"Summary\":null");
        var legacy = EnsembleResults.FromJson(legacyJson)!;
        Assert.IsNull(legacy.Summary);
        var recomputed = legacy.ComputeSummary(0.8d)!;
        Assert.AreEqual(ensemble.Summary.Mean.Total.Mean, recomputed.Mean.Total.Mean, 0d);
        Assert.AreEqual(ensemble.Summary.Upper.Total.ValueAtRisk, recomputed.Upper.Total.ValueAtRisk, 0d);
    }

    /// <summary>
    /// Verifies the unit-weight identity: a weight vector of ones reduces bit-identically to
    /// the unweighted path on every slot (the upstream weighted percentile and mean are
    /// arithmetically identical to the unweighted forms at unit weights), and the weighted
    /// summary identifies itself through <see cref="EnsembleSummary.EffectiveRealizationCount"/>.
    /// </summary>
    [TestMethod]
    public void Test_Compute_UnitWeights_BitIdenticalToUnweighted()
    {
        // Arrange — the same values with and without unit weights. Five points put the
        // equal-weight plotting positions at i/4 — exact binary fractions — so the documented
        // bit-exactness condition holds here.
        var values = new[] { 5d, 1d, 4d, 2d, 3d };
        var unweighted = Ensemble(values);
        var weighted = Ensemble(values);
        weighted.SetRealizationWeights(new[] { 1d, 1d, 1d, 1d, 1d });

        // Act
        var baseline = EnsembleSummary.Compute(unweighted, 0.8d)!;
        var summary = EnsembleSummary.Compute(weighted, 0.8d)!;

        // Assert — bit-identical reductions across representative slots.
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(baseline.Lower.Total.Mean), BitConverter.DoubleToInt64Bits(summary.Lower.Total.Mean));
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(baseline.Upper.Total.Mean), BitConverter.DoubleToInt64Bits(summary.Upper.Total.Mean));
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(baseline.Median.Total.Mean), BitConverter.DoubleToInt64Bits(summary.Median.Total.Mean));
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(baseline.Mean.Total.Mean), BitConverter.DoubleToInt64Bits(summary.Mean.Total.Mean));
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(baseline.Upper.Total.ValueAtRisk), BitConverter.DoubleToInt64Bits(summary.Upper.Total.ValueAtRisk));
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(baseline.Convergence.Indicators[0].Mean), BitConverter.DoubleToInt64Bits(summary.Convergence.Indicators[0].Mean));

        // The indicator standard error agrees algebraically, not bitwise: √(V/N_eff) and
        // √V/√N compose differently in the last unit of precision (documented behavior).
        double baselineError = baseline.Convergence.Indicators[0].EnsembleStandardError;
        Assert.AreEqual(baselineError, summary.Convergence.Indicators[0].EnsembleStandardError, Math.Abs(baselineError) * 1e-15);

        // The self-description: unweighted reductions carry no effective count; unit weights
        // carry N_eff = N.
        Assert.IsNull(baseline.EffectiveRealizationCount);
        Assert.AreEqual(5d, summary.EffectiveRealizationCount!.Value, 0d);
    }

    /// <summary>
    /// Verifies the weighted reducer against direct upstream weighted calls: every percentile
    /// slot equals <c>Statistics.Percentile</c> with the weights, the mean slot equals the
    /// weighted mean, the indicator standard error equals √(V/N_eff) with the
    /// reliability-weighted variance, an all-NaN measure stays NaN, and the effort aggregates
    /// stay unweighted.
    /// </summary>
    [TestMethod]
    public void Test_Compute_WeightedReducerMatchesDirectWeightedCalls()
    {
        // Arrange — unsorted values with unequal raw weights.
        var values = new[] { 5d, 1d, 4d, 2d, 3d };
        var weights = new[] { 1d, 2d, 3d, 4d, 5d };
        var ensemble = Ensemble(values);
        ensemble.SetRealizationWeights(weights);

        // Act
        var summary = EnsembleSummary.Compute(ensemble, 0.8d)!;

        // Assert — percentile and mean slots vs direct weighted calls at the reducer's exact
        // tail level (1 − 0.8)/2, which is one unit below the literal 0.1.
        double tail = (1d - 0.8d) / 2d;
        Assert.AreEqual(Statistics.Percentile(values, tail, weights), summary.Lower.Total.Mean, 0d);
        Assert.AreEqual(Statistics.Percentile(values, 1d - tail, weights), summary.Upper.Total.Mean, 0d);
        Assert.AreEqual(Statistics.Percentile(values, 0.5d, weights), summary.Median.Total.Mean, 0d);
        Assert.AreEqual(Statistics.Mean(values, weights), summary.Mean.Total.Mean, 0d);

        // The indicator: weighted mean and √(V/N_eff) with reliability weights.
        var failureProbabilities = new[] { 0.01d, 0.02d, 0.03d, 0.04d, 0.05d };
        var indicator = summary.Convergence.Indicators[0];
        Assert.AreEqual(Statistics.Mean(failureProbabilities, weights), indicator.Mean, 0d);
        double totalWeight = 15d;
        double sumOfSquares = 1d + 4d + 9d + 16d + 25d;
        double effectiveCount = totalWeight * totalWeight / sumOfSquares;
        double variance = Statistics.Variance(failureProbabilities, weights, WeightType.Reliability);
        Assert.AreEqual(Math.Sqrt(variance / effectiveCount), indicator.EnsembleStandardError, 0d);
        Assert.AreEqual(effectiveCount, summary.EffectiveRealizationCount!.Value, 1e-15);

        // The all-NaN measure stays NaN under weights.
        Assert.IsTrue(double.IsNaN(summary.Lower.Fail.HazardThresholdProbability));
        Assert.IsTrue(double.IsNaN(summary.Mean.Fail.HazardThresholdProbability));

        // The effort aggregates are deliberately unweighted.
        Assert.AreEqual(100d + 200d + 300d + 400d + 500d, summary.Convergence.TotalFunctionEvaluations, 0d);
        Assert.AreEqual((100d + 200d + 300d + 400d + 500d) / 5d, summary.Convergence.MeanFunctionEvaluations, 0d);
        Assert.AreEqual(0.003d, summary.Convergence.MedianStandardError, 1e-15);
    }

    /// <summary>
    /// Verifies the integer-weight replication identity: the weighted mean equals the
    /// replicated-ensemble mean, and the weighted percentile agrees with the replicated
    /// ensemble exactly at the weighted plotting positions (the upstream contract — between
    /// positions the two differ by the replicated interpolation gaps, which no estimator can
    /// close while also reproducing the unweighted method at equal weights).
    /// </summary>
    [TestMethod]
    public void Test_Compute_IntegerWeights_MatchReplicatedEnsemble()
    {
        // Arrange — values {1, 2, 3} with integer weights {2, 1, 3} vs the replicated ensemble.
        var weightedEnsemble = Ensemble(1d, 2d, 3d);
        weightedEnsemble.SetRealizationWeights(new[] { 2d, 1d, 3d });
        var replicated = Ensemble(1d, 1d, 2d, 3d, 3d, 3d);

        // The weighted plotting positions p(i) = A(i)/(A(i) + B(i)) for the sorted points:
        // 1 → 0/(0 + 4) = 0; 2 → 2/(2 + 3) = 0.4; 3 → 3/(3 + 0) = 1.
        var positions = new[] { 0d, 0.4d, 1d };
        var sortedValues = new[] { 1d, 2d, 3d };
        var replicatedSorted = new[] { 1d, 1d, 2d, 3d, 3d, 3d };

        // Act — reduce the weighted ensemble at a width that lands the slots on the positions:
        // tail 0.4 is not a valid band width form, so assert through direct calls instead and
        // through the mean slot of the reduced summary.
        var summary = EnsembleSummary.Compute(weightedEnsemble, 0.8d)!;
        var replicatedSummary = EnsembleSummary.Compute(replicated, 0.8d)!;

        // Assert — the mean slot matches the replicated mean to rounding (Σw·x/Σw vs the
        // sequential replicated sum associate differently).
        Assert.AreEqual(replicatedSummary.Mean.Total.Mean, summary.Mean.Total.Mean, 1e-14);

        // Knot exactness at each weighted plotting position, against the replicated sample.
        for (int i = 0; i < positions.Length; i++)
        {
            double weightedValue = Statistics.Percentile(sortedValues, positions[i], new[] { 2d, 1d, 3d }, dataIsSorted: true);
            double replicatedValue = Statistics.Percentile(replicatedSorted, positions[i], dataIsSorted: true);
            Assert.AreEqual(replicatedValue, weightedValue, 0d, $"Knot {i} at position {positions[i]} must be replication-exact.");
        }

        // The Kish effective count under frequency-like integer weights: (Σw)²/Σw² = 36/14.
        Assert.AreEqual(36d / 14d, summary.EffectiveRealizationCount!.Value, 1e-15);
    }

    /// <summary>
    /// Verifies zero-weight and alignment semantics: zero-weight realizations carry no
    /// percentile mass and no mean contribution, weights align to realization slots so null
    /// entries drop their weights, and a measure whose surviving realizations carry zero total
    /// weight reduces to NaN.
    /// </summary>
    [TestMethod]
    public void Test_Compute_ZeroWeightsAndSlotAlignment()
    {
        // Zero-weight exclusion: only the interior two values carry mass, so the reduction
        // equals the direct weighted call over the full vector and the direct unweighted call
        // over the surviving pair.
        var values = new[] { 1d, 2d, 3d, 4d };
        var zeroEdgeWeights = new[] { 0d, 1d, 1d, 0d };
        var ensemble = Ensemble(values);
        ensemble.SetRealizationWeights(zeroEdgeWeights);
        var summary = EnsembleSummary.Compute(ensemble, 0.8d)!;
        double zeroEdgeTail = (1d - 0.8d) / 2d;
        Assert.AreEqual(Statistics.Percentile(values, zeroEdgeTail, zeroEdgeWeights), summary.Lower.Total.Mean, 0d);
        Assert.AreEqual(Statistics.Percentile(values, 1d - zeroEdgeTail, zeroEdgeWeights), summary.Upper.Total.Mean, 0d);
        Assert.AreEqual(Statistics.Percentile(new[] { 2d, 3d }, zeroEdgeTail, true), summary.Lower.Total.Mean, 0d);
        Assert.AreEqual(2.5d, summary.Mean.Total.Mean, 0d);
        Assert.AreEqual(2d, summary.EffectiveRealizationCount!.Value, 1e-15);

        // Slot alignment: a null realization slot drops its weight from the reduction.
        var sparse = Ensemble(10d, 999d, 30d);
        sparse.SetRealizationWeights(new[] { 1d, 5d, 3d });
        sparse[1] = null;
        var sparseSummary = EnsembleSummary.Compute(sparse, 0.8d)!;
        Assert.AreEqual((1d * 10d + 3d * 30d) / 4d, sparseSummary.Mean.Total.Mean, 0d);
        Assert.AreEqual(16d / 10d, sparseSummary.EffectiveRealizationCount!.Value, 1e-15);

        // Zero surviving weight for one measure: value present only where the weight is zero.
        var degenerate = new EnsembleResults(2);
        var first = new SystemRiskResults();
        first.Total.Mean = 5d;
        first.Fail.TotalProbability = 0.01d;
        var second = new SystemRiskResults();
        second.Total.Mean = double.NaN;
        second.Fail.TotalProbability = 0.02d;
        degenerate[0] = first;
        degenerate[1] = second;
        degenerate.SetRealizationWeights(new[] { 0d, 1d });
        var degenerateSummary = EnsembleSummary.Compute(degenerate, 0.8d)!;
        Assert.IsTrue(double.IsNaN(degenerateSummary.Mean.Total.Mean), "A measure surviving only on zero-weight realizations must reduce to NaN.");
        Assert.IsTrue(double.IsNaN(degenerateSummary.Lower.Total.Mean));
        Assert.AreEqual(0.02d, degenerateSummary.Mean.Fail.TotalProbability, 0d, "A measure with positive surviving weight reduces normally.");
    }

    /// <summary>
    /// Verifies the invalid-weight contract on the reduction path: a raw weight vector that
    /// bypassed the validating setter (the lenient serialization property) throws loudly at
    /// <see cref="EnsembleSummary.Compute"/>.
    /// </summary>
    [TestMethod]
    public void Test_Compute_InvalidRawWeights_Throws()
    {
        // Arrange — a length mismatch assigned through the lenient property.
        var ensemble = Ensemble(1d, 2d, 3d);
        ensemble.RealizationWeights = new[] { 1d, 2d };

        // Act / Assert
        Assert.ThrowsException<ArgumentException>(() => EnsembleSummary.Compute(ensemble, 0.9d));
    }

    /// <summary>
    /// Verifies the weighted summary's serialization: the effective count round-trips through
    /// the ensemble JSON and the field is absent from an unweighted summary's payload.
    /// </summary>
    [TestMethod]
    public void Test_Serialization_EffectiveRealizationCount_AppendOnly()
    {
        // Arrange
        var ensemble = Ensemble(1d, 2d, 3d, 4d);
        ensemble.SetRealizationWeights(new[] { 1d, 2d, 3d, 4d });
        ensemble.Summary = ensemble.ComputeSummary(0.8d);

        // Act
        var restored = EnsembleResults.FromJson(ensemble.ToJson());

        // Assert — the weighted marker round-trips bit-exactly.
        Assert.AreEqual(
            BitConverter.DoubleToInt64Bits(ensemble.Summary!.EffectiveRealizationCount!.Value),
            BitConverter.DoubleToInt64Bits(restored.Summary!.EffectiveRealizationCount!.Value));

        // An unweighted summary's payload omits the field entirely.
        var unweighted = Ensemble(1d, 2d, 3d, 4d);
        unweighted.Summary = unweighted.ComputeSummary(0.8d);
        StringAssert.DoesNotMatch(unweighted.ToJson(), new System.Text.RegularExpressions.Regex("EffectiveRealizationCount"));
    }
}
