using System;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// One knowledge input's value-of-information entry: the epistemic variance of the queried
    /// risk measure that a study resolving this input could remove, with its share of the total
    /// and the candidate-study group it rolls up to.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    public sealed class ValueOfInformationEntry
    {
        /// <summary>
        /// Initializes an entry.
        /// </summary>
        /// <param name="label">The input column's display label. Null coerces to empty.</param>
        /// <param name="groupLabel">The candidate-study group label. Null coerces to the entry label.</param>
        /// <param name="resolvableVariance">The resolvable epistemic variance (NaN when inestimable).</param>
        /// <param name="varianceShare">The share of the total epistemic variance (NaN when the total is zero or inestimable).</param>
        /// <param name="realizations">The valid realization pairs behind the estimate.</param>
        public ValueOfInformationEntry(string? label, string? groupLabel, double resolvableVariance,
            double varianceShare, int realizations)
        {
            Label = label ?? string.Empty;
            GroupLabel = groupLabel ?? Label;
            ResolvableVariance = resolvableVariance;
            VarianceShare = varianceShare;
            Realizations = realizations;
        }

        /// <summary>
        /// The input column's display label, in the sampler walk order's naming.
        /// </summary>
        public string Label { get; }

        /// <summary>
        /// The candidate-study group label — the owning function, so a multi-dimension
        /// function's columns share one group.
        /// </summary>
        public string GroupLabel { get; }

        /// <summary>
        /// The epistemic variance of the queried measure resolvable by this input, in the
        /// measure's squared units: the variance of the conditional measure mean across the
        /// input's equal-weight bins. NaN when too few valid pairs exist.
        /// </summary>
        public double ResolvableVariance { get; }

        /// <summary>
        /// The resolvable epistemic uncertainty in the measure's own units — the square root of
        /// <see cref="ResolvableVariance"/> (the practitioner column of the ranked table).
        /// </summary>
        public double ResolvableStandardDeviation => ResolvableVariance >= 0d ? Math.Sqrt(ResolvableVariance) : double.NaN;

        /// <summary>
        /// This input's share of the total epistemic variance of the measure, in [0, 1] up to
        /// the estimator's documented bias.
        /// </summary>
        public double VarianceShare { get; }

        /// <summary>
        /// The valid realization pairs behind the estimate (after pairwise NaN filtering).
        /// </summary>
        public int Realizations { get; }
    }
}
