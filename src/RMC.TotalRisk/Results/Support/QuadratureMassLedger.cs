using System;
using System.Buffers;

namespace RMC.TotalRisk.Results.Support
{
    /// <summary>
    /// The probability mass each abscissa of an adaptive Gauss–Kronrod pass carries, taken from
    /// the composite rule's own quadrature weights.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The integrator records only the nodes of ACCEPTED intervals, so an evaluation absent from
    /// the ledger belongs to an interval the refinement superseded and carries no measure. Weights
    /// are summed per abscissa: a hazard curve that saturates collapses several stratification bins
    /// onto one probability, and a zero-width bin is then evaluated repeatedly at the same point
    /// with zero weight.
    /// </para>
    /// <para>
    /// Populated by a single-threaded integration pass and read afterwards; not thread-safe.
    /// </para>
    /// </remarks>
    internal sealed class QuadratureMassLedger : IDisposable
    {
        /// <summary>
        /// Initializes an empty ledger.
        /// </summary>
        /// <param name="capacity">The expected node count.</param>
        public QuadratureMassLedger(int capacity = 1024)
        {
            int initial = capacity < 64 ? 64 : capacity;
            _abscissas = ArrayPool<double>.Shared.Rent(initial);
            _weights = ArrayPool<double>.Shared.Rent(initial);
        }

        /// <summary>The recorded abscissas, sorted once <see cref="Seal"/> has run.</summary>
        private double[] _abscissas;

        /// <summary>The weights parallel to <see cref="_abscissas"/>.</summary>
        private double[] _weights;

        /// <summary>The number of entries in use.</summary>
        private int _count;

        /// <summary>The compensation term of the running weight total.</summary>
        private double _compensation;

        /// <summary>Whether <see cref="Seal"/> has run.</summary>
        private bool _sealed;

        /// <summary>Whether the pooled buffers have been returned.</summary>
        private bool _disposed;

        /// <summary>
        /// The sum of every recorded weight, accumulated with compensated summation. Equals the
        /// width of the integration domain when the recorded set is complete.
        /// </summary>
        public double TotalWeight { get; private set; }

        /// <summary>
        /// The number of recorded nodes, before duplicate abscissas are coalesced.
        /// </summary>
        public int NodeCount => _count;

        /// <summary>
        /// The number of distinct abscissas; valid only after <see cref="Seal"/>.
        /// </summary>
        public int DistinctAbscissaCount { get; private set; }

        /// <summary>
        /// The <c>AdaptiveGaussKronrod.Recorder</c> callback.
        /// </summary>
        /// <param name="abscissa">The node's position.</param>
        /// <param name="weight">The node's quadrature weight, scaled by its interval's half-length.</param>
        /// <param name="value">The integrand value there; unused — the engine's integrand already consumed it.</param>
        /// <exception cref="InvalidOperationException">Thrown when the ledger has already been sealed.</exception>
        public void Record(double abscissa, double weight, double value)
        {
            if (_sealed) throw new InvalidOperationException("The quadrature ledger is sealed and cannot record further nodes.");
            EnsureNotDisposed();
            if (!double.IsFinite(abscissa)) throw new ArgumentOutOfRangeException(nameof(abscissa), "The quadrature abscissa must be finite.");
            if (!double.IsFinite(weight) || weight < 0d) throw new ArgumentOutOfRangeException(nameof(weight), "The quadrature weight must be finite and non-negative.");
            if (_count == _abscissas.Length)
            {
                Grow();
            }
            _abscissas[_count] = abscissa;
            _weights[_count] = weight;
            _count++;

            // Neumaier compensation: the total gates the recorded set against the domain width, so
            // it must not accumulate the drift of a naive sum over tens of thousands of nodes.
            double sum = TotalWeight + weight;
            _compensation += Math.Abs(TotalWeight) >= Math.Abs(weight)
                ? (TotalWeight - sum) + weight
                : (weight - sum) + TotalWeight;
            TotalWeight = sum;
        }

        /// <summary>
        /// Gets the compensated weight recorded so far, before or after sealing.
        /// </summary>
        /// <remarks>
        /// The risk engine uses this value after the interior Gauss?Kronrod pass to assign the
        /// final endpoint rectangle as the exact residual to a collectively exhaustive unit
        /// probability budget.
        /// </remarks>
        internal double RunningTotalWeight => _sealed ? TotalWeight : TotalWeight + _compensation;

        /// <summary>
        /// Doubles the pooled storage while preserving the recorded prefix.
        /// </summary>
        private void Grow()
        {
            int capacity = checked(_abscissas.Length * 2);
            double[] abscissas = ArrayPool<double>.Shared.Rent(capacity);
            double[] weights = ArrayPool<double>.Shared.Rent(capacity);
            Array.Copy(_abscissas, abscissas, _count);
            Array.Copy(_weights, weights, _count);
            ArrayPool<double>.Shared.Return(_abscissas, clearArray: false);
            ArrayPool<double>.Shared.Return(_weights, clearArray: false);
            _abscissas = abscissas;
            _weights = weights;
        }

        /// <summary>
        /// Throws when the ledger's pooled storage has already been released.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Thrown after <see cref="Dispose"/>.</exception>
        private void EnsureNotDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(QuadratureMassLedger));
        }

        /// <summary>
        /// Returns the ledger's storage to the shared array pool.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            ArrayPool<double>.Shared.Return(_abscissas, clearArray: false);
            ArrayPool<double>.Shared.Return(_weights, clearArray: false);
            _abscissas = Array.Empty<double>();
            _weights = Array.Empty<double>();
            _count = 0;
            DistinctAbscissaCount = 0;
            _disposed = true;
        }

        /// Sorts and coalesces the recorded nodes. Call once, after the integration returns.
        /// <summary>
        /// </summary>
        public void Seal()
        {
            if (_sealed) return;
            _sealed = true;
            EnsureNotDisposed();
            TotalWeight += _compensation;
            _compensation = 0d;
            if (_count == 0)
            {
                DistinctAbscissaCount = 0;
                return;
            }

            Array.Sort(_abscissas, _weights, 0, _count);

            int write = 0;
            double duplicateCompensation = 0d;
            for (int read = 1; read < _count; read++)
            {
                if (_abscissas[read] == _abscissas[write])
                {
                    double sum = _weights[write] + _weights[read];
                    duplicateCompensation += Math.Abs(_weights[write]) >= Math.Abs(_weights[read])
                        ? (_weights[write] - sum) + _weights[read]
                        : (_weights[read] - sum) + _weights[write];
                    _weights[write] = sum;
                }
                else
                {
                    _weights[write] += duplicateCompensation;
                    duplicateCompensation = 0d;
                    write++;
                    _abscissas[write] = _abscissas[read];
                    _weights[write] = _weights[read];
                }
            }
            _weights[write] += duplicateCompensation;
            DistinctAbscissaCount = write + 1;
        }

        /// <summary>
        /// Seals a collectively exhaustive ledger and absorbs only floating-point residue into
        /// its greatest-abscissa atom so the stored weights sum to exactly one.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no positive-mass node was recorded or the total differs materially from
        /// one.
        /// </exception>
        internal void SealExhaustive()
        {
            Seal();
            if (DistinctAbscissaCount == 0)
            {
                throw new InvalidOperationException("A collectively exhaustive quadrature ledger must contain at least one node.");
            }

            const double tolerance = 1e-12;
            double residual = 1d - TotalWeight;
            if (Math.Abs(residual) > tolerance)
            {
                throw new InvalidOperationException($"The collectively exhaustive quadrature weights sum to {TotalWeight:R} instead of one.");
            }

            _weights[DistinctAbscissaCount - 1] += residual;
            if (_weights[DistinctAbscissaCount - 1] < 0d)
            {
                throw new InvalidOperationException("The exhaustive-mass residual would make the final quadrature weight negative.");
            }
            TotalWeight = 1d;
        }

        /// <summary>
        /// The mass at an abscissa, or zero when it was never accepted.
        /// </summary>
        /// <param name="abscissa">The abscissa to look up; matched exactly.</param>
        /// <returns>The mass.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the ledger has not been sealed.</exception>
        public double MassAt(double abscissa)
        {
            TryGetMass(abscissa, out double mass);
            return mass;
        }

        /// <summary>
        /// Looks up an abscissa, reporting whether the quadrature accepted it. Distinguishes an
        /// accepted node of zero weight — a degenerate stratification bin produces those — from
        /// one the refinement superseded.
        /// </summary>
        /// <param name="abscissa">The abscissa to look up; matched exactly.</param>
        /// <param name="mass">Receives the mass, or zero when the abscissa was not accepted.</param>
        /// <returns>True when the abscissa was accepted.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the ledger has not been sealed.</exception>
        public bool TryGetMass(double abscissa, out double mass)
        {
            EnsureNotDisposed();
            if (!_sealed) throw new InvalidOperationException("The quadrature ledger must be sealed before it is read.");
            int index = Array.BinarySearch(_abscissas, 0, DistinctAbscissaCount, abscissa);
            mass = index >= 0 ? _weights[index] : 0d;
            return index >= 0;
        }
    }
}
