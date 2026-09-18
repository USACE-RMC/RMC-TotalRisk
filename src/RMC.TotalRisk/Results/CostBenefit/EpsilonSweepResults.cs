using System;
using System.Collections.Generic;
using System.Linq;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Results.CostBenefit
{
    /// <summary>
    /// The results of a discrete ε-constraint sweep: the objective echo, the swept grid, the
    /// per-ε selection entries, and the noninferior set of the swept pair.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The noninferior set is the weak-Pareto screen of (primary, swept) values over the
    /// alternatives satisfying the fixed constraints, ordered by ascending swept value; exact
    /// ties are both kept. The swept bound is always an upper bound on the swept objective.
    /// </para>
    /// </remarks>
    public sealed class EpsilonSweepResults
    {
        /// <summary>
        /// Initializes a sweep results block.
        /// </summary>
        /// <param name="primaryName">The primary objective's declared name.</param>
        /// <param name="primaryDirection">The primary objective's direction.</param>
        /// <param name="epsilonMetricLabel">The swept objective's metric label.</param>
        /// <param name="grid">The swept bounds, ascending.</param>
        /// <param name="entries">The per-ε selection entries, parallel to the grid.</param>
        /// <param name="noninferiorAlternatives">The noninferior alternatives, by ascending swept value.</param>
        /// <exception cref="ArgumentNullException">Thrown when a required argument is null.</exception>
        /// <exception cref="ArgumentException">Thrown when the entries do not parallel the grid.</exception>
        public EpsilonSweepResults(string primaryName, ObjectiveDirection primaryDirection,
            string epsilonMetricLabel, IReadOnlyList<double> grid,
            IReadOnlyList<EpsilonSweepEntry> entries, IReadOnlyList<string> noninferiorAlternatives)
        {
            PrimaryName = primaryName ?? throw new ArgumentNullException(nameof(primaryName));
            PrimaryDirection = primaryDirection;
            EpsilonMetricLabel = epsilonMetricLabel ?? throw new ArgumentNullException(nameof(epsilonMetricLabel));
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            if (noninferiorAlternatives == null) throw new ArgumentNullException(nameof(noninferiorAlternatives));
            if (entries.Count != grid.Count)
                throw new ArgumentException("The entries must parallel the grid.", nameof(entries));
            Grid = Array.AsReadOnly(grid.ToArray());
            Entries = Array.AsReadOnly(entries.ToArray());
            NoninferiorAlternatives = Array.AsReadOnly(noninferiorAlternatives.ToArray());
        }

        /// <summary>The primary objective's declared name.</summary>
        public string PrimaryName { get; }

        /// <summary>The primary objective's direction.</summary>
        public ObjectiveDirection PrimaryDirection { get; }

        /// <summary>The swept objective's metric label.</summary>
        public string EpsilonMetricLabel { get; }

        /// <summary>The swept bounds, ascending.</summary>
        public IReadOnlyList<double> Grid { get; }

        /// <summary>The per-ε selection entries, parallel to the grid.</summary>
        public IReadOnlyList<EpsilonSweepEntry> Entries { get; }

        /// <summary>The noninferior alternatives of the swept pair, by ascending swept value.</summary>
        public IReadOnlyList<string> NoninferiorAlternatives { get; }
    }
}
