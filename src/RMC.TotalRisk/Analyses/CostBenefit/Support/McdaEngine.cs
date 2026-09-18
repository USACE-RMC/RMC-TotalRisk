using System;
using System.Collections.Generic;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results;

namespace RMC.TotalRisk.Analyses.CostBenefit.Support
{
    /// <summary>
    /// The weighted-sum multi-criteria scoring over min-max-normalized objective values.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Weights normalize to sum to one and are echoed. Normalization is direction-aware —
    /// one is always best, zero always worst — and a constant objective contributes zero to
    /// every score rather than poisoning the normalization. A NaN objective value excludes
    /// the alternative (NaN score, rank zero); recommendation-excluded alternatives keep
    /// their scores as facts but are unranked. The standard caveat stands: a weighted sum
    /// cannot reach non-convex regions of the frontier that the ε-constraint sweep can.
    /// </para>
    /// </remarks>
    internal static class McdaEngine
    {
        /// <summary>
        /// Scores the alternatives: normalized weights, direction-aware min-max normalization
        /// over the included alternatives, weighted-sum scores, and competition ranks among
        /// the recommendable alternatives.
        /// </summary>
        /// <param name="objectiveNames">The declared objective names.</param>
        /// <param name="directions">The objective directions, parallel to the names.</param>
        /// <param name="weights">The declared weights (positive finite; validated upstream).</param>
        /// <param name="alternativeNames">The alternative names, in results row order.</param>
        /// <param name="values">The objective values (one row per alternative).</param>
        /// <param name="recommendationEligible">The per-alternative recommendation-eligibility flags.</param>
        /// <returns>The multi-criteria results block.</returns>
        /// <exception cref="ArgumentNullException">Thrown when a required argument is null.</exception>
        /// <exception cref="ArgumentException">Thrown when a list does not parallel its axis.</exception>
        internal static McdaResults Score(IReadOnlyList<string> objectiveNames,
            IReadOnlyList<ObjectiveDirection> directions, IReadOnlyList<double> weights,
            IReadOnlyList<string> alternativeNames, double[][] values,
            IReadOnlyList<bool> recommendationEligible)
        {
            if (objectiveNames == null) throw new ArgumentNullException(nameof(objectiveNames));
            if (directions == null) throw new ArgumentNullException(nameof(directions));
            if (weights == null) throw new ArgumentNullException(nameof(weights));
            if (alternativeNames == null) throw new ArgumentNullException(nameof(alternativeNames));
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (recommendationEligible == null) throw new ArgumentNullException(nameof(recommendationEligible));
            int objectiveCount = objectiveNames.Count;
            int count = alternativeNames.Count;
            if (directions.Count != objectiveCount || weights.Count != objectiveCount)
                throw new ArgumentException("The directions and weights must parallel the objective names.", nameof(weights));
            if (values.Length != count || recommendationEligible.Count != count)
                throw new ArgumentException("Every per-alternative list must parallel the names.", nameof(values));

            double weightTotal = 0d;
            for (int j = 0; j < objectiveCount; j++)
            {
                weightTotal += weights[j];
            }
            var normalizedWeights = new double[objectiveCount];
            for (int j = 0; j < objectiveCount; j++)
            {
                normalizedWeights[j] = weights[j] / weightTotal;
            }

            // NaN exclusion, then the per-objective ranges over the included alternatives.
            var excluded = new bool[count];
            for (int i = 0; i < count; i++)
            {
                for (int j = 0; j < objectiveCount; j++)
                {
                    if (double.IsNaN(values[i][j])) excluded[i] = true;
                }
            }
            var minima = new double[objectiveCount];
            var maxima = new double[objectiveCount];
            for (int j = 0; j < objectiveCount; j++)
            {
                minima[j] = double.PositiveInfinity;
                maxima[j] = double.NegativeInfinity;
                for (int i = 0; i < count; i++)
                {
                    if (excluded[i]) continue;
                    if (values[i][j] < minima[j]) minima[j] = values[i][j];
                    if (values[i][j] > maxima[j]) maxima[j] = values[i][j];
                }
            }

            var normalizedValues = new IReadOnlyList<double>[count];
            var scores = new double[count];
            for (int i = 0; i < count; i++)
            {
                var row = new double[objectiveCount];
                if (excluded[i])
                {
                    for (int j = 0; j < objectiveCount; j++)
                    {
                        row[j] = double.NaN;
                    }
                    scores[i] = double.NaN;
                }
                else
                {
                    double score = 0d;
                    for (int j = 0; j < objectiveCount; j++)
                    {
                        double range = maxima[j] - minima[j];
                        row[j] = range == 0d
                            ? 0d
                            : directions[j] == ObjectiveDirection.Maximize
                                ? (values[i][j] - minima[j]) / range
                                : (maxima[j] - values[i][j]) / range;
                        score += normalizedWeights[j] * row[j];
                    }
                    scores[i] = score;
                }
                normalizedValues[i] = Array.AsReadOnly(row);
            }

            // Competition ranks among the recommendable scored alternatives: ties share the
            // better rank; the next rank skips the tied count.
            var ranks = new int[count];
            for (int i = 0; i < count; i++)
            {
                if (excluded[i] || !recommendationEligible[i]) continue;
                int rank = 1;
                for (int j = 0; j < count; j++)
                {
                    if (j == i || excluded[j] || !recommendationEligible[j]) continue;
                    if (scores[j] > scores[i]) rank++;
                }
                ranks[i] = rank;
            }

            var notEligible = new bool[count];
            for (int i = 0; i < count; i++)
            {
                notEligible[i] = !recommendationEligible[i];
            }
            return new McdaResults(objectiveNames, directions, normalizedWeights, alternativeNames,
                normalizedValues, scores, ranks, excluded, notEligible);
        }
    }
}
