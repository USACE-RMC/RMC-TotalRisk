using System;
using System.Collections.Generic;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.CostBenefit;

namespace RMC.TotalRisk.Analyses.CostBenefit.Support
{
    /// <summary>
    /// The ranking core of the decision-strategy catalog: one authority turning a criterion
    /// vector into a published <see cref="StrategyRanking"/> — competition ranks, exclusion
    /// flags, and the recommendation — plus the catalog's layer and discipline labels.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Criterion values are reported for every alternative; ranks and the recommendation are
    /// assigned among alternatives that are both recommendation-eligible and measurable. A
    /// competition rank is one plus the count of strictly better eligible measurable rows, so
    /// exact ties share the better rank; the recommendation is the first results-order row at
    /// rank one (the designated baseline is row zero — the conservative reading), and a
    /// ranking with no eligible measurable row withholds its recommendation. The layer and
    /// discipline constants are the single authority for the catalog's honesty labels.
    /// </para>
    /// </remarks>
    internal static class DecisionStrategyEngine
    {
        /// <summary>The exact layer: mean-only twin quantities with no sampling noise.</summary>
        internal const string LayerExact = "Exact";

        /// <summary>The aleatory layer: annual-variability measures from mean loss-exceedance curves.</summary>
        internal const string LayerAleatory = "Aleatory";

        /// <summary>The epistemic layer: weighted measures over stored full-uncertainty ensembles.</summary>
        internal const string LayerEpistemic = "Epistemic";

        /// <summary>The shared-state layer: block measures over aligned logic-tree states.</summary>
        internal const string LayerSharedState = "Shared-state";

        /// <summary>The exact discipline label (mean-only twin deltas).</summary>
        internal const string DisciplineExact = "exact (mean-only twin deltas)";

        /// <summary>The aleatory discipline label (tail measures from the mean loss-exceedance curve).</summary>
        internal const string DisciplineAleatory = "aleatory-from-mean-LEC";

        /// <summary>The epistemic discipline label (weighted reductions over stored ensembles).</summary>
        internal const string DisciplineEpistemic = "epistemic-weighted";

        /// <summary>The shared-state discipline label (block means carrying Monte Carlo block noise).</summary>
        internal const string DisciplineBlockNoise = "block-noise k·SE/√M";

        /// <summary>
        /// Ranks one criterion vector into a published strategy ranking.
        /// </summary>
        /// <param name="strategy">The decision-strategy family.</param>
        /// <param name="tier">The catalog tier (1 = exact mean-only, 2 = stored-ensemble, 3 = shared-state).</param>
        /// <param name="layer">The uncertainty-layer label.</param>
        /// <param name="discipline">The uncertainty-discipline label.</param>
        /// <param name="criterionLabel">The criterion's display label.</param>
        /// <param name="parameterEcho">The strategy's parameter echo (empty for a parameterless rule).</param>
        /// <param name="direction">The optimization direction of the criterion.</param>
        /// <param name="names">The alternative names, in results row order.</param>
        /// <param name="values">The criterion values, parallel to the names.</param>
        /// <param name="recommendationEligible">The per-alternative recommendation eligibility, parallel to the names.</param>
        /// <returns>The published ranking.</returns>
        internal static StrategyRanking Rank(DecisionStrategy strategy, int tier, string layer,
            string discipline, string criterionLabel, string parameterEcho,
            ObjectiveDirection direction, IReadOnlyList<string> names, IReadOnlyList<double> values,
            IReadOnlyList<bool> recommendationEligible)
        {
            int count = names.Count;
            var ranks = new int[count];
            var isExcludedForNaN = new bool[count];
            var isExcludedFromRecommendation = new bool[count];
            var rankable = new bool[count];
            for (int i = 0; i < count; i++)
            {
                isExcludedForNaN[i] = double.IsNaN(values[i]);
                isExcludedFromRecommendation[i] = !recommendationEligible[i];
                rankable[i] = recommendationEligible[i] && !isExcludedForNaN[i];
            }
            int recommended = -1;
            for (int i = 0; i < count; i++)
            {
                if (!rankable[i]) continue;
                int betterCount = 0;
                for (int j = 0; j < count; j++)
                {
                    if (j == i || !rankable[j]) continue;
                    if (IsStrictlyBetter(values[j], values[i], direction)) betterCount++;
                }
                ranks[i] = 1 + betterCount;
                if (ranks[i] == 1 && recommended < 0) recommended = i;
            }
            return new StrategyRanking(strategy, tier, layer, discipline, criterionLabel,
                parameterEcho, direction, names, values, ranks, isExcludedForNaN,
                isExcludedFromRecommendation, recommended);
        }

        /// <summary>
        /// Direction-aware strict comparison: whether the candidate value beats the reference.
        /// </summary>
        /// <param name="candidate">The candidate value.</param>
        /// <param name="reference">The reference value.</param>
        /// <param name="direction">The optimization direction.</param>
        /// <returns>True when strictly better.</returns>
        private static bool IsStrictlyBetter(double candidate, double reference, ObjectiveDirection direction)
        {
            return direction == ObjectiveDirection.Maximize ? candidate > reference : candidate < reference;
        }
    }
}
