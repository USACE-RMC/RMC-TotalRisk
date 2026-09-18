using System;

namespace RMC.TotalRisk.Core.Support
{
    /// <summary>
    /// The given-data value-of-information estimator: weighted equal-frequency conditioning of a
    /// stored output sample on one knowledge-input column, yielding the epistemic variance a
    /// study resolving that input could remove and the expected movement of a tolerable-risk
    /// confidence statement — computed entirely from stored realizations, with no re-simulation.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The estimator is the Plischke-style given-data construction: realizations are sorted by
    /// the input column and partitioned into equal-weight bins, and the variance of the weighted
    /// conditional output means across bins estimates Var(E[output | input]) — the first-order
    /// main effect in output units. The partition is exact, so the weighted total variance
    /// decomposes exactly into the between-bin and within-bin parts. Two bias terms bound the
    /// estimate and are documented with the public surface: a discretization bias of order
    /// 1/bins² (the conditional mean varies inside a bin) and a noise bias of order bins/n
    /// (each bin mean carries sampling error that inflates the between-bin variance). Weights
    /// follow the reliability convention of the ensemble reductions: population-form variances
    /// normalized by the total weight.
    /// </para>
    /// </remarks>
    internal static class ValueOfInformationEstimator
    {
        /// <summary>
        /// One column's main-effect decomposition of the stored output sample.
        /// </summary>
        internal readonly struct MainEffectResult
        {
            /// <summary>
            /// Initializes the decomposition.
            /// </summary>
            /// <param name="resolvableVariance">The between-bin (resolvable) variance.</param>
            /// <param name="totalVariance">The total weighted variance of the filtered pairs.</param>
            /// <param name="pairs">The valid realization pairs behind the estimate.</param>
            public MainEffectResult(double resolvableVariance, double totalVariance, int pairs)
            {
                ResolvableVariance = resolvableVariance;
                TotalVariance = totalVariance;
                Pairs = pairs;
            }

            /// <summary>
            /// The variance of the weighted conditional output means across the input bins —
            /// the epistemic variance a study resolving the input could remove. NaN when too
            /// few valid pairs exist.
            /// </summary>
            public double ResolvableVariance { get; }

            /// <summary>
            /// The total weighted output variance over the same filtered pairs; the between-bin
            /// and within-bin parts sum to it exactly.
            /// </summary>
            public double TotalVariance { get; }

            /// <summary>
            /// The valid realization pairs behind the estimate (after pairwise NaN filtering).
            /// </summary>
            public int Pairs { get; }
        }

        /// <summary>
        /// Estimates the main effect of one input column on the output sample: the variance of
        /// the weighted conditional output means across equal-weight input bins.
        /// </summary>
        /// <param name="inputs">The input column, parallel to the outputs.</param>
        /// <param name="outputs">The stored per-realization outputs.</param>
        /// <param name="weights">The realization weights parallel to the outputs, or null for equal weights.</param>
        /// <param name="bins">The bin count (at least two).</param>
        /// <returns>
        /// The decomposition; <see cref="MainEffectResult.ResolvableVariance"/> is NaN when
        /// fewer valid pairs remain than bins, or when the surviving weight is zero.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown when either sample is null.</exception>
        /// <exception cref="ArgumentException">Thrown when the samples differ in length or the weights do.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the bin count is below two.</exception>
        public static MainEffectResult MainEffect(double[] inputs, double[] outputs, double[]? weights, int bins)
        {
            var order = PrepareOrder(inputs, outputs, weights, bins, out int pairs);
            if (pairs == 0) return new MainEffectResult(double.NaN, double.NaN, 0);

            // The weighted grand mean over the valid pairs.
            double totalWeight = 0d;
            double mean = 0d;
            for (int k = 0; k < pairs; k++)
            {
                int i = order[k];
                double w = weights == null ? 1d : weights[i];
                totalWeight += w;
                mean += w * outputs[i];
            }
            if (totalWeight <= 0d) return new MainEffectResult(double.NaN, double.NaN, pairs);
            mean /= totalWeight;

            double totalVariance = 0d;
            for (int k = 0; k < pairs; k++)
            {
                int i = order[k];
                double w = weights == null ? 1d : weights[i];
                double deviation = outputs[i] - mean;
                totalVariance += w * deviation * deviation;
            }
            totalVariance /= totalWeight;

            if (pairs < bins) return new MainEffectResult(double.NaN, totalVariance, pairs);

            // A bit-constant input column (a pinned function's draws, or a degenerate design)
            // resolves nothing: binning its arbitrary tie order would otherwise report a
            // spurious bins/n-scale noise floor as resolvable variance.
            if (inputs[order[0]] == inputs[order[pairs - 1]])
            {
                return new MainEffectResult(0d, totalVariance, pairs);
            }

            // Equal-weight bins over the sorted order; the between-bin variance of the
            // conditional means is the resolvable part.
            double betweenVariance = 0d;
            int start = 0;
            double consumedWeight = 0d;
            for (int b = 0; b < bins && start < pairs; b++)
            {
                double target = totalWeight * (b + 1) / bins;
                int end = start;
                double binWeight = 0d;
                double binMean = 0d;
                while (end < pairs && (binWeight <= 0d || consumedWeight + binWeight < target || b == bins - 1))
                {
                    int i = order[end];
                    double w = weights == null ? 1d : weights[i];
                    binWeight += w;
                    binMean += w * outputs[i];
                    end++;
                }
                consumedWeight += binWeight;
                if (binWeight > 0d)
                {
                    binMean /= binWeight;
                    double deviation = binMean - mean;
                    betweenVariance += binWeight * deviation * deviation;
                }
                start = end;
            }
            betweenVariance /= totalWeight;
            return new MainEffectResult(betweenVariance, totalVariance, pairs);
        }

        /// <summary>
        /// Estimates the expected movement of a tolerable-risk confidence statement under
        /// perfect information about one input column: the weighted mean absolute difference
        /// between the per-bin exceedance fractions and the overall fraction.
        /// </summary>
        /// <param name="inputs">The input column, parallel to the outputs.</param>
        /// <param name="outputs">The stored per-realization criterion measures.</param>
        /// <param name="weights">The realization weights parallel to the outputs, or null for equal weights.</param>
        /// <param name="bins">The bin count (at least two).</param>
        /// <param name="threshold">The criterion threshold; exceedance is strict, matching the published confidence.</param>
        /// <returns>
        /// The expected absolute confidence movement, or NaN when fewer valid pairs remain than
        /// bins or the surviving weight is zero.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown when either sample is null.</exception>
        /// <exception cref="ArgumentException">Thrown when the samples differ in length or the weights do.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the bin count is below two.</exception>
        public static double ExceedanceMovement(double[] inputs, double[] outputs, double[]? weights, int bins, double threshold)
        {
            var order = PrepareOrder(inputs, outputs, weights, bins, out int pairs);
            if (pairs < bins) return double.NaN;

            double totalWeight = 0d;
            double exceedingWeight = 0d;
            for (int k = 0; k < pairs; k++)
            {
                int i = order[k];
                double w = weights == null ? 1d : weights[i];
                totalWeight += w;
                if (outputs[i] > threshold) exceedingWeight += w;
            }
            if (totalWeight <= 0d) return double.NaN;
            double baseline = exceedingWeight / totalWeight;

            // A bit-constant input column resolves nothing (the pinned-function convention —
            // see the main-effect guard).
            if (inputs[order[0]] == inputs[order[pairs - 1]]) return 0d;

            double movement = 0d;
            int start = 0;
            double consumedWeight = 0d;
            for (int b = 0; b < bins && start < pairs; b++)
            {
                double target = totalWeight * (b + 1) / bins;
                int end = start;
                double binWeight = 0d;
                double binExceeding = 0d;
                while (end < pairs && (binWeight <= 0d || consumedWeight + binWeight < target || b == bins - 1))
                {
                    int i = order[end];
                    double w = weights == null ? 1d : weights[i];
                    binWeight += w;
                    if (outputs[i] > threshold) binExceeding += w;
                    end++;
                }
                consumedWeight += binWeight;
                if (binWeight > 0d)
                {
                    movement += binWeight * Math.Abs(binExceeding / binWeight - baseline);
                }
                start = end;
            }
            return movement / totalWeight;
        }

        /// <summary>
        /// Validates the samples and produces the deterministic sorted order of the valid pairs:
        /// pairwise NaN filtering, then a stable ascending sort by the input column with the
        /// realization index breaking ties.
        /// </summary>
        /// <param name="inputs">The input column.</param>
        /// <param name="outputs">The output sample.</param>
        /// <param name="weights">The weights, or null.</param>
        /// <param name="bins">The bin count to validate.</param>
        /// <param name="pairs">Receives the valid pair count.</param>
        /// <returns>The sorted realization indices of the valid pairs.</returns>
        /// <exception cref="ArgumentNullException">Thrown when either sample is null.</exception>
        /// <exception cref="ArgumentException">Thrown when the samples differ in length or the weights do.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the bin count is below two.</exception>
        private static int[] PrepareOrder(double[] inputs, double[] outputs, double[]? weights, int bins, out int pairs)
        {
            if (inputs == null) throw new ArgumentNullException(nameof(inputs));
            if (outputs == null) throw new ArgumentNullException(nameof(outputs));
            if (inputs.Length != outputs.Length)
            {
                throw new ArgumentException("The input and output samples must have the same length.", nameof(inputs));
            }
            if (weights != null && weights.Length != outputs.Length)
            {
                throw new ArgumentException("The weights must be parallel to the samples.", nameof(weights));
            }
            if (bins < 2) throw new ArgumentOutOfRangeException(nameof(bins), "The bin count must be at least two.");

            var valid = new int[inputs.Length];
            pairs = 0;
            for (int i = 0; i < inputs.Length; i++)
            {
                if (!double.IsNaN(inputs[i]) && !double.IsNaN(outputs[i]))
                {
                    valid[pairs++] = i;
                }
            }
            var order = new int[pairs];
            Array.Copy(valid, order, pairs);
            Array.Sort(order, (a, b) =>
            {
                int comparison = inputs[a].CompareTo(inputs[b]);
                return comparison != 0 ? comparison : a.CompareTo(b);
            });
            return order;
        }
    }
}
