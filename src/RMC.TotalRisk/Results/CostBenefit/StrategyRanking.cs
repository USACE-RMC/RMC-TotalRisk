using System;
using System.Collections.Generic;
using System.Linq;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Results.CostBenefit
{
    /// <summary>
    /// One decision strategy's published ranking: the strategy identity with its tier, layer,
    /// and discipline echoes, the criterion and parameter echoes, the per-alternative
    /// criterion values and competition ranks, the exclusion flags, and the recommendation.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Criterion values are reported for every alternative — values are facts — while the
    /// competition ranks and the recommendation are restricted to alternatives that are both
    /// recommendation-eligible and measurable: a NaN criterion value excludes an alternative
    /// from ranking, a do-no-harm exclusion keeps its value visible but bars it from the
    /// recommendation, ties break to the first results-order row (the designated baseline is
    /// row zero — the conservative reading), and a ranking with no eligible measurable row
    /// withholds its recommendation. The layer and discipline echoes state each number's
    /// uncertainty pedigree, so a reader never mistakes an aleatory tail measure for an
    /// epistemic one.
    /// </para>
    /// </remarks>
    public sealed class StrategyRanking
    {
        /// <summary>
        /// Initializes a strategy ranking.
        /// </summary>
        /// <param name="strategy">The decision-strategy family.</param>
        /// <param name="tier">The catalog tier (1 = exact mean-only, 2 = stored-ensemble, 3 = shared-state).</param>
        /// <param name="layer">The uncertainty-layer label.</param>
        /// <param name="discipline">The uncertainty-discipline label.</param>
        /// <param name="criterionLabel">The ranked criterion's display label.</param>
        /// <param name="parameterEcho">The strategy's parameter echo (empty for a parameterless rule).</param>
        /// <param name="direction">The optimization direction of the criterion.</param>
        /// <param name="alternativeNames">The alternative names, in results row order.</param>
        /// <param name="criterionValues">The criterion values, parallel to the names.</param>
        /// <param name="ranks">The competition ranks (1 = best; 0 = unranked), parallel to the names.</param>
        /// <param name="isExcludedForNaN">The NaN-exclusion flags, parallel to the names.</param>
        /// <param name="isExcludedFromRecommendation">The recommendation-exclusion flags, parallel to the names.</param>
        /// <param name="recommendedIndex">The recommended alternative's row index, or −1 when withheld.</param>
        /// <exception cref="ArgumentNullException">Thrown when a required argument is null.</exception>
        /// <exception cref="ArgumentException">Thrown when the tier is not 1–3, a list does not parallel the names, or the recommended index is out of range.</exception>
        public StrategyRanking(DecisionStrategy strategy, int tier, string layer, string discipline,
            string criterionLabel, string parameterEcho, ObjectiveDirection direction,
            IReadOnlyList<string> alternativeNames, IReadOnlyList<double> criterionValues,
            IReadOnlyList<int> ranks, IReadOnlyList<bool> isExcludedForNaN,
            IReadOnlyList<bool> isExcludedFromRecommendation, int recommendedIndex)
        {
            if (tier < 1 || tier > 3)
                throw new ArgumentException("The tier must be 1, 2, or 3.", nameof(tier));
            Layer = layer ?? throw new ArgumentNullException(nameof(layer));
            Discipline = discipline ?? throw new ArgumentNullException(nameof(discipline));
            CriterionLabel = criterionLabel ?? throw new ArgumentNullException(nameof(criterionLabel));
            if (alternativeNames == null) throw new ArgumentNullException(nameof(alternativeNames));
            if (criterionValues == null) throw new ArgumentNullException(nameof(criterionValues));
            if (ranks == null) throw new ArgumentNullException(nameof(ranks));
            if (isExcludedForNaN == null) throw new ArgumentNullException(nameof(isExcludedForNaN));
            if (isExcludedFromRecommendation == null) throw new ArgumentNullException(nameof(isExcludedFromRecommendation));
            if (criterionValues.Count != alternativeNames.Count || ranks.Count != alternativeNames.Count
                || isExcludedForNaN.Count != alternativeNames.Count
                || isExcludedFromRecommendation.Count != alternativeNames.Count)
            {
                throw new ArgumentException("Every per-alternative list must parallel the alternative names.",
                    nameof(alternativeNames));
            }
            if (recommendedIndex < -1 || recommendedIndex >= alternativeNames.Count)
                throw new ArgumentException("The recommended index must be −1 or a valid row index.", nameof(recommendedIndex));
            Strategy = strategy;
            Tier = tier;
            ParameterEcho = parameterEcho ?? string.Empty;
            Direction = direction;
            AlternativeNames = Array.AsReadOnly(alternativeNames.ToArray());
            CriterionValues = Array.AsReadOnly(criterionValues.ToArray());
            Ranks = Array.AsReadOnly(ranks.ToArray());
            IsExcludedForNaN = Array.AsReadOnly(isExcludedForNaN.ToArray());
            IsExcludedFromRecommendation = Array.AsReadOnly(isExcludedFromRecommendation.ToArray());
            RecommendedIndex = recommendedIndex;
        }

        /// <summary>The decision-strategy family.</summary>
        public DecisionStrategy Strategy { get; }

        /// <summary>The catalog tier (1 = exact mean-only, 2 = stored-ensemble, 3 = shared-state).</summary>
        public int Tier { get; }

        /// <summary>The uncertainty-layer label.</summary>
        public string Layer { get; }

        /// <summary>The uncertainty-discipline label.</summary>
        public string Discipline { get; }

        /// <summary>The ranked criterion's display label.</summary>
        public string CriterionLabel { get; }

        /// <summary>The strategy's parameter echo (empty for a parameterless rule).</summary>
        public string ParameterEcho { get; }

        /// <summary>The optimization direction of the criterion.</summary>
        public ObjectiveDirection Direction { get; }

        /// <summary>The alternative names, in results row order.</summary>
        public IReadOnlyList<string> AlternativeNames { get; }

        /// <summary>The criterion values, parallel to the names (reported for every alternative).</summary>
        public IReadOnlyList<double> CriterionValues { get; }

        /// <summary>The competition ranks (1 = best; ties share the better rank; 0 = unranked).</summary>
        public IReadOnlyList<int> Ranks { get; }

        /// <summary>The NaN-exclusion flags, parallel to the names.</summary>
        public IReadOnlyList<bool> IsExcludedForNaN { get; }

        /// <summary>The recommendation-exclusion flags, parallel to the names.</summary>
        public IReadOnlyList<bool> IsExcludedFromRecommendation { get; }

        /// <summary>The recommended alternative's row index, or −1 when withheld.</summary>
        public int RecommendedIndex { get; }

        /// <summary>Whether the recommendation is withheld (no eligible measurable alternative).</summary>
        public bool RecommendationWithheld
        {
            get { return RecommendedIndex < 0; }
        }

        /// <summary>The recommended alternative's name, or empty when withheld.</summary>
        public string RecommendedAlternative
        {
            get { return RecommendedIndex < 0 ? string.Empty : AlternativeNames[RecommendedIndex]; }
        }
    }
}
