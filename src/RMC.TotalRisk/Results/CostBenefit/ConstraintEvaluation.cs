using System;
using System.Collections.Generic;
using System.Linq;

namespace RMC.TotalRisk.Results.CostBenefit
{
    /// <summary>
    /// One declared study constraint's evaluation: the constraint echo and the
    /// per-alternative satisfaction flags.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// A declared constraint is never silently inert: every study evaluates its declared
    /// constraints at their scopes into this table, and the strategy layer consumes the
    /// flags. A NaN metric value never satisfies a bound — an unmeasurable quantity cannot
    /// demonstrate compliance.
    /// </para>
    /// </remarks>
    public sealed class ConstraintEvaluation
    {
        /// <summary>
        /// Initializes a constraint evaluation row.
        /// </summary>
        /// <param name="label">The constraint's display label (metric, sense, threshold, and scope).</param>
        /// <param name="satisfied">The per-alternative satisfaction flags, in results row order.</param>
        /// <exception cref="ArgumentNullException">Thrown when a required argument is null.</exception>
        public ConstraintEvaluation(string label, IReadOnlyList<bool> satisfied)
        {
            Label = label ?? throw new ArgumentNullException(nameof(label));
            if (satisfied == null) throw new ArgumentNullException(nameof(satisfied));
            Satisfied = Array.AsReadOnly(satisfied.ToArray());
        }

        /// <summary>The constraint's display label (metric, sense, threshold, and scope).</summary>
        public string Label { get; }

        /// <summary>The per-alternative satisfaction flags, in results row order.</summary>
        public IReadOnlyList<bool> Satisfied { get; }
    }
}
