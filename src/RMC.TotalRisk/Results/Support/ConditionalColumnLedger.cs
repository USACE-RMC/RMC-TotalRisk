using System;
using System.Buffers;

namespace RMC.TotalRisk.Results.Support
{
    /// <summary>
    /// The flushed composite rule of a two-dimensional adaptive Gauss–Kronrod pass over
    /// (primary non-exceedance, conditional probability), grouped by exact primary abscissa
    /// into the per-slice conditional columns the staged bivariate evaluation replays — one
    /// merged risk point per distinct abscissa, so every downstream one-point-per-abscissa
    /// gate reuses unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The tensor rule evaluates 21 x-columns of 21 t-nodes per region and flushes only the
    /// final (unsplit) regions, with each weight carrying the region half-lengths so the raw
    /// sum equals the domain area. Sealing sorts globally by (x, t, flush order), coalesces
    /// exact-duplicate (x, t) nodes with compensated summation, and walks the equal-x runs into
    /// groups: a group's compensated weight total is the primary-axis mass its merged risk
    /// point records, and <see cref="FillGroupNormalized"/> hands the staged evaluation the
    /// group's t-nodes with weights normalized to sum exactly to one (residual absorbed into
    /// the largest-mass node — the fixed trapezoid vectors' contract). Where the refinement
    /// splits the primary axis at different conditional bands, neighboring groups cover
    /// complementary conditional slabs; their masses partition the area exactly, and the
    /// merged points' entries are conditional on the covered slab — the documented
    /// output-granularity property of the two-dimensional rule.
    /// </para>
    /// <para>
    /// Populated by single-threaded integration passes and read afterwards; not thread-safe.
    /// </para>
    /// </remarks>
    internal sealed class ConditionalColumnLedger : IDisposable
    {
        /// <summary>
        /// Initializes an empty ledger.
        /// </summary>
        /// <param name="capacity">The expected node count.</param>
        public ConditionalColumnLedger(int capacity = 4096)
        {
            int initial = capacity < 441 ? 441 : capacity;
            _abscissas = ArrayPool<double>.Shared.Rent(initial);
            _nodes = ArrayPool<double>.Shared.Rent(initial);
            _weights = ArrayPool<double>.Shared.Rent(initial);
            _order = ArrayPool<int>.Shared.Rent(initial);
        }

        /// <summary>The recorded primary abscissas.</summary>
        private double[] _abscissas;

        /// <summary>The recorded conditional-probability nodes.</summary>
        private double[] _nodes;

        /// <summary>The weights parallel to the node arrays.</summary>
        private double[] _weights;

        /// <summary>The flush-order sequence, the deterministic tiebreak of the seal sort.</summary>
        private int[] _order;

        /// <summary>The number of entries in use (raw before sealing, coalesced after).</summary>
        private int _count;

        /// <summary>The group start indices, valid after <see cref="Seal"/>.</summary>
        private int[]? _groupStart;

        /// <summary>The compensated group masses, valid after <see cref="Seal"/>.</summary>
        private double[]? _groupMass;

        /// <summary>Whether <see cref="Seal"/> has run.</summary>
        private bool _sealed;

        /// <summary>Whether the pooled buffers have been returned.</summary>
        private bool _disposed;

        /// <summary>The number of groups (distinct primary abscissas); valid after <see cref="Seal"/>.</summary>
        public int GroupCount { get; private set; }

        /// <summary>
        /// The largest group's node count; valid after <see cref="Seal"/> — sizes the replay
        /// buffers once for the whole pass.
        /// </summary>
        public int MaxGroupNodeCount { get; private set; }

        /// <summary>
        /// The <c>AdaptiveGaussKronrod2D.Recorder</c> callback: appends one flushed node.
        /// </summary>
        /// <param name="x">The primary non-exceedance abscissa.</param>
        /// <param name="t">The conditional-probability abscissa.</param>
        /// <param name="weight">The node's tensor quadrature weight, scaled by the region half-lengths.</param>
        /// <param name="value">The integrand value there; unused — the surrogate already consumed it.</param>
        /// <exception cref="InvalidOperationException">Thrown when the ledger has already been sealed.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown on a non-finite abscissa or a non-finite or negative weight.</exception>
        public void Record(double x, double t, double weight, double value)
        {
            if (_sealed) throw new InvalidOperationException("The conditional column ledger is sealed and cannot record further nodes.");
            EnsureNotDisposed();
            if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x), "The primary abscissa must be finite.");
            if (!double.IsFinite(t)) throw new ArgumentOutOfRangeException(nameof(t), "The conditional abscissa must be finite.");
            if (!double.IsFinite(weight) || weight < 0d) throw new ArgumentOutOfRangeException(nameof(weight), "The quadrature weight must be finite and non-negative.");
            if (_count == _abscissas.Length)
            {
                Grow();
            }
            _abscissas[_count] = x;
            _nodes[_count] = t;
            _weights[_count] = weight;
            _order[_count] = _count;
            _count++;
        }

        /// <summary>
        /// Sorts globally by (x, t, flush order), coalesces exact-duplicate (x, t) nodes with
        /// compensated summation, and derives the per-abscissa groups with their compensated
        /// masses. Call once, after every strip's integration has flushed.
        /// </summary>
        public void Seal()
        {
            if (_sealed) return;
            _sealed = true;
            EnsureNotDisposed();
            if (_count == 0)
            {
                GroupCount = 0;
                MaxGroupNodeCount = 0;
                return;
            }

            // A deterministic total order: primary abscissa, then conditional abscissa, then
            // flush order — layout-independent and bit-stable across identical passes.
            var abscissas = _abscissas;
            var nodes = _nodes;
            var weights = _weights;
            var permutation = new int[_count];
            for (int i = 0; i < _count; i++)
            {
                permutation[i] = i;
            }
            Array.Sort(permutation, (a, b) =>
            {
                int byX = abscissas[a].CompareTo(abscissas[b]);
                if (byX != 0) return byX;
                int byT = nodes[a].CompareTo(nodes[b]);
                if (byT != 0) return byT;
                return _order[a].CompareTo(_order[b]);
            });

            // Apply the permutation into fresh pooled arrays (the sorted views become the
            // stored state; the originals return to the pool).
            double[] sortedX = ArrayPool<double>.Shared.Rent(_count);
            double[] sortedT = ArrayPool<double>.Shared.Rent(_count);
            double[] sortedW = ArrayPool<double>.Shared.Rent(_count);
            for (int i = 0; i < _count; i++)
            {
                sortedX[i] = abscissas[permutation[i]];
                sortedT[i] = nodes[permutation[i]];
                sortedW[i] = weights[permutation[i]];
            }
            ArrayPool<double>.Shared.Return(_abscissas, clearArray: false);
            ArrayPool<double>.Shared.Return(_nodes, clearArray: false);
            ArrayPool<double>.Shared.Return(_weights, clearArray: false);
            _abscissas = sortedX;
            _nodes = sortedT;
            _weights = sortedW;

            // Coalesce exact-duplicate (x, t) nodes with compensated summation.
            int write = 0;
            double duplicateCompensation = 0d;
            for (int read = 1; read < _count; read++)
            {
                if (_abscissas[read] == _abscissas[write] && _nodes[read] == _nodes[write])
                {
                    double pairSum = _weights[write] + _weights[read];
                    duplicateCompensation += Math.Abs(_weights[write]) >= Math.Abs(_weights[read])
                        ? (_weights[write] - pairSum) + _weights[read]
                        : (_weights[read] - pairSum) + _weights[write];
                    _weights[write] = pairSum;
                }
                else
                {
                    _weights[write] += duplicateCompensation;
                    duplicateCompensation = 0d;
                    write++;
                    _abscissas[write] = _abscissas[read];
                    _nodes[write] = _nodes[read];
                    _weights[write] = _weights[read];
                }
            }
            _weights[write] += duplicateCompensation;
            _count = write + 1;

            // Derive the equal-x groups and their compensated masses.
            int groups = 1;
            for (int i = 1; i < _count; i++)
            {
                if (_abscissas[i] != _abscissas[i - 1]) groups++;
            }
            GroupCount = groups;
            _groupStart = ArrayPool<int>.Shared.Rent(groups + 1);
            _groupMass = ArrayPool<double>.Shared.Rent(groups);
            int group = 0;
            _groupStart[0] = 0;
            double mass = 0d, compensation = 0d;
            int maxNodes = 0;
            for (int i = 0; i < _count; i++)
            {
                if (i > 0 && _abscissas[i] != _abscissas[i - 1])
                {
                    _groupMass[group] = mass + compensation;
                    maxNodes = Math.Max(maxNodes, i - _groupStart[group]);
                    group++;
                    _groupStart[group] = i;
                    mass = 0d;
                    compensation = 0d;
                }
                double weight = _weights[i];
                double sum = mass + weight;
                compensation += Math.Abs(mass) >= Math.Abs(weight)
                    ? (mass - sum) + weight
                    : (weight - sum) + mass;
                mass = sum;
            }
            _groupMass[group] = mass + compensation;
            _groupStart[group + 1] = _count;
            MaxGroupNodeCount = Math.Max(maxNodes, _count - _groupStart[group]);
        }

        /// <summary>The group's primary abscissa; valid after <see cref="Seal"/>.</summary>
        /// <param name="group">The group index, ascending in the primary abscissa.</param>
        /// <returns>The abscissa.</returns>
        public double GroupAbscissa(int group)
        {
            EnsureSealed();
            return _abscissas[_groupStart![group]];
        }

        /// <summary>The group's compensated mass — the primary-axis probability mass its merged risk point records.</summary>
        /// <param name="group">The group index.</param>
        /// <returns>The mass.</returns>
        public double GroupMass(int group)
        {
            EnsureSealed();
            return _groupMass![group];
        }

        /// <summary>The group's node count.</summary>
        /// <param name="group">The group index.</param>
        /// <returns>The node count.</returns>
        public int GroupNodeCount(int group)
        {
            EnsureSealed();
            return _groupStart![group + 1] - _groupStart[group];
        }

        /// <summary>
        /// Fills the caller-owned buffers with the group's conditional-probability nodes
        /// (t-ascending) and its weights normalized to sum exactly to one: each weight divides
        /// by the group mass, with the largest-mass node assigned the exact residual — the
        /// fixed trapezoid vectors' unit-sum contract, transplanted onto the adopted column.
        /// The residual carrier is the largest node (first on ties) rather than the last
        /// because a probit-edge node's mass can sit below the summation's rounding noise,
        /// while the largest normalized weight is at least the reciprocal node count — orders
        /// above it — so the residual assignment can never round negative.
        /// </summary>
        /// <param name="group">The group index.</param>
        /// <param name="tNodes">Receives the conditional-probability nodes; length ≥ the group's node count.</param>
        /// <param name="weights">Receives the normalized weights, index-aligned and summing exactly to one.</param>
        /// <returns>The group's node count.</returns>
        /// <exception cref="ArgumentNullException">Thrown when either buffer is null.</exception>
        /// <exception cref="ArgumentException">Thrown when either buffer is shorter than the group.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the ledger is unsealed, the group mass is not positive, or the residual
        /// would make the carrier weight negative (unreachable from rounding alone).
        /// </exception>
        public int FillGroupNormalized(int group, double[] tNodes, double[] weights)
        {
            EnsureSealed();
            if (tNodes == null) throw new ArgumentNullException(nameof(tNodes));
            if (weights == null) throw new ArgumentNullException(nameof(weights));
            int start = _groupStart![group];
            int count = _groupStart[group + 1] - start;
            if (tNodes.Length < count) throw new ArgumentException($"The node buffer must hold at least {count} entries.", nameof(tNodes));
            if (weights.Length < count) throw new ArgumentException($"The weight buffer must hold at least {count} entries.", nameof(weights));

            double mass = _groupMass![group];
            if (!(mass > 0d))
            {
                throw new InvalidOperationException("A conditional column group must carry positive mass.");
            }

            int carrier = start;
            for (int i = start + 1; i < start + count; i++)
            {
                if (_weights[i] > _weights[carrier]) carrier = i;
            }
            double running = 0d, compensation = 0d;
            for (int i = 0; i < count; i++)
            {
                tNodes[i] = _nodes[start + i];
                if (start + i == carrier) continue;
                double normalized = _weights[start + i] / mass;
                weights[i] = normalized;
                double sum = running + normalized;
                compensation += Math.Abs(running) >= Math.Abs(normalized)
                    ? (running - sum) + normalized
                    : (normalized - sum) + running;
                running = sum;
            }
            weights[carrier - start] = 1d - (running + compensation);
            if (weights[carrier - start] < 0d)
            {
                throw new InvalidOperationException("The conditional column's normalization residual would make the carrier weight negative.");
            }
            return count;
        }

        /// <summary>
        /// The number of recorded nodes so far (raw before sealing) — the strip-range anchor
        /// for <see cref="ScaleRange"/>.
        /// </summary>
        public int NodeCount => _count;

        /// <summary>
        /// Scales a recorded weight range in place — the per-strip probit mass renormalization:
        /// each strip's Jacobian-folded masses scale so their sum equals the strip's exact
        /// probability width, bounding the φ-quadrature deficiency by the refinement tolerance.
        /// </summary>
        /// <param name="start">The first node index of the range.</param>
        /// <param name="count">The range length.</param>
        /// <param name="factor">The positive scale factor.</param>
        /// <exception cref="InvalidOperationException">Thrown once the ledger is sealed.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown on an invalid range or a non-positive or non-finite factor.</exception>
        public void ScaleRange(int start, int count, double factor)
        {
            if (_sealed) throw new InvalidOperationException("The conditional column ledger is sealed and cannot be rescaled.");
            EnsureNotDisposed();
            if (start < 0 || count < 0 || start + count > _count)
                throw new ArgumentOutOfRangeException(nameof(count), "The scale range must lie within the recorded nodes.");
            if (!double.IsFinite(factor) || factor <= 0d)
                throw new ArgumentOutOfRangeException(nameof(factor), "The scale factor must be finite and positive.");
            for (int i = start; i < start + count; i++)
            {
                _weights[i] *= factor;
            }
        }

        /// <summary>
        /// Doubles the pooled storage while preserving the recorded prefix.
        /// </summary>
        private void Grow()
        {
            int capacity = checked(_abscissas.Length * 2);
            double[] abscissas = ArrayPool<double>.Shared.Rent(capacity);
            double[] nodes = ArrayPool<double>.Shared.Rent(capacity);
            double[] weights = ArrayPool<double>.Shared.Rent(capacity);
            int[] order = ArrayPool<int>.Shared.Rent(capacity);
            Array.Copy(_abscissas, abscissas, _count);
            Array.Copy(_nodes, nodes, _count);
            Array.Copy(_weights, weights, _count);
            Array.Copy(_order, order, _count);
            ArrayPool<double>.Shared.Return(_abscissas, clearArray: false);
            ArrayPool<double>.Shared.Return(_nodes, clearArray: false);
            ArrayPool<double>.Shared.Return(_weights, clearArray: false);
            ArrayPool<int>.Shared.Return(_order, clearArray: false);
            _abscissas = abscissas;
            _nodes = nodes;
            _weights = weights;
            _order = order;
        }

        /// <summary>
        /// Throws when the ledger has not been sealed.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown before <see cref="Seal"/>.</exception>
        private void EnsureSealed()
        {
            EnsureNotDisposed();
            if (!_sealed) throw new InvalidOperationException("The conditional column ledger must be sealed before it is read.");
        }

        /// <summary>
        /// Throws when the ledger's pooled storage has already been released.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Thrown after <see cref="Dispose"/>.</exception>
        private void EnsureNotDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ConditionalColumnLedger));
        }

        /// <summary>
        /// Returns the ledger's storage to the shared array pool.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            ArrayPool<double>.Shared.Return(_abscissas, clearArray: false);
            ArrayPool<double>.Shared.Return(_nodes, clearArray: false);
            ArrayPool<double>.Shared.Return(_weights, clearArray: false);
            ArrayPool<int>.Shared.Return(_order, clearArray: false);
            if (_groupStart != null) ArrayPool<int>.Shared.Return(_groupStart, clearArray: false);
            if (_groupMass != null) ArrayPool<double>.Shared.Return(_groupMass, clearArray: false);
            _abscissas = Array.Empty<double>();
            _nodes = Array.Empty<double>();
            _weights = Array.Empty<double>();
            _order = Array.Empty<int>();
            _groupStart = null;
            _groupMass = null;
            _count = 0;
            GroupCount = 0;
            _disposed = true;
        }
    }
}
