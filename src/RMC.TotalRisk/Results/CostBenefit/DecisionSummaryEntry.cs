using System;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Results.CostBenefit
{
    /// <summary>
    /// One row of the decision summary's cross-tabulation: a computed strategy with its
    /// identity echoes and the alternative it recommended, or the withheld marker.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Every computed ranking contributes exactly one row, so the summary's per-alternative
    /// margins reconcile against the strategy tables by construction. A withheld
    /// recommendation carries an empty alternative name and a NaN value with the flag set —
    /// absence is stated, never implied.
    /// </para>
    /// </remarks>
    public sealed class DecisionSummaryEntry
    {
        /// <summary>
        /// Initializes a decision-summary row.
        /// </summary>
        /// <param name="strategy">The decision-strategy family.</param>
        /// <param name="tier">The catalog tier (1 = exact mean-only, 2 = stored-ensemble, 3 = shared-state).</param>
        /// <param name="layer">The uncertainty-layer label.</param>
        /// <param name="criterionLabel">The ranked criterion's display label.</param>
        /// <param name="parameterEcho">The strategy's parameter echo (empty for a parameterless rule).</param>
        /// <param name="recommendedAlternative">The recommended alternative's name (empty when withheld).</param>
        /// <param name="recommendedValue">The criterion value at the recommendation (NaN when withheld).</param>
        /// <param name="recommendationWithheld">Whether the recommendation is withheld.</param>
        /// <exception cref="ArgumentNullException">Thrown when a required label is null.</exception>
        /// <exception cref="ArgumentException">Thrown when the tier is not 1–3.</exception>
        public DecisionSummaryEntry(DecisionStrategy strategy, int tier, string layer,
            string criterionLabel, string parameterEcho, string recommendedAlternative,
            double recommendedValue, bool recommendationWithheld)
        {
            if (tier < 1 || tier > 3)
                throw new ArgumentException("The tier must be 1, 2, or 3.", nameof(tier));
            Layer = layer ?? throw new ArgumentNullException(nameof(layer));
            CriterionLabel = criterionLabel ?? throw new ArgumentNullException(nameof(criterionLabel));
            Strategy = strategy;
            Tier = tier;
            ParameterEcho = parameterEcho ?? string.Empty;
            RecommendedAlternative = recommendedAlternative ?? string.Empty;
            RecommendedValue = recommendedValue;
            RecommendationWithheld = recommendationWithheld;
        }

        /// <summary>The decision-strategy family.</summary>
        public DecisionStrategy Strategy { get; }

        /// <summary>The catalog tier (1 = exact mean-only, 2 = stored-ensemble, 3 = shared-state).</summary>
        public int Tier { get; }

        /// <summary>The uncertainty-layer label.</summary>
        public string Layer { get; }

        /// <summary>The ranked criterion's display label.</summary>
        public string CriterionLabel { get; }

        /// <summary>The strategy's parameter echo (empty for a parameterless rule).</summary>
        public string ParameterEcho { get; }

        /// <summary>The recommended alternative's name (empty when withheld).</summary>
        public string RecommendedAlternative { get; }

        /// <summary>The criterion value at the recommendation (NaN when withheld).</summary>
        public double RecommendedValue { get; }

        /// <summary>Whether the recommendation is withheld.</summary>
        public bool RecommendationWithheld { get; }
    }
}
