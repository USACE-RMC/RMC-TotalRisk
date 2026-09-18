using System;
using System.Collections.Generic;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// One candidate study's value-of-information rollup: the summed main effects of every
    /// knowledge column belonging to one function — the study a decision maker would actually
    /// commission resolves a function's uncertainty, not one latent dimension of it.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The rollup is the sum of the member columns' resolvable variances. Because the knowledge
    /// draws are independent by construction, the sum equals the group's joint main effect
    /// exactly when the members enter the measure additively, and understates it by the
    /// within-group interaction variance otherwise — a conservative rollup, documented with the
    /// reporting surface.
    /// </para>
    /// </remarks>
    public sealed class ValueOfInformationGroup
    {
        /// <summary>
        /// Initializes a group rollup.
        /// </summary>
        /// <param name="label">The candidate-study (function) label. Null coerces to empty.</param>
        /// <param name="memberLabels">The member column labels, in walk order.</param>
        /// <param name="resolvableVariance">The summed resolvable variance (NaN when any member is inestimable).</param>
        /// <param name="varianceShare">The share of the total epistemic variance.</param>
        /// <exception cref="ArgumentNullException">Thrown when the member list is null.</exception>
        public ValueOfInformationGroup(string? label, IReadOnlyList<string> memberLabels,
            double resolvableVariance, double varianceShare)
        {
            Label = label ?? string.Empty;
            if (memberLabels == null) throw new ArgumentNullException(nameof(memberLabels));
            MemberLabels = Array.AsReadOnly(new List<string>(memberLabels).ToArray());
            ResolvableVariance = resolvableVariance;
            VarianceShare = varianceShare;
        }

        /// <summary>
        /// The candidate-study label — the owning function's deduplicated display label.
        /// </summary>
        public string Label { get; }

        /// <summary>
        /// The member column labels, in the sampler walk order.
        /// </summary>
        public IReadOnlyList<string> MemberLabels { get; }

        /// <summary>
        /// The summed resolvable epistemic variance of the member columns, in the measure's
        /// squared units. NaN when any member is inestimable.
        /// </summary>
        public double ResolvableVariance { get; }

        /// <summary>
        /// The resolvable epistemic uncertainty in the measure's own units — the square root of
        /// <see cref="ResolvableVariance"/>.
        /// </summary>
        public double ResolvableStandardDeviation => ResolvableVariance >= 0d ? Math.Sqrt(ResolvableVariance) : double.NaN;

        /// <summary>
        /// The study's share of the total epistemic variance of the measure.
        /// </summary>
        public double VarianceShare { get; }
    }
}
