using System;
using System.Collections.Generic;
using Numerics.Data.Statistics;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results;
using RMC.TotalRisk.Analyses.Risk;

namespace RMC.TotalRisk.Analyses.CostBenefit.Support
{
    /// <summary>
    /// The weighted arithmetic of the Tier-2 epistemic decision criteria over stored
    /// full-uncertainty ensembles: criterion sampling through the engine's scope-and-measure
    /// switches, the tolerable-risk exceedance fraction, weighted moments and percentiles,
    /// the epistemic tail average, the weighted extremes, and the Hurwicz blend.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// <see cref="ExceedanceFraction"/> mirrors the tolerable-risk confidence evaluation on
    /// <c>RiskAnalysis.EvaluateTolerableRiskCriteria</c> token for token — null realization
    /// slots are skipped, NaN measures are filtered pairwise with their weights, exceedance is
    /// strict, and a zero surviving weight reports NaN — so the chance-constraint surface is
    /// bit-consistent with the published confidence block by construction. The weighted
    /// reductions call the same upstream statistics the ensemble summary uses (the weighted
    /// vector-percentile call, the weighted mean, and the reliability-weight variance), and
    /// every helper treats zero surviving weight as unmeasurable rather than inventing a
    /// value.
    /// </para>
    /// </remarks>
    internal static class EpistemicCriteriaEngine
    {
        /// <summary>
        /// Samples one criterion over an ensemble into caller buffers: per-realization values
        /// through the engine's scope-and-measure switches with pairwise NaN filtering, weights
        /// carried alongside (equal weights when the ensemble declares none).
        /// </summary>
        /// <param name="summaries">The per-realization summaries, by realization slot.</param>
        /// <param name="weights">The realization weights parallel to the slots, or null for equal weights.</param>
        /// <param name="riskType">The stream.</param>
        /// <param name="consequenceType">The consequence-type position.</param>
        /// <param name="measure">The measure.</param>
        /// <param name="values">The surviving-value buffer (at least the slot count).</param>
        /// <param name="sampleWeights">The surviving-weight buffer, parallel to the values.</param>
        /// <returns>The surviving sample count.</returns>
        internal static int CriterionSample(IReadOnlyList<SystemRiskResults?> summaries, double[]? weights,
            RiskType riskType, int consequenceType, RiskMeasure measure, double[] values,
            double[] sampleWeights)
        {
            int used = 0;
            for (int i = 0; i < summaries.Count; i++)
            {
                var summary = summaries[i];
                if (summary == null) continue;
                var stream = RiskAnalysis.SelectScope(summary, -1, -1, riskType, consequenceType);
                double value = stream != null ? RiskAnalysis.ExtractMeasure(stream, measure) : double.NaN;
                if (double.IsNaN(value)) continue;
                values[used] = value;
                sampleWeights[used] = weights == null ? 1d : weights[i];
                used++;
            }
            return used;
        }

        /// <summary>
        /// The weighted strict threshold-exceedance fraction — the arithmetic of
        /// <c>RiskAnalysis.EvaluateTolerableRiskCriteria</c> mirrored token for token: null
        /// slots skipped, NaN measures dropped with their weights, strict exceedance, and NaN
        /// at zero surviving weight.
        /// </summary>
        /// <param name="summaries">The per-realization summaries, by realization slot.</param>
        /// <param name="weights">The realization weights parallel to the slots, or null for equal weights.</param>
        /// <param name="riskType">The stream.</param>
        /// <param name="consequenceType">The consequence-type position.</param>
        /// <param name="measure">The measure.</param>
        /// <param name="threshold">The threshold.</param>
        /// <returns>The exceedance fraction, or NaN at zero surviving weight.</returns>
        internal static double ExceedanceFraction(IReadOnlyList<SystemRiskResults?> summaries,
            double[]? weights, RiskType riskType, int consequenceType, RiskMeasure measure,
            double threshold)
        {
            double exceedingWeight = 0d;
            double totalWeight = 0d;
            for (int i = 0; i < summaries.Count; i++)
            {
                var summary = summaries[i];
                if (summary == null) continue;
                var stream = RiskAnalysis.SelectScope(summary, -1, -1, riskType, consequenceType);
                double value = stream != null ? RiskAnalysis.ExtractMeasure(stream, measure) : double.NaN;
                if (double.IsNaN(value)) continue;
                double weight = weights == null ? 1d : weights[i];
                totalWeight += weight;
                if (value > threshold) exceedingWeight += weight;
            }
            return totalWeight > 0d ? exceedingWeight / totalWeight : double.NaN;
        }

        /// <summary>
        /// The weighted fraction of realizations whose measure is at or above the threshold —
        /// the satisfaction probability of a greater-or-equal constraint (the less-or-equal
        /// side derives from the exceedance fraction as its complement).
        /// </summary>
        /// <param name="summaries">The per-realization summaries, by realization slot.</param>
        /// <param name="weights">The realization weights parallel to the slots, or null for equal weights.</param>
        /// <param name="riskType">The stream.</param>
        /// <param name="consequenceType">The consequence-type position.</param>
        /// <param name="measure">The measure.</param>
        /// <param name="threshold">The threshold.</param>
        /// <returns>The at-or-above fraction, or NaN at zero surviving weight.</returns>
        internal static double SatisfactionFraction(IReadOnlyList<SystemRiskResults?> summaries,
            double[]? weights, RiskType riskType, int consequenceType, RiskMeasure measure,
            double threshold)
        {
            double satisfiedWeight = 0d;
            double totalWeight = 0d;
            for (int i = 0; i < summaries.Count; i++)
            {
                var summary = summaries[i];
                if (summary == null) continue;
                var stream = RiskAnalysis.SelectScope(summary, -1, -1, riskType, consequenceType);
                double value = stream != null ? RiskAnalysis.ExtractMeasure(stream, measure) : double.NaN;
                if (double.IsNaN(value)) continue;
                double weight = weights == null ? 1d : weights[i];
                totalWeight += weight;
                if (value >= threshold) satisfiedWeight += weight;
            }
            return totalWeight > 0d ? satisfiedWeight / totalWeight : double.NaN;
        }

        /// <summary>
        /// The weighted mean over a surviving sample, through the upstream weighted-mean call.
        /// </summary>
        /// <param name="values">The surviving values.</param>
        /// <param name="weights">The surviving weights, parallel to the values.</param>
        /// <param name="count">The surviving count.</param>
        /// <returns>The weighted mean, or NaN at zero surviving weight.</returns>
        internal static double WeightedMean(double[] values, double[] weights, int count)
        {
            if (!HasWeight(weights, count)) return double.NaN;
            return Statistics.Mean(Trim(values, count), Trim(weights, count));
        }

        /// <summary>
        /// The weighted variance over a surviving sample, through the upstream
        /// reliability-weight variance call.
        /// </summary>
        /// <param name="values">The surviving values.</param>
        /// <param name="weights">The surviving weights, parallel to the values.</param>
        /// <param name="count">The surviving count.</param>
        /// <returns>The weighted variance, or NaN when unsupported.</returns>
        internal static double WeightedVariance(double[] values, double[] weights, int count)
        {
            if (!HasWeight(weights, count)) return double.NaN;
            return Statistics.Variance(Trim(values, count), Trim(weights, count), WeightType.Reliability);
        }

        /// <summary>
        /// The weighted percentiles over a surviving sample, through the upstream weighted
        /// vector-percentile call (unsorted data).
        /// </summary>
        /// <param name="values">The surviving values.</param>
        /// <param name="weights">The surviving weights, parallel to the values.</param>
        /// <param name="count">The surviving count.</param>
        /// <param name="levels">The percentile levels.</param>
        /// <returns>The percentiles, or NaN entries at zero surviving weight.</returns>
        internal static double[] WeightedPercentiles(double[] values, double[] weights, int count,
            IList<double> levels)
        {
            if (!HasWeight(weights, count))
            {
                var empty = new double[levels.Count];
                for (int i = 0; i < empty.Length; i++) empty[i] = double.NaN;
                return empty;
            }
            return Statistics.Percentile(Trim(values, count), levels, Trim(weights, count),
                dataIsSorted: false);
        }

        /// <summary>
        /// The epistemic tail average — the weighted mean of the adverse tail carrying exactly
        /// the tail level's share of the total weight: realizations are taken in adverseness
        /// order (value ties break by realization index), the boundary realization contributes
        /// the exact partial weight that completes the target, and the divisor is the target
        /// itself.
        /// </summary>
        /// <param name="values">The surviving values.</param>
        /// <param name="weights">The surviving weights, parallel to the values.</param>
        /// <param name="count">The surviving count.</param>
        /// <param name="tailAlpha">The tail level, in (0, 1).</param>
        /// <param name="direction">The optimization direction (the adverse tail opposes it).</param>
        /// <returns>The tail average, or NaN when unmeasurable.</returns>
        internal static double TailAverage(double[] values, double[] weights, int count,
            double tailAlpha, ObjectiveDirection direction)
        {
            if (count == 0) return double.NaN;
            double totalWeight = 0d;
            for (int i = 0; i < count; i++) totalWeight += weights[i];
            if (totalWeight <= 0d) return double.NaN;
            double target = tailAlpha * totalWeight;
            if (!(target > 0d)) return double.NaN;

            var order = new int[count];
            for (int i = 0; i < count; i++) order[i] = i;
            bool adverseIsLarge = direction != ObjectiveDirection.Maximize;
            Array.Sort(order, (a, b) =>
            {
                int byValue = adverseIsLarge ? values[b].CompareTo(values[a]) : values[a].CompareTo(values[b]);
                return byValue != 0 ? byValue : a.CompareTo(b);
            });

            double taken = 0d;
            double weightedSum = 0d;
            for (int k = 0; k < count; k++)
            {
                int index = order[k];
                double take = Math.Min(weights[index], target - taken);
                if (take <= 0d) break;
                weightedSum += take * values[index];
                taken += take;
            }
            return weightedSum / target;
        }

        /// <summary>
        /// The direction-aware weighted extreme, ignoring zero-weight realizations.
        /// </summary>
        /// <param name="values">The surviving values.</param>
        /// <param name="weights">The surviving weights, parallel to the values.</param>
        /// <param name="count">The surviving count.</param>
        /// <param name="worst">True for the adverse extreme, false for the favorable one.</param>
        /// <param name="direction">The optimization direction.</param>
        /// <returns>The extreme over positive-weight realizations, or NaN when none carry weight.</returns>
        internal static double WeightedExtreme(double[] values, double[] weights, int count,
            bool worst, ObjectiveDirection direction)
        {
            bool wantLarge = worst != (direction == ObjectiveDirection.Maximize);
            double result = double.NaN;
            for (int i = 0; i < count; i++)
            {
                if (!(weights[i] > 0d)) continue;
                if (double.IsNaN(result))
                {
                    result = values[i];
                }
                else if (wantLarge ? values[i] > result : values[i] < result)
                {
                    result = values[i];
                }
            }
            return result;
        }

        /// <summary>
        /// The Hurwicz optimism blend α·best + (1 − α)·worst; the endpoints reproduce the
        /// extremes bit-exactly for finite arguments.
        /// </summary>
        /// <param name="best">The favorable extreme.</param>
        /// <param name="worst">The adverse extreme.</param>
        /// <param name="alpha">The optimism coefficient, in [0, 1].</param>
        /// <returns>The blend.</returns>
        internal static double HurwiczBlend(double best, double worst, double alpha)
        {
            return alpha * best + (1d - alpha) * worst;
        }

        /// <summary>
        /// The Kish effective sample size (Σw)²/Σw² of a surviving weight sample.
        /// </summary>
        /// <param name="weights">The surviving weights.</param>
        /// <param name="count">The surviving count.</param>
        /// <returns>The effective count, or NaN at zero weight.</returns>
        internal static double KishEffectiveCount(double[] weights, int count)
        {
            double sum = 0d;
            double sumSquares = 0d;
            for (int i = 0; i < count; i++)
            {
                sum += weights[i];
                sumSquares += weights[i] * weights[i];
            }
            return sumSquares > 0d ? sum * sum / sumSquares : double.NaN;
        }

        /// <summary>
        /// Whether a surviving sample carries positive total weight.
        /// </summary>
        /// <param name="weights">The surviving weights.</param>
        /// <param name="count">The surviving count.</param>
        /// <returns>True when the total weight is positive.</returns>
        private static bool HasWeight(double[] weights, int count)
        {
            if (count == 0) return false;
            double total = 0d;
            for (int i = 0; i < count; i++) total += weights[i];
            return total > 0d;
        }

        /// <summary>
        /// Copies the surviving prefix of a buffer into an exact-size array for the upstream
        /// statistics calls.
        /// </summary>
        /// <param name="buffer">The buffer.</param>
        /// <param name="count">The surviving count.</param>
        /// <returns>The exact-size copy.</returns>
        private static double[] Trim(double[] buffer, int count)
        {
            var trimmed = new double[count];
            Array.Copy(buffer, trimmed, count);
            return trimmed;
        }
    }
}
