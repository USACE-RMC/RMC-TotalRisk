using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// The design of one exact logic-tree enumeration run: the epistemic axes crossed, the
    /// branch combinations enumerated, their exact weight products, and the assignment of every
    /// realization index to its combination — the run's branch-attribution authority.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// An enumerated run computes one full-uncertainty ensemble of
    /// <see cref="RealizationCount"/> = <see cref="CombinationCount"/> ×
    /// <see cref="RealizationsPerCombination"/> realizations in which the block
    /// [c·M, (c+1)·M) is held on branch combination c, and publishes the per-realization weight
    /// vector w = W(c)/M on the ensemble, where W(c) is the product of the combination's
    /// per-axis normalized branch weights. Combinations index lexicographically with axis 0 the
    /// most significant digit. The map is a runtime-only query result — never serialized, and
    /// absent from an analysis restored from stored results.
    /// </para>
    /// <para>
    /// Because the analysis run computes on component clones, reading branch attribution from
    /// the authoring composites after a run is invalid; this map is the post-run attribution
    /// surface, re-derivable from the model alone (the axis order, branch sets, and forcing
    /// percentiles are functions of the composites' declared weights).
    /// </para>
    /// </remarks>
    public sealed class LogicTreeEnumerationMap
    {
        /// <summary>The per-axis decode strides (axis 0 most significant).</summary>
        private readonly int[] _strides;

        /// <summary>
        /// Initializes the enumeration design.
        /// </summary>
        /// <param name="axes">The enumeration axes, in deterministic order.</param>
        /// <param name="realizationsPerCombination">The continuous-knowledge realization count per combination (M).</param>
        /// <param name="combinationWeights">The exact combination weights, one per lexicographic combination.</param>
        /// <exception cref="ArgumentNullException">Thrown when the axes or weights are null.</exception>
        /// <exception cref="ArgumentException">Thrown when no axis exists or the weight count does not equal the combination count.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the per-combination count is not positive.</exception>
        internal LogicTreeEnumerationMap(IList<LogicTreeAxis> axes, int realizationsPerCombination,
            double[] combinationWeights)
        {
            if (axes == null) throw new ArgumentNullException(nameof(axes));
            if (combinationWeights == null) throw new ArgumentNullException(nameof(combinationWeights));
            if (axes.Count == 0) throw new ArgumentException("A logic-tree enumeration needs at least one axis.", nameof(axes));
            if (realizationsPerCombination < 1)
                throw new ArgumentOutOfRangeException(nameof(realizationsPerCombination), "The per-combination realization count must be at least one.");

            long combinations = 1;
            _strides = new int[axes.Count];
            for (int a = axes.Count - 1; a >= 0; a--)
            {
                _strides[a] = (int)Math.Min(int.MaxValue, combinations);
                combinations *= axes[a].Branches.Count;
            }
            if (combinations > int.MaxValue)
                throw new ArgumentException("The branch-combination count overflows the enumeration index range.", nameof(axes));
            if (combinationWeights.Length != combinations)
                throw new ArgumentException("The combination weight count must equal the product of the axis branch counts.", nameof(combinationWeights));

            Axes = new ReadOnlyCollection<LogicTreeAxis>(axes);
            CombinationCount = (int)combinations;
            RealizationsPerCombination = realizationsPerCombination;
            long realizations = combinations * realizationsPerCombination;
            if (realizations > int.MaxValue)
                throw new ArgumentException("The enumeration realization count overflows the realization index range.", nameof(realizationsPerCombination));
            RealizationCount = (int)realizations;
            CombinationWeights = new ReadOnlyCollection<double>(combinationWeights);
        }

        /// <summary>
        /// The enumeration axes: shared variables first (ordinal name order), then unbound
        /// composites (function-id order).
        /// </summary>
        public IReadOnlyList<LogicTreeAxis> Axes { get; }

        /// <summary>
        /// The number of branch combinations K — the product of the axis branch counts.
        /// </summary>
        public int CombinationCount { get; }

        /// <summary>
        /// The continuous-knowledge realization count per combination, M.
        /// </summary>
        public int RealizationsPerCombination { get; }

        /// <summary>
        /// The run's realization count, N = K·M.
        /// </summary>
        public int RealizationCount { get; }

        /// <summary>
        /// The exact combination weights W(c) — the product of each combination's per-axis
        /// normalized branch weights — indexed lexicographically with axis 0 most significant.
        /// </summary>
        public IReadOnlyList<double> CombinationWeights { get; }

        /// <summary>
        /// The branch combination a realization index belongs to.
        /// </summary>
        /// <param name="realizationIndex">The realization index, in [0, <see cref="RealizationCount"/>).</param>
        /// <returns>The combination index.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the index is outside the run.</exception>
        public int CombinationOf(int realizationIndex)
        {
            if (realizationIndex < 0 || realizationIndex >= RealizationCount)
                throw new ArgumentOutOfRangeException(nameof(realizationIndex), "The realization index is outside the enumerated run.");
            return realizationIndex / RealizationsPerCombination;
        }

        /// <summary>
        /// The branch a combination selects on one axis.
        /// </summary>
        /// <param name="combinationIndex">The combination index, in [0, <see cref="CombinationCount"/>).</param>
        /// <param name="axisIndex">The axis index, in [0, <c>Axes.Count</c>).</param>
        /// <returns>The position in that axis's <see cref="LogicTreeAxis.Branches"/> list.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when either index is outside the enumeration.</exception>
        public int BranchIndexOf(int combinationIndex, int axisIndex)
        {
            if (combinationIndex < 0 || combinationIndex >= CombinationCount)
                throw new ArgumentOutOfRangeException(nameof(combinationIndex), "The combination index is outside the enumeration.");
            if (axisIndex < 0 || axisIndex >= Axes.Count)
                throw new ArgumentOutOfRangeException(nameof(axisIndex), "The axis index is outside the enumeration.");
            return combinationIndex / _strides[axisIndex] % Axes[axisIndex].Branches.Count;
        }

        /// <summary>
        /// The exact epistemic weight of one realization — its combination's weight divided by
        /// the per-combination realization count, the value the run publishes at that index of
        /// <see cref="EnsembleResults.RealizationWeights"/>.
        /// </summary>
        /// <param name="realizationIndex">The realization index, in [0, <see cref="RealizationCount"/>).</param>
        /// <returns>The realization weight.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the index is outside the run.</exception>
        public double RealizationWeight(int realizationIndex)
        {
            return CombinationWeights[CombinationOf(realizationIndex)] / RealizationsPerCombination;
        }
    }
}
