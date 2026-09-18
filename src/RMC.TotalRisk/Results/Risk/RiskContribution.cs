using System;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// One scope's attributed contribution to its parent's risk (the % contribution
    /// diagnostic): the attributed annualized failure probability, the attributed failure mean,
    /// and the attributed excess (incremental) mean. Stored as raw values — percentages derive
    /// on read via <see cref="ShareOf"/> so the additivity identities stay testable and a zero
    /// parent never poisons a stored field.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// <b>The attribution scheme:</b> every failure-mode combination
    /// method already produces an exclusive failure-event decomposition — the mutually-exclusive
    /// normalized marginals, the common-cause adjusted marginals, the competing cumulative
    /// incidence functions, and the joint method's inclusion–exclusion pathways. Within each
    /// exclusive event, the event probability splits <i>equally</i> among the participating
    /// modes — exactly the Shapley value of the union game v(S) = P(∪ F<sub>j</sub>), since each
    /// exclusive event is a scaled dual-unanimity game — and the event's combined consequence
    /// splits <i>proportionally to the participants' marginal consequences</i> (equal split when
    /// they sum to zero, which also covers reliability mode). Shares always sum to the event's
    /// values, so Σ contributions ≡ the parent's recorded totals under every method, rule, and
    /// dependency; the one-mode events of the per-mode methods reduce the scheme exactly to
    /// "adjusted marginal × marginal consequence" — the decomposition other tools report.
    /// </para>
    /// <para>
    /// The probability base is consequence-free (the Shapley split needs no consequence values),
    /// so reliability mode populates <see cref="FailureProbability"/> fully while the mean
    /// fields stay zero — % of the annualized failure probability is available in both analysis
    /// modes. Sums are pinned against the parent's raw recorded <c>MassBalance</c> and
    /// unclamped means: the Min(·, 1) clamp and the upstream inclusion–exclusion truncation are
    /// documented exclusions of the identity.
    /// </para>
    /// <para>
    /// Serialized in the results JSON (System.Text.Json) as an append-only member of the
    /// realization and summary containers; a missing (null) block on older payloads means
    /// "not computed".
    /// </para>
    /// </remarks>
    public sealed class RiskContribution
    {
        /// <summary>
        /// The attributed annualized failure probability — this scope's Shapley share of its
        /// parent's failure probability mass.
        /// </summary>
        public double FailureProbability { get; set; }

        /// <summary>
        /// The attributed failure mean (the expected annual failure consequence credited to this
        /// scope under the consequence-proportional split).
        /// </summary>
        public double FailureMean { get; set; }

        /// <summary>
        /// The attributed excess (incremental) mean credited to this scope.
        /// </summary>
        public double ExcessMean { get; set; }

        /// <summary>
        /// A value's share of a parent total — the % contribution read: 0 when the parent total
        /// is not positive (never NaN), the plain ratio otherwise.
        /// </summary>
        /// <param name="value">The attributed value.</param>
        /// <param name="parentTotal">The parent's total for the same measure.</param>
        /// <returns>The fractional share in [0, ∞) — multiply by 100 for percent.</returns>
        public static double ShareOf(double value, double parentTotal)
        {
            return parentTotal > 0d ? value / parentTotal : 0d;
        }

        /// <summary>
        /// Creates a detached copy, or null for a null source — the summary trees copy the
        /// realization values so the compact ensemble never aliases realization state.
        /// </summary>
        /// <param name="source">The contribution to copy.</param>
        /// <returns>The copy, or null.</returns>
        public static RiskContribution? Copy(RiskContribution? source)
        {
            return source == null
                ? null
                : new RiskContribution
                {
                    FailureProbability = source.FailureProbability,
                    FailureMean = source.FailureMean,
                    ExcessMean = source.ExcessMean,
                };
        }
    }
}
