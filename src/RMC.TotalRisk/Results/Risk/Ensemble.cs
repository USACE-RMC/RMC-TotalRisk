using System;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// The runtime store of full per-realization results during a Monte Carlo run — an indexed
    /// array of <see cref="SystemRealization"/> written by the parallel realization loop.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Runtime working state, never serialized: the engine post-processes the ensemble into the
    /// percentile curves and the compact <c>EnsembleResults</c>, then discards it. Each parallel
    /// realization writes only its own index — the index-owned-write discipline that keeps results
    /// bit-identical at any thread count.
    /// </para>
    /// </remarks>
    public class Ensemble
    {
        /// <summary>
        /// The realization store.
        /// </summary>
        private SystemRealization?[] _list = Array.Empty<SystemRealization?>();

        /// <summary>
        /// Initializes an empty ensemble.
        /// </summary>
        public Ensemble()
        {
        }

        /// <summary>
        /// Initializes an ensemble sized for the given realization count.
        /// </summary>
        /// <param name="length">The realization count. Must not be negative.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the length is negative.</exception>
        public Ensemble(int length)
        {
            SetEnsembleLength(length);
        }

        /// <summary>
        /// Gets or sets the realization at the given index.
        /// </summary>
        /// <param name="index">The zero-based realization index.</param>
        /// <returns>The realization, or null when the slot has not been written.</returns>
        public SystemRealization? this[int index]
        {
            get { return _list[index]; }
            set { _list[index] = value; }
        }

        /// <summary>
        /// The number of realization slots.
        /// </summary>
        public int Count => _list.Length;

        /// <summary>
        /// Resizes the ensemble, clearing all current realizations.
        /// </summary>
        /// <param name="length">The realization count. Must not be negative.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the length is negative.</exception>
        public void SetEnsembleLength(int length)
        {
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length), "The ensemble length must not be negative.");
            _list = new SystemRealization?[length];
        }

        /// <summary>
        /// Removes all realizations.
        /// </summary>
        public void Clear()
        {
            _list = Array.Empty<SystemRealization?>();
        }
    }
}
