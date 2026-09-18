using System;
using Numerics.Distributions;

namespace RMC.TotalRisk.Results.Support
{
    /// <summary>
    /// The adopted conditional-probability mesh of one per-slice adaptive Gauss–Kronrod pass
    /// over the secondary axis in the probit coordinate: the accepted composite rule's
    /// (z, weight) nodes, sorted, coalesced, Jacobian-folded, and renormalized so the
    /// probability masses sum to exactly one — the same exhaustive-weight contract the fixed
    /// conditional-trapezoid vectors carry.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The integrator records only the nodes of accepted intervals (21 per interval), with
    /// weights carrying the interval half-lengths so the raw sum equals the width of the
    /// probit integration domain [Φ⁻¹(1e-16), Φ⁻¹(1 − 1e-16)]. Adoption sorts by z,
    /// coalesces exact-duplicate abscissas with compensated summation, folds each node's
    /// weight into its probability mass w·φ(z), gates the folded total against one, and
    /// renormalizes exactly to a unit sum — restoring the bit-exact unit weight sum every
    /// downstream exhaustive-entry gate relies on. Owned by one sampled component, reset and
    /// refilled per evaluation on one thread; not thread-safe.
    /// </para>
    /// </remarks>
    internal sealed class ConditionalQuadratureMesh
    {
        /// <summary>
        /// Initializes a mesh with a fixed capacity — the refinement-budget bound on adopted
        /// nodes, which the recorder can never exceed by construction.
        /// </summary>
        /// <param name="capacity">The maximum node count.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the capacity is not positive.</exception>
        public ConditionalQuadratureMesh(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "The mesh capacity must be positive.");
            _nodes = new double[capacity];
            _weights = new double[capacity];
        }

        /// <summary>The recorded conditional-probability nodes, sorted once adopted.</summary>
        private readonly double[] _nodes;

        /// <summary>The weights parallel to <see cref="_nodes"/>.</summary>
        private readonly double[] _weights;

        /// <summary>The number of entries in use (raw before adoption, coalesced after).</summary>
        private int _count;

        /// <summary>Whether <see cref="AdoptProbitExhaustive"/> has run since the last <see cref="Reset"/>.</summary>
        private bool _adopted;

        /// <summary>The adopted node count.</summary>
        public int Count => _count;

        /// <summary>The adopted conditional-probability nodes (valid entries: 0 .. <see cref="Count"/> − 1).</summary>
        public double[] Nodes => _nodes;

        /// <summary>The adopted weights, index-aligned with <see cref="Nodes"/> and summing exactly to one.</summary>
        public double[] Weights => _weights;

        /// <summary>
        /// Clears the mesh for the next evaluation.
        /// </summary>
        public void Reset()
        {
            _count = 0;
            _adopted = false;
        }

        /// <summary>
        /// The <c>AdaptiveGaussKronrod.Recorder</c> callback: appends one accepted node.
        /// </summary>
        /// <param name="t">The conditional-probability abscissa.</param>
        /// <param name="weight">The node's quadrature weight, scaled by its interval's half-length.</param>
        /// <param name="value">The integrand value there; unused — the surrogate already consumed it.</param>
        /// <exception cref="InvalidOperationException">Thrown when the mesh is adopted or its budget-bound capacity would be exceeded.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown on a non-finite abscissa or a non-finite or negative weight.</exception>
        public void Record(double t, double weight, double value)
        {
            if (_adopted) throw new InvalidOperationException("The conditional mesh is adopted and cannot record further nodes.");
            if (!double.IsFinite(t)) throw new ArgumentOutOfRangeException(nameof(t), "The conditional abscissa must be finite.");
            if (!double.IsFinite(weight) || weight < 0d) throw new ArgumentOutOfRangeException(nameof(weight), "The conditional weight must be finite and non-negative.");
            if (_count == _nodes.Length)
            {
                throw new InvalidOperationException("The conditional mesh exceeded its refinement-budget capacity — the evaluation budget no longer bounds the accepted composite rule.");
            }
            _nodes[_count] = t;
            _weights[_count] = weight;
            _count++;
        }

        /// <summary>
        /// Adopts a probit-coordinate mesh: the recorded nodes are standard-normal abscissas z
        /// and the recorded weights are the quadrature weights in z, so each node's probability
        /// mass is its weight times φ(z). Sorts by z, coalesces exact-duplicate abscissas with
        /// compensated summation (adjacent accepted intervals never share an interior node by
        /// placement, so duplicates are degenerate coincidences only — handled for
        /// robustness), folds the Jacobian into the stored weights, gates the folded total
        /// against one at the mass-sanity tolerance (the φ-quadrature deficiency of the
        /// accepted mesh — refined down with the tolerance, never structural loss), and
        /// renormalizes exactly to a unit sum with the residual on the largest-mass node
        /// (first on ties; an extreme-z node's Jacobian-folded mass sits at the φ tail scale,
        /// far below the normalization's rounding noise, so the edge nodes cannot carry the
        /// residual without rounding negative).
        /// </summary>
        /// <param name="ownerName">The owner named in the gate diagnostics.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no node was recorded, the folded total departs one by more than the
        /// sanity tolerance, or the residual would make the carrier weight negative
        /// (unreachable from rounding alone).
        /// </exception>
        public void AdoptProbitExhaustive(string ownerName)
        {
            if (_adopted) return;
            _adopted = true;
            if (_count == 0)
            {
                throw new InvalidOperationException($"The adaptive conditional mesh of {ownerName} recorded no accepted nodes.");
            }

            Array.Sort(_nodes, _weights, 0, _count);

            int write = 0;
            double duplicateCompensation = 0d;
            for (int read = 1; read < _count; read++)
            {
                if (_nodes[read] == _nodes[write])
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
                    _nodes[write] = _nodes[read];
                    _weights[write] = _weights[read];
                }
            }
            _weights[write] += duplicateCompensation;
            _count = write + 1;

            double total = 0d;
            double compensation = 0d;
            for (int i = 0; i < _count; i++)
            {
                double mass = _weights[i] * Normal.StandardPDF(_nodes[i]);
                _weights[i] = mass;
                double sum = total + mass;
                compensation += Math.Abs(total) >= Math.Abs(mass)
                    ? (total - sum) + mass
                    : (mass - sum) + total;
                total = sum;
            }
            total += compensation;

            const double sanity = 1e-3;
            if (!(Math.Abs(1d - total) <= sanity))
            {
                throw new InvalidOperationException($"The probit conditional mass of {ownerName} sums to {total:R} instead of one.");
            }

            int carrier = 0;
            for (int i = 1; i < _count; i++)
            {
                if (_weights[i] > _weights[carrier]) carrier = i;
            }
            double running = 0d;
            compensation = 0d;
            for (int i = 0; i < _count; i++)
            {
                if (i == carrier) continue;
                double normalized = _weights[i] / total;
                _weights[i] = normalized;
                double sum = running + normalized;
                compensation += Math.Abs(running) >= Math.Abs(normalized)
                    ? (running - sum) + normalized
                    : (normalized - sum) + running;
                running = sum;
            }
            _weights[carrier] = 1d - (running + compensation);
            if (_weights[carrier] < 0d)
            {
                throw new InvalidOperationException($"The conditional-mesh residual of {ownerName} would make the carrier weight negative.");
            }
        }
    }
}
