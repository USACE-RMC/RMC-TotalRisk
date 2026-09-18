using System;
using System.Collections.Generic;
using System.Linq;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Results.CostBenefit
{
    /// <summary>
    /// The weighted-sum multi-criteria scores over min-max-normalized objective values: the
    /// objective and weight echoes, the normalized value matrix, the scores, the competition
    /// ranks, and the exclusion flags.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Normalization is direction-aware, so one is always best and zero always worst; a
    /// constant objective contributes zero to every score rather than poisoning the
    /// normalization; weights are normalized to sum to one and echoed. An alternative with a
    /// NaN objective value is excluded (NaN score, rank zero); a do-no-harm
    /// recommendation-exclusion keeps its score — scores are facts — but ranks are assigned
    /// among recommendable alternatives only, ties sharing the better rank.
    /// </para>
    /// </remarks>
    public sealed class McdaResults
    {
        /// <summary>
        /// Initializes a multi-criteria results block.
        /// </summary>
        /// <param name="objectiveNames">The declared objective names.</param>
        /// <param name="directions">The objective directions, parallel to the names.</param>
        /// <param name="normalizedWeights">The normalized weights, parallel to the names.</param>
        /// <param name="alternativeNames">The alternative names, in results row order.</param>
        /// <param name="normalizedValues">The normalized objective values (one row per alternative).</param>
        /// <param name="scores">The weighted-sum scores, parallel to the alternative names.</param>
        /// <param name="ranks">The competition ranks (1 = best; 0 = unranked), parallel to the alternative names.</param>
        /// <param name="isExcludedForNaN">The NaN-exclusion flags, parallel to the alternative names.</param>
        /// <param name="isExcludedFromRecommendation">The do-no-harm recommendation-exclusion flags, parallel to the alternative names.</param>
        /// <exception cref="ArgumentNullException">Thrown when a required argument is null.</exception>
        /// <exception cref="ArgumentException">Thrown when a list does not parallel its axis.</exception>
        public McdaResults(IReadOnlyList<string> objectiveNames, IReadOnlyList<ObjectiveDirection> directions,
            IReadOnlyList<double> normalizedWeights, IReadOnlyList<string> alternativeNames,
            IReadOnlyList<IReadOnlyList<double>> normalizedValues, IReadOnlyList<double> scores,
            IReadOnlyList<int> ranks, IReadOnlyList<bool> isExcludedForNaN,
            IReadOnlyList<bool> isExcludedFromRecommendation)
        {
            if (objectiveNames == null) throw new ArgumentNullException(nameof(objectiveNames));
            if (directions == null) throw new ArgumentNullException(nameof(directions));
            if (normalizedWeights == null) throw new ArgumentNullException(nameof(normalizedWeights));
            if (alternativeNames == null) throw new ArgumentNullException(nameof(alternativeNames));
            if (normalizedValues == null) throw new ArgumentNullException(nameof(normalizedValues));
            if (scores == null) throw new ArgumentNullException(nameof(scores));
            if (ranks == null) throw new ArgumentNullException(nameof(ranks));
            if (isExcludedForNaN == null) throw new ArgumentNullException(nameof(isExcludedForNaN));
            if (isExcludedFromRecommendation == null) throw new ArgumentNullException(nameof(isExcludedFromRecommendation));
            if (directions.Count != objectiveNames.Count || normalizedWeights.Count != objectiveNames.Count)
                throw new ArgumentException("The directions and weights must parallel the objective names.", nameof(directions));
            if (normalizedValues.Count != alternativeNames.Count || scores.Count != alternativeNames.Count
                || ranks.Count != alternativeNames.Count || isExcludedForNaN.Count != alternativeNames.Count
                || isExcludedFromRecommendation.Count != alternativeNames.Count)
            {
                throw new ArgumentException("Every per-alternative list must parallel the alternative names.",
                    nameof(alternativeNames));
            }
            var valueRows = new IReadOnlyList<double>[normalizedValues.Count];
            for (int i = 0; i < normalizedValues.Count; i++)
            {
                if (normalizedValues[i] == null || normalizedValues[i].Count != objectiveNames.Count)
                    throw new ArgumentException("Every normalized row must parallel the objective names.", nameof(normalizedValues));
                valueRows[i] = Array.AsReadOnly(normalizedValues[i].ToArray());
            }
            ObjectiveNames = Array.AsReadOnly(objectiveNames.ToArray());
            Directions = Array.AsReadOnly(directions.ToArray());
            NormalizedWeights = Array.AsReadOnly(normalizedWeights.ToArray());
            AlternativeNames = Array.AsReadOnly(alternativeNames.ToArray());
            NormalizedValues = Array.AsReadOnly(valueRows);
            Scores = Array.AsReadOnly(scores.ToArray());
            Ranks = Array.AsReadOnly(ranks.ToArray());
            IsExcludedForNaN = Array.AsReadOnly(isExcludedForNaN.ToArray());
            IsExcludedFromRecommendation = Array.AsReadOnly(isExcludedFromRecommendation.ToArray());
        }

        /// <summary>The declared objective names.</summary>
        public IReadOnlyList<string> ObjectiveNames { get; }

        /// <summary>The objective directions, parallel to the names.</summary>
        public IReadOnlyList<ObjectiveDirection> Directions { get; }

        /// <summary>The normalized weights (summing to one), parallel to the names.</summary>
        public IReadOnlyList<double> NormalizedWeights { get; }

        /// <summary>The alternative names, in results row order.</summary>
        public IReadOnlyList<string> AlternativeNames { get; }

        /// <summary>The normalized objective values (one row per alternative; one is best).</summary>
        public IReadOnlyList<IReadOnlyList<double>> NormalizedValues { get; }

        /// <summary>The weighted-sum scores, parallel to the alternative names (NaN when excluded).</summary>
        public IReadOnlyList<double> Scores { get; }

        /// <summary>The competition ranks (1 = best; ties share the better rank; 0 = unranked).</summary>
        public IReadOnlyList<int> Ranks { get; }

        /// <summary>The NaN-exclusion flags, parallel to the alternative names.</summary>
        public IReadOnlyList<bool> IsExcludedForNaN { get; }

        /// <summary>The do-no-harm recommendation-exclusion flags, parallel to the alternative names.</summary>
        public IReadOnlyList<bool> IsExcludedFromRecommendation { get; }
    }
}
