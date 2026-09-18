using System;
using Numerics.Distributions;
using Numerics.Distributions.Copulas;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// The frozen per-realization snapshot of a bivariate hazard the engine's conditional-bin loop
    /// consumes: the sampled secondary (Y) marginal distribution, an independently cloned copula,
    /// and the non-allocating trapezoid discretization kernel over the hazard's precomputed
    /// conditional-probability nodes and weights.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// One snapshot serves exactly one realization on one thread. The copula is a deep clone and
    /// the Y marginal is that realization's sampled distribution, because neither is safe to share
    /// across threads (a sampled empirical distribution mutates its interpolation search state on
    /// every lookup). The node and weight vectors are shared read-only across all of a hazard's
    /// snapshots — they are realization-independent, precomputed once per sampler setup.
    /// </para>
    /// <para>
    /// The discretization (docs/requirements/BIVARIATE_RISK_DESIGN.md): N bins produce
    /// N + 1 nodes t_j = j/N uniform on [0, 1] in CONDITIONAL-probability space, with the endpoint
    /// nodes clamped to [1e-16, 1 − 1e-16] for inverse evaluations (the engine's probability floor,
    /// keeping y finite for unbounded marginals); y_j inverts the copula's conditional distribution
    /// through the sampled Y marginal; trapezoid weights are w_0 = w_N = 1/(2N) and 1/N between,
    /// with the LAST weight computed as the exact residual 1 − Σ so the ordered weight sum is
    /// exactly one in floating point. Equal-probability nodes adapt to the conditional density, so
    /// the same node set serves every copula family; under independence the nodes are the plain
    /// marginal quantiles.
    /// </para>
    /// </remarks>
    public sealed class SampledBivariateHazard
    {
        #region Construction

        /// <summary>
        /// Initializes a frozen per-realization snapshot over the owning hazard's precomputed
        /// discretization vectors.
        /// </summary>
        /// <param name="marginalY">The realization's sampled secondary (Y) marginal distribution.</param>
        /// <param name="copula">The independently cloned copula (fixed parameters).</param>
        /// <param name="conditionalNodes">
        /// The precomputed conditional-probability nodes t_j (endpoint-clamped), length bins + 1.
        /// Shared read-only across snapshots.
        /// </param>
        /// <param name="conditionalWeights">
        /// The precomputed trapezoid weights, index-aligned with the nodes and summing exactly to
        /// one. Shared read-only across snapshots.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when any argument is null.</exception>
        internal SampledBivariateHazard(IUnivariateDistribution marginalY, BivariateCopula copula,
            double[] conditionalNodes, double[] conditionalWeights)
        {
            _marginalY = marginalY ?? throw new ArgumentNullException(nameof(marginalY));
            _copula = copula ?? throw new ArgumentNullException(nameof(copula));
            _conditionalNodes = conditionalNodes ?? throw new ArgumentNullException(nameof(conditionalNodes));
            _conditionalWeights = conditionalWeights ?? throw new ArgumentNullException(nameof(conditionalWeights));
        }

        #endregion

        #region Members

        /// <summary>
        /// The realization's sampled secondary (Y) marginal distribution.
        /// </summary>
        private readonly IUnivariateDistribution _marginalY;

        /// <summary>
        /// The independently cloned copula evaluated by the kernel.
        /// </summary>
        private readonly BivariateCopula _copula;

        /// <summary>
        /// The precomputed conditional-probability nodes (endpoint-clamped), shared read-only
        /// across the owning hazard's snapshots.
        /// </summary>
        private readonly double[] _conditionalNodes;

        /// <summary>
        /// The precomputed trapezoid weights, index-aligned with the nodes, shared read-only
        /// across the owning hazard's snapshots.
        /// </summary>
        private readonly double[] _conditionalWeights;

        /// <summary>
        /// The realization's sampled secondary (Y) marginal distribution — the curve the kernel
        /// inverts at each conditional node.
        /// </summary>
        public IUnivariateDistribution MarginalY => _marginalY;

        /// <summary>
        /// The independently cloned copula this snapshot evaluates. Fixed parameters — the clone
        /// exists for thread isolation, never for per-realization dependence uncertainty.
        /// </summary>
        public BivariateCopula Copula => _copula;

        /// <summary>
        /// The number of trapezoidal integration bins behind this snapshot.
        /// </summary>
        public int SecondaryIntegrationBins => _conditionalNodes.Length - 1;

        /// <summary>
        /// The number of conditional nodes the kernel fills: <see cref="SecondaryIntegrationBins"/>
        /// + 1. Caller-owned buffers must be at least this long.
        /// </summary>
        public int ConditionalNodeCount => _conditionalNodes.Length;

        #endregion

        #region Methods

        /// <summary>
        /// Fills the caller-owned buffers with the conditional secondary hazard nodes and their
        /// trapezoid weights at the given primary non-exceedance probability:
        /// y_j = MarginalY.InverseCDF(copula.InverseConditionalCDF(u, t_j)) over the precomputed
        /// nodes, weights copied via <see cref="Array.Copy(Array, Array, int)"/>. No allocation.
        /// </summary>
        /// <param name="u">
        /// The primary slice's NON-exceedance probability, strictly inside (0, 1) — the engine
        /// clamps its slice probabilities to [1e-16, 1 − 1e-16] before calling.
        /// </param>
        /// <param name="yNodes">
        /// Receives the conditional secondary hazard values; length ≥
        /// <see cref="ConditionalNodeCount"/>. Entries beyond the node count are untouched.
        /// </param>
        /// <param name="weights">
        /// Receives the trapezoid weights, index-aligned with <paramref name="yNodes"/> and summing
        /// exactly to one; length ≥ <see cref="ConditionalNodeCount"/>. Entries beyond the node
        /// count are untouched.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when either buffer is null.</exception>
        /// <exception cref="ArgumentException">Thrown when either buffer is shorter than <see cref="ConditionalNodeCount"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="u"/> is not strictly inside (0, 1).</exception>
        public void FillConditionalBins(double u, double[] yNodes, double[] weights)
        {
            if (yNodes == null) throw new ArgumentNullException(nameof(yNodes));
            if (weights == null) throw new ArgumentNullException(nameof(weights));

            int count = _conditionalNodes.Length;
            if (yNodes.Length < count)
                throw new ArgumentException($"The node buffer must hold at least {count} entries.", nameof(yNodes));
            if (weights.Length < count)
                throw new ArgumentException($"The weight buffer must hold at least {count} entries.", nameof(weights));
            if (!(u > 0d && u < 1d))
                throw new ArgumentOutOfRangeException(nameof(u), "The primary non-exceedance probability must be strictly inside (0, 1).");

            for (int j = 0; j < count; j++)
            {
                yNodes[j] = _marginalY.InverseCDF(_copula.InverseConditionalCDF(u, _conditionalNodes[j]));
            }
            Array.Copy(_conditionalWeights, weights, count);
        }

        /// <summary>
        /// Inverts one conditional node: y = MarginalY.InverseCDF(copula.InverseConditionalCDF(u, t))
        /// — the single-point analog of <see cref="FillConditionalBins"/>, with the conditional
        /// probability clamped into the same [1e-16, 1 − 1e-16] band the precomputed nodes use, so
        /// an adaptively placed node reproduces a precomputed node's value bit-exactly at equal t.
        /// </summary>
        /// <param name="u">
        /// The primary slice's NON-exceedance probability, strictly inside (0, 1) — the engine
        /// clamps its slice probabilities to [1e-16, 1 − 1e-16] before calling.
        /// </param>
        /// <param name="t">The conditional probability of the secondary given the primary slice.</param>
        /// <returns>The conditional secondary hazard value.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="u"/> is not strictly inside (0, 1).</exception>
        public double InverseConditional(double u, double t)
        {
            if (!(u > 0d && u < 1d))
                throw new ArgumentOutOfRangeException(nameof(u), "The primary non-exceedance probability must be strictly inside (0, 1).");
            if (t < 1e-16) t = 1e-16;
            else if (t > 1d - 1e-16) t = 1d - 1e-16;
            return _marginalY.InverseCDF(_copula.InverseConditionalCDF(u, t));
        }

        #endregion
    }
}
