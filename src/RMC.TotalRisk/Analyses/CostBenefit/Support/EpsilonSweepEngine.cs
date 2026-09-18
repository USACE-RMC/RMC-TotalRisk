using System;
using System.Collections.Generic;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.CostBenefit;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Analyses.CostBenefit.Support
{
    /// <summary>
    /// The discrete ε-constraint sweep over an assembled metric matrix: feasibility filtering
    /// and exact selection per swept bound, the noninferior set of the swept pair, and the
    /// discrete trade-off ratios between adjacent selections.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The alternative set is finite and manually defined, so each grid point solves exactly
    /// by filtering on the fixed constraints and the swept upper bound and selecting the best
    /// primary value; ties break to the first alternative in results row order. The trade-off
    /// ratio −Δf₁/Δf_ε between adjacent selections is the discrete shadow price, identically
    /// the incremental cost-effectiveness ratio of planning practice — the incremental table
    /// calls the same helper, so the identity holds bit-exactly. The engine reads plain
    /// per-alternative arrays so a decision study can be driven from any assembled matrix,
    /// with no trajectory in the call path.
    /// </para>
    /// </remarks>
    internal static class EpsilonSweepEngine
    {
        /// <summary>
        /// The discrete trade-off ratio −Δf₁/Δf_ε between two selections — the discrete
        /// shadow price and the incremental cost-effectiveness ratio, computed in one place
        /// with one operand order.
        /// </summary>
        /// <param name="primaryFrom">The earlier selection's primary value.</param>
        /// <param name="primaryTo">The later selection's primary value.</param>
        /// <param name="constrainedFrom">The earlier selection's swept value.</param>
        /// <param name="constrainedTo">The later selection's swept value.</param>
        /// <returns>The ratio; NaN when the swept values coincide.</returns>
        internal static double TradeOffRatio(double primaryFrom, double primaryTo,
            double constrainedFrom, double constrainedTo)
        {
            if (constrainedTo == constrainedFrom) return double.NaN;
            return -(primaryTo - primaryFrom) / (constrainedTo - constrainedFrom);
        }

        /// <summary>
        /// Runs the sweep: derives or adopts the grid, selects per bound among the feasible
        /// and recommendation-eligible alternatives, screens the noninferior set over the
        /// fixed-feasible alternatives, and reports the per-bound trade-offs, binding flags,
        /// and infeasibility diagnostics.
        /// </summary>
        /// <param name="primaryName">The primary objective's declared name.</param>
        /// <param name="primaryDirection">The primary objective's direction.</param>
        /// <param name="epsilonMetricLabel">The swept objective's metric label.</param>
        /// <param name="names">The alternative names, in results row order.</param>
        /// <param name="primaryValues">The per-alternative primary values.</param>
        /// <param name="epsilonValues">The per-alternative swept values.</param>
        /// <param name="fixedFeasible">The per-alternative fixed-constraint satisfaction flags.</param>
        /// <param name="recommendationEligible">The per-alternative recommendation-eligibility flags (the do-no-harm gate).</param>
        /// <param name="epsilonGrid">The explicit swept bounds, or null for the automatic uniform grid.</param>
        /// <param name="gridPoints">The automatic grid's point count (endpoints inclusive).</param>
        /// <param name="diagnostics">The diagnostics sink.</param>
        /// <returns>The sweep results.</returns>
        /// <exception cref="ArgumentNullException">Thrown when a required argument is null.</exception>
        /// <exception cref="ArgumentException">Thrown when a per-alternative list does not parallel the names.</exception>
        internal static EpsilonSweepResults Run(string primaryName, ObjectiveDirection primaryDirection,
            string epsilonMetricLabel, IReadOnlyList<string> names, IReadOnlyList<double> primaryValues,
            IReadOnlyList<double> epsilonValues, IReadOnlyList<bool> fixedFeasible,
            IReadOnlyList<bool> recommendationEligible, IReadOnlyList<double>? epsilonGrid,
            int gridPoints, List<ComputationDiagnostic> diagnostics)
        {
            if (names == null) throw new ArgumentNullException(nameof(names));
            if (primaryValues == null) throw new ArgumentNullException(nameof(primaryValues));
            if (epsilonValues == null) throw new ArgumentNullException(nameof(epsilonValues));
            if (fixedFeasible == null) throw new ArgumentNullException(nameof(fixedFeasible));
            if (recommendationEligible == null) throw new ArgumentNullException(nameof(recommendationEligible));
            if (diagnostics == null) throw new ArgumentNullException(nameof(diagnostics));
            int count = names.Count;
            if (primaryValues.Count != count || epsilonValues.Count != count
                || fixedFeasible.Count != count || recommendationEligible.Count != count)
            {
                throw new ArgumentException("Every per-alternative list must parallel the names.", nameof(names));
            }

            // A NaN objective value excludes the alternative from the sweep, loudly.
            var candidate = new bool[count];
            for (int i = 0; i < count; i++)
            {
                bool hasNaN = double.IsNaN(primaryValues[i]) || double.IsNaN(epsilonValues[i]);
                if (hasNaN && fixedFeasible[i])
                {
                    diagnostics.Add(new ComputationDiagnostic("TRC2003", DiagnosticSeverity.Warning,
                        $"Alternative '{names[i]}' carries a NaN objective value and is excluded from the ε-constraint sweep.",
                        string.Empty));
                }
                candidate[i] = fixedFeasible[i] && !hasNaN;
            }

            double[] grid = BuildGrid(epsilonValues, candidate, epsilonGrid, gridPoints, diagnostics);
            IReadOnlyList<string> noninferior = NoninferiorNames(names, primaryValues, epsilonValues,
                candidate, primaryDirection);

            // The fixed-constraints-only optimum anchors the binding flags.
            int unconstrainedBest = Select(primaryValues, epsilonValues, candidate,
                recommendationEligible, primaryDirection, epsilon: double.PositiveInfinity);

            var entries = new List<EpsilonSweepEntry>(grid.Length);
            int previousSelection = -1;
            for (int g = 0; g < grid.Length; g++)
            {
                double epsilon = grid[g];
                int feasibleCount = 0;
                for (int i = 0; i < count; i++)
                {
                    if (candidate[i] && epsilonValues[i] <= epsilon) feasibleCount++;
                }
                int selection = Select(primaryValues, epsilonValues, candidate, recommendationEligible,
                    primaryDirection, epsilon);
                bool binding = unconstrainedBest >= 0 && epsilonValues[unconstrainedBest] > epsilon;
                if (selection < 0)
                {
                    entries.Add(new EpsilonSweepEntry(epsilon, null, double.NaN, double.NaN, double.NaN,
                        feasibleCount, binding, isInfeasible: true));
                    continue;
                }
                double tradeOff = double.NaN;
                if (previousSelection >= 0 && previousSelection != selection)
                {
                    tradeOff = TradeOffRatio(primaryValues[previousSelection], primaryValues[selection],
                        epsilonValues[previousSelection], epsilonValues[selection]);
                }
                entries.Add(new EpsilonSweepEntry(epsilon, names[selection], primaryValues[selection],
                    epsilonValues[selection], tradeOff, feasibleCount, binding, isInfeasible: false));
                previousSelection = selection;
            }

            return new EpsilonSweepResults(primaryName, primaryDirection, epsilonMetricLabel, grid,
                entries, noninferior);
        }

        /// <summary>
        /// Selects the best primary value among the candidates that are recommendation
        /// eligible and satisfy the swept upper bound; ties break to the first row.
        /// </summary>
        /// <param name="primaryValues">The primary values.</param>
        /// <param name="epsilonValues">The swept values.</param>
        /// <param name="candidate">The fixed-feasible non-NaN flags.</param>
        /// <param name="recommendationEligible">The eligibility flags.</param>
        /// <param name="direction">The primary direction.</param>
        /// <param name="epsilon">The swept upper bound.</param>
        /// <returns>The selected index, or −1 when nothing qualifies.</returns>
        private static int Select(IReadOnlyList<double> primaryValues, IReadOnlyList<double> epsilonValues,
            bool[] candidate, IReadOnlyList<bool> recommendationEligible, ObjectiveDirection direction,
            double epsilon)
        {
            int best = -1;
            for (int i = 0; i < primaryValues.Count; i++)
            {
                if (!candidate[i] || !recommendationEligible[i] || epsilonValues[i] > epsilon) continue;
                if (best < 0)
                {
                    best = i;
                    continue;
                }
                bool better = direction == ObjectiveDirection.Maximize
                    ? primaryValues[i] > primaryValues[best]
                    : primaryValues[i] < primaryValues[best];
                if (better) best = i;
            }
            return best;
        }

        /// <summary>
        /// Builds the swept grid: an explicit list sorts ascending; the automatic grid is the
        /// requested number of uniform values, endpoints inclusive, across the fixed-feasible
        /// alternatives' payoff range. A degenerate range yields a one-point grid; an empty
        /// candidate set yields an empty grid with a named diagnostic.
        /// </summary>
        /// <param name="epsilonValues">The swept values.</param>
        /// <param name="candidate">The fixed-feasible non-NaN flags.</param>
        /// <param name="epsilonGrid">The explicit bounds, or null.</param>
        /// <param name="gridPoints">The automatic point count.</param>
        /// <param name="diagnostics">The diagnostics sink.</param>
        /// <returns>The ascending grid.</returns>
        private static double[] BuildGrid(IReadOnlyList<double> epsilonValues, bool[] candidate,
            IReadOnlyList<double>? epsilonGrid, int gridPoints, List<ComputationDiagnostic> diagnostics)
        {
            if (epsilonGrid != null)
            {
                var explicitGrid = new double[epsilonGrid.Count];
                for (int i = 0; i < epsilonGrid.Count; i++)
                {
                    explicitGrid[i] = epsilonGrid[i];
                }
                Array.Sort(explicitGrid);
                return explicitGrid;
            }

            double low = double.PositiveInfinity;
            double high = double.NegativeInfinity;
            for (int i = 0; i < epsilonValues.Count; i++)
            {
                if (!candidate[i]) continue;
                if (epsilonValues[i] < low) low = epsilonValues[i];
                if (epsilonValues[i] > high) high = epsilonValues[i];
            }
            if (low > high)
            {
                diagnostics.Add(new ComputationDiagnostic("TRC2004", DiagnosticSeverity.Warning,
                    "No alternative satisfies the ε study's fixed constraints; the sweep is empty.",
                    string.Empty));
                return Array.Empty<double>();
            }
            if (low == high) return new[] { low };
            var grid = new double[gridPoints];
            for (int i = 0; i < gridPoints; i++)
            {
                grid[i] = low + i * (high - low) / (gridPoints - 1);
            }
            grid[gridPoints - 1] = high;
            return grid;
        }

        /// <summary>
        /// Screens the noninferior (weak-Pareto) set of the swept pair over the fixed-feasible
        /// alternatives — the primary direction-aware, the swept value minimized — keeping
        /// exact ties, ordered by ascending swept value then row order.
        /// </summary>
        /// <param name="names">The alternative names.</param>
        /// <param name="primaryValues">The primary values.</param>
        /// <param name="epsilonValues">The swept values.</param>
        /// <param name="candidate">The fixed-feasible non-NaN flags.</param>
        /// <param name="direction">The primary direction.</param>
        /// <returns>The noninferior names.</returns>
        private static IReadOnlyList<string> NoninferiorNames(IReadOnlyList<string> names,
            IReadOnlyList<double> primaryValues, IReadOnlyList<double> epsilonValues, bool[] candidate,
            ObjectiveDirection direction)
        {
            int count = names.Count;
            var noninferior = new List<int>();
            for (int i = 0; i < count; i++)
            {
                if (!candidate[i]) continue;
                bool dominated = false;
                for (int j = 0; j < count && !dominated; j++)
                {
                    if (j == i || !candidate[j]) continue;
                    bool primaryAtLeast = direction == ObjectiveDirection.Maximize
                        ? primaryValues[j] >= primaryValues[i]
                        : primaryValues[j] <= primaryValues[i];
                    bool primaryStrict = direction == ObjectiveDirection.Maximize
                        ? primaryValues[j] > primaryValues[i]
                        : primaryValues[j] < primaryValues[i];
                    bool epsilonAtMost = epsilonValues[j] <= epsilonValues[i];
                    bool epsilonStrict = epsilonValues[j] < epsilonValues[i];
                    dominated = primaryAtLeast && epsilonAtMost && (primaryStrict || epsilonStrict);
                }
                if (!dominated) noninferior.Add(i);
            }
            noninferior.Sort((a, b) =>
            {
                int byEpsilon = epsilonValues[a].CompareTo(epsilonValues[b]);
                return byEpsilon != 0 ? byEpsilon : a.CompareTo(b);
            });
            var result = new string[noninferior.Count];
            for (int i = 0; i < noninferior.Count; i++)
            {
                result[i] = names[noninferior[i]];
            }
            return result;
        }
    }
}
