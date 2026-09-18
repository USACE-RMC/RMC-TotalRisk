using System;
using System.Collections.Generic;
using System.Linq;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Results.CostBenefit
{
    /// <summary>
    /// One criterion's shared-state regret analysis: the aligned logic-tree states with their
    /// exact weights, the per-alternative block-mean, standard-error, and regret matrices,
    /// the max and expected regrets, the per-state win counts, and the minimax and
    /// expected-regret picks.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// States are the exact logic-tree branch combinations shared by every compared
    /// alternative, and state weights are the retained exact branch-weight products,
    /// unrenormalized. Cell values are Monte Carlo block means over each state's aligned
    /// realization block, so every cell carries block noise at the published standard error
    /// (NaN when a single realization per combination leaves no variance estimate) — the
    /// noise label states that discipline. Regret is direction-aware against the per-state
    /// best value, and per-state win counts credit exact ties to every tied alternative.
    /// </para>
    /// </remarks>
    public sealed class RegretMatrixResults
    {
        /// <summary>
        /// Initializes a regret-matrix results block.
        /// </summary>
        /// <param name="criterionLabel">The compared criterion's display label.</param>
        /// <param name="direction">The optimization direction of the criterion.</param>
        /// <param name="alternativeNames">The compared alternative names, in results row order.</param>
        /// <param name="stateLabels">The shared-state labels, in enumeration order.</param>
        /// <param name="stateWeights">The exact state weights, parallel to the state labels.</param>
        /// <param name="values">The block-mean matrix (one row per alternative, one column per state).</param>
        /// <param name="standardErrors">The block standard-error matrix, congruent to the values.</param>
        /// <param name="regrets">The direction-aware regret matrix, congruent to the values.</param>
        /// <param name="maxRegrets">The per-alternative maximum regrets, parallel to the names.</param>
        /// <param name="expectedRegrets">The per-alternative expected regrets, parallel to the names.</param>
        /// <param name="winCounts">The per-alternative per-state win counts (ties credited to all), parallel to the names.</param>
        /// <param name="minimaxIndex">The minimax-regret pick's row index, or −1 when withheld.</param>
        /// <param name="expectedIndex">The expected-regret pick's row index, or −1 when withheld.</param>
        /// <param name="realizationsPerCombination">The realizations per shared state (the block size M).</param>
        /// <param name="noiseLabel">The block-noise discipline label.</param>
        /// <exception cref="ArgumentNullException">Thrown when a required argument is null.</exception>
        /// <exception cref="ArgumentException">Thrown when a matrix is not rectangular over the axes, a list does not parallel its axis, a pick index is out of range, or the block size is not positive.</exception>
        public RegretMatrixResults(string criterionLabel, ObjectiveDirection direction,
            IReadOnlyList<string> alternativeNames, IReadOnlyList<string> stateLabels,
            IReadOnlyList<double> stateWeights, IReadOnlyList<IReadOnlyList<double>> values,
            IReadOnlyList<IReadOnlyList<double>> standardErrors,
            IReadOnlyList<IReadOnlyList<double>> regrets, IReadOnlyList<double> maxRegrets,
            IReadOnlyList<double> expectedRegrets, IReadOnlyList<int> winCounts,
            int minimaxIndex, int expectedIndex, int realizationsPerCombination, string noiseLabel)
        {
            CriterionLabel = criterionLabel ?? throw new ArgumentNullException(nameof(criterionLabel));
            NoiseLabel = noiseLabel ?? throw new ArgumentNullException(nameof(noiseLabel));
            if (alternativeNames == null) throw new ArgumentNullException(nameof(alternativeNames));
            if (stateLabels == null) throw new ArgumentNullException(nameof(stateLabels));
            if (stateWeights == null) throw new ArgumentNullException(nameof(stateWeights));
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (standardErrors == null) throw new ArgumentNullException(nameof(standardErrors));
            if (regrets == null) throw new ArgumentNullException(nameof(regrets));
            if (maxRegrets == null) throw new ArgumentNullException(nameof(maxRegrets));
            if (expectedRegrets == null) throw new ArgumentNullException(nameof(expectedRegrets));
            if (winCounts == null) throw new ArgumentNullException(nameof(winCounts));
            if (stateWeights.Count != stateLabels.Count)
                throw new ArgumentException("The state weights must parallel the state labels.", nameof(stateWeights));
            if (maxRegrets.Count != alternativeNames.Count || expectedRegrets.Count != alternativeNames.Count
                || winCounts.Count != alternativeNames.Count)
            {
                throw new ArgumentException("Every per-alternative list must parallel the alternative names.",
                    nameof(alternativeNames));
            }
            if (minimaxIndex < -1 || minimaxIndex >= alternativeNames.Count)
                throw new ArgumentException("The minimax pick must be −1 or a valid row index.", nameof(minimaxIndex));
            if (expectedIndex < -1 || expectedIndex >= alternativeNames.Count)
                throw new ArgumentException("The expected-regret pick must be −1 or a valid row index.", nameof(expectedIndex));
            if (realizationsPerCombination < 1)
                throw new ArgumentException("The block size must be positive.", nameof(realizationsPerCombination));
            Values = FreezeMatrix(values, alternativeNames.Count, stateLabels.Count, nameof(values));
            StandardErrors = FreezeMatrix(standardErrors, alternativeNames.Count, stateLabels.Count, nameof(standardErrors));
            Regrets = FreezeMatrix(regrets, alternativeNames.Count, stateLabels.Count, nameof(regrets));
            Direction = direction;
            AlternativeNames = Array.AsReadOnly(alternativeNames.ToArray());
            StateLabels = Array.AsReadOnly(stateLabels.ToArray());
            StateWeights = Array.AsReadOnly(stateWeights.ToArray());
            MaxRegrets = Array.AsReadOnly(maxRegrets.ToArray());
            ExpectedRegrets = Array.AsReadOnly(expectedRegrets.ToArray());
            WinCounts = Array.AsReadOnly(winCounts.ToArray());
            MinimaxIndex = minimaxIndex;
            ExpectedIndex = expectedIndex;
            RealizationsPerCombination = realizationsPerCombination;
        }

        /// <summary>
        /// Freezes one matrix into read-only rows after checking it is rectangular over the
        /// alternative and state axes.
        /// </summary>
        /// <param name="matrix">The matrix to freeze.</param>
        /// <param name="rowCount">The required row count (alternatives).</param>
        /// <param name="columnCount">The required column count (states).</param>
        /// <param name="parameterName">The parameter name for the guard message.</param>
        /// <returns>The frozen matrix.</returns>
        /// <exception cref="ArgumentException">Thrown when the matrix is not rectangular over the axes.</exception>
        private static IReadOnlyList<IReadOnlyList<double>> FreezeMatrix(
            IReadOnlyList<IReadOnlyList<double>> matrix, int rowCount, int columnCount, string parameterName)
        {
            if (matrix.Count != rowCount)
                throw new ArgumentException("The matrix must carry one row per alternative.", parameterName);
            var rows = new IReadOnlyList<double>[matrix.Count];
            for (int i = 0; i < matrix.Count; i++)
            {
                if (matrix[i] == null || matrix[i].Count != columnCount)
                    throw new ArgumentException("Every matrix row must carry one column per state.", parameterName);
                rows[i] = Array.AsReadOnly(matrix[i].ToArray());
            }
            return Array.AsReadOnly(rows);
        }

        /// <summary>The compared criterion's display label.</summary>
        public string CriterionLabel { get; }

        /// <summary>The optimization direction of the criterion.</summary>
        public ObjectiveDirection Direction { get; }

        /// <summary>The compared alternative names, in results row order.</summary>
        public IReadOnlyList<string> AlternativeNames { get; }

        /// <summary>The shared-state labels, in enumeration order.</summary>
        public IReadOnlyList<string> StateLabels { get; }

        /// <summary>The exact state weights, parallel to the state labels, unrenormalized.</summary>
        public IReadOnlyList<double> StateWeights { get; }

        /// <summary>The block-mean matrix (one row per alternative, one column per state).</summary>
        public IReadOnlyList<IReadOnlyList<double>> Values { get; }

        /// <summary>The block standard-error matrix, congruent to the values (NaN at block size one).</summary>
        public IReadOnlyList<IReadOnlyList<double>> StandardErrors { get; }

        /// <summary>The direction-aware regret matrix, congruent to the values.</summary>
        public IReadOnlyList<IReadOnlyList<double>> Regrets { get; }

        /// <summary>The per-alternative maximum regrets, parallel to the names.</summary>
        public IReadOnlyList<double> MaxRegrets { get; }

        /// <summary>The per-alternative expected regrets under the exact state weights.</summary>
        public IReadOnlyList<double> ExpectedRegrets { get; }

        /// <summary>The per-alternative per-state win counts (exact ties credited to every tied alternative).</summary>
        public IReadOnlyList<int> WinCounts { get; }

        /// <summary>The minimax-regret pick's row index, or −1 when withheld.</summary>
        public int MinimaxIndex { get; }

        /// <summary>The expected-regret pick's row index, or −1 when withheld.</summary>
        public int ExpectedIndex { get; }

        /// <summary>The realizations per shared state (the block size M).</summary>
        public int RealizationsPerCombination { get; }

        /// <summary>The block-noise discipline label.</summary>
        public string NoiseLabel { get; }
    }
}
