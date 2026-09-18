using System;
using System.Collections.Generic;
using System.Linq;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Results.CostBenefit
{
    /// <summary>
    /// The k-objective frontier over the declared objective vector: the objective echo, the
    /// per-alternative value matrix with non-dominance and exclusion flags, the standard
    /// two-dimensional projections, and the incremental cost-effectiveness table.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The screen is weak dominance, direction-aware; exact ties are both kept; an
    /// alternative with a NaN objective value is excluded and flagged rather than compared.
    /// A do-no-harm failure never removes a row — dominance flags are facts, not
    /// recommendations — but the recommendation flag rides beside them for the strategy
    /// layer. The incremental table runs along the cost-ranked non-dominated set.
    /// </para>
    /// </remarks>
    public sealed class ParetoFrontierResults
    {
        /// <summary>
        /// Initializes a frontier results block.
        /// </summary>
        /// <param name="objectiveNames">The declared objective names.</param>
        /// <param name="directions">The objective directions, parallel to the names.</param>
        /// <param name="alternativeNames">The alternative names, in results row order.</param>
        /// <param name="values">The per-alternative objective values (one row per alternative, parallel to the objective names).</param>
        /// <param name="isNonDominated">The non-dominance flags, parallel to the alternative names.</param>
        /// <param name="isExcludedForNaN">The NaN-exclusion flags, parallel to the alternative names.</param>
        /// <param name="isExcludedFromRecommendation">The do-no-harm recommendation-exclusion flags, parallel to the alternative names.</param>
        /// <param name="projections">The standard two-dimensional projections.</param>
        /// <param name="incrementalAnalysis">The incremental cost-effectiveness steps along the cost-ranked non-dominated set.</param>
        /// <exception cref="ArgumentNullException">Thrown when a required argument is null.</exception>
        /// <exception cref="ArgumentException">Thrown when a list does not parallel its axis.</exception>
        public ParetoFrontierResults(IReadOnlyList<string> objectiveNames,
            IReadOnlyList<ObjectiveDirection> directions, IReadOnlyList<string> alternativeNames,
            IReadOnlyList<IReadOnlyList<double>> values, IReadOnlyList<bool> isNonDominated,
            IReadOnlyList<bool> isExcludedForNaN, IReadOnlyList<bool> isExcludedFromRecommendation,
            IReadOnlyList<FrontierProjection> projections,
            IReadOnlyList<IncrementalEntry> incrementalAnalysis)
        {
            if (objectiveNames == null) throw new ArgumentNullException(nameof(objectiveNames));
            if (directions == null) throw new ArgumentNullException(nameof(directions));
            if (alternativeNames == null) throw new ArgumentNullException(nameof(alternativeNames));
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (isNonDominated == null) throw new ArgumentNullException(nameof(isNonDominated));
            if (isExcludedForNaN == null) throw new ArgumentNullException(nameof(isExcludedForNaN));
            if (isExcludedFromRecommendation == null) throw new ArgumentNullException(nameof(isExcludedFromRecommendation));
            if (projections == null) throw new ArgumentNullException(nameof(projections));
            if (incrementalAnalysis == null) throw new ArgumentNullException(nameof(incrementalAnalysis));
            if (directions.Count != objectiveNames.Count)
                throw new ArgumentException("The directions must parallel the objective names.", nameof(directions));
            if (values.Count != alternativeNames.Count || isNonDominated.Count != alternativeNames.Count
                || isExcludedForNaN.Count != alternativeNames.Count
                || isExcludedFromRecommendation.Count != alternativeNames.Count)
            {
                throw new ArgumentException("Every per-alternative list must parallel the alternative names.",
                    nameof(alternativeNames));
            }
            var valueRows = new IReadOnlyList<double>[values.Count];
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] == null || values[i].Count != objectiveNames.Count)
                    throw new ArgumentException("Every value row must parallel the objective names.", nameof(values));
                valueRows[i] = Array.AsReadOnly(values[i].ToArray());
            }
            ObjectiveNames = Array.AsReadOnly(objectiveNames.ToArray());
            Directions = Array.AsReadOnly(directions.ToArray());
            AlternativeNames = Array.AsReadOnly(alternativeNames.ToArray());
            Values = Array.AsReadOnly(valueRows);
            IsNonDominated = Array.AsReadOnly(isNonDominated.ToArray());
            IsExcludedForNaN = Array.AsReadOnly(isExcludedForNaN.ToArray());
            IsExcludedFromRecommendation = Array.AsReadOnly(isExcludedFromRecommendation.ToArray());
            Projections = Array.AsReadOnly(projections.ToArray());
            IncrementalAnalysis = Array.AsReadOnly(incrementalAnalysis.ToArray());
        }

        /// <summary>The declared objective names.</summary>
        public IReadOnlyList<string> ObjectiveNames { get; }

        /// <summary>The objective directions, parallel to the names.</summary>
        public IReadOnlyList<ObjectiveDirection> Directions { get; }

        /// <summary>The alternative names, in results row order.</summary>
        public IReadOnlyList<string> AlternativeNames { get; }

        /// <summary>The per-alternative objective values (one row per alternative).</summary>
        public IReadOnlyList<IReadOnlyList<double>> Values { get; }

        /// <summary>The non-dominance flags, parallel to the alternative names.</summary>
        public IReadOnlyList<bool> IsNonDominated { get; }

        /// <summary>The NaN-exclusion flags, parallel to the alternative names.</summary>
        public IReadOnlyList<bool> IsExcludedForNaN { get; }

        /// <summary>The do-no-harm recommendation-exclusion flags, parallel to the alternative names.</summary>
        public IReadOnlyList<bool> IsExcludedFromRecommendation { get; }

        /// <summary>The standard two-dimensional projections.</summary>
        public IReadOnlyList<FrontierProjection> Projections { get; }

        /// <summary>The incremental cost-effectiveness steps along the cost-ranked non-dominated set.</summary>
        public IReadOnlyList<IncrementalEntry> IncrementalAnalysis { get; }
    }
}
