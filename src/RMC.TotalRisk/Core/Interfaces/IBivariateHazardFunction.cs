using System.Collections.Generic;
using Numerics.Distributions;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Core.Interfaces
{
    /// <summary>
    /// The bivariate hazard contract: a primary (X) and a secondary (Y) marginal hazard function
    /// coupled by a copula, with the secondary dimension integrated per primary slice over a
    /// conditional-probability trapezoid discretization. The engine integrates the PRIMARY axis
    /// exactly as it does a univariate hazard — <see cref="IHazardFunction.SampleFunction()"/>
    /// returns the sampled X marginal — and evaluates the conditional Y bins inside its
    /// per-hazard-level objective.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The marginals are typed <see cref="IHazardFunction"/> rather than
    /// <see cref="IUnivariateHazardFunction"/> deliberately: univariateness is enforced by
    /// <see cref="IRiskFunction.Validate"/>, not by the type system, so admitting a bivariate
    /// marginal (a nested/vine dependence structure) would require no contract or serialization
    /// break.
    /// The copula convention throughout is that (u, v) are NON-exceedance marginal probabilities:
    /// upper-tail dependence in (u, v) is joint extreme-hazard dependence, and an exceedance
    /// probability p converts as u = 1 − p.
    /// </para>
    /// </remarks>
    public interface IBivariateHazardFunction : IHazardFunction
    {
        /// <summary>
        /// The primary (X) marginal hazard function — a reference to a univariate hazard function
        /// stored by the consuming layer, never owned content. Null while unconfigured or when a
        /// serialized name-only reference could not be resolved (reported by
        /// <see cref="IRiskFunction.Validate"/>).
        /// </summary>
        IHazardFunction? MarginalX { get; set; }

        /// <summary>
        /// The secondary (Y) marginal hazard function — a reference to a univariate hazard
        /// function stored by the consuming layer, never owned content. Null while unconfigured or
        /// when a serialized name-only reference could not be resolved (reported by
        /// <see cref="IRiskFunction.Validate"/>).
        /// </summary>
        IHazardFunction? MarginalY { get; set; }

        /// <summary>
        /// The hazard type of the secondary (Y) axis (e.g., "Pool Duration"). Axis-label metadata —
        /// serialized, never hashed. The primary axis uses the inherited
        /// <see cref="IRiskFunction.SpecifiedHazard"/>.
        /// </summary>
        string SecondarySpecifiedHazard { get; set; }

        /// <summary>
        /// The unit of the secondary (Y) axis (e.g., "days"). Axis-label metadata — serialized,
        /// never hashed. The primary axis uses the inherited <see cref="IRiskFunction.HazardUnit"/>.
        /// </summary>
        string SecondaryHazardUnit { get; set; }

        /// <summary>
        /// The number of trapezoidal integration bins discretizing the conditional secondary
        /// distribution per primary slice. Default 20; <see cref="IRiskFunction.Validate"/> requires
        /// the value to lie in [3, 1000] — there is no silent clamp. Compute-relevant and hashed.
        /// </summary>
        int SecondaryIntegrationBins { get; set; }

        /// <summary>
        /// The number of conditional nodes the discretization produces:
        /// <see cref="SecondaryIntegrationBins"/> + 1. Caller-owned bin buffers must be at least
        /// this long.
        /// </summary>
        int ConditionalNodeCount { get; }

        /// <summary>
        /// Samples the mean secondary (Y) marginal — the expected frequency curve across the Y
        /// marginal's knowledge uncertainty.
        /// </summary>
        /// <returns>The mean secondary hazard distribution.</returns>
        IUnivariateDistribution SampleSecondaryFunction();

        /// <summary>
        /// Samples the secondary (Y) marginal at a fixed knowledge-uncertainty percentile.
        /// </summary>
        /// <param name="percentile">The percentile in (0, 1) driving the co-monotonic sample.</param>
        /// <returns>The sampled secondary hazard distribution.</returns>
        IUnivariateDistribution SampleSecondaryFunction(double percentile);

        /// <summary>
        /// Samples the secondary (Y) marginal for the given realization from its content-seeded
        /// sampler. <see cref="IRiskFunction.SetupSampler"/> must be called first.
        /// </summary>
        /// <param name="realizationIndex">The realization index in [0, sample size).</param>
        /// <returns>The sampled secondary hazard distribution.</returns>
        IUnivariateDistribution SampleSecondaryFunction(int realizationIndex);

        /// <summary>
        /// The minimum hazard value the secondary (Y) marginal can produce.
        /// </summary>
        /// <param name="meanOnly">
        /// When true, the bound of the mean curve only; when false, the bound across the marginal's
        /// full knowledge uncertainty.
        /// </param>
        /// <returns>The minimum secondary hazard value.</returns>
        double MinSecondaryHazard(bool meanOnly);

        /// <summary>
        /// The maximum hazard value the secondary (Y) marginal can produce.
        /// </summary>
        /// <param name="meanOnly">
        /// When true, the bound of the mean curve only; when false, the bound across the marginal's
        /// full knowledge uncertainty.
        /// </param>
        /// <returns>The maximum secondary hazard value.</returns>
        double MaxSecondaryHazard(bool meanOnly);

        /// <summary>
        /// Samples the frozen mean bivariate snapshot the engine's conditional-bin loop consumes on
        /// the mean pass and the deterministic probes: the mean Y marginal (the expected frequency
        /// curve across the Y marginal's knowledge uncertainty), an independently cloned copula,
        /// and the non-allocating <see cref="SampledBivariateHazard.FillConditionalBins"/> kernel
        /// over the precomputed trapezoid nodes and weights.
        /// <see cref="IRiskFunction.SetupSampler"/> must be called first (it precomputes the
        /// node and weight vectors).
        /// </summary>
        /// <returns>The frozen mean bivariate snapshot.</returns>
        SampledBivariateHazard SampleBivariate();

        /// <summary>
        /// Samples the frozen per-realization bivariate snapshot the engine's conditional-bin loop
        /// consumes: the sampled Y marginal, an independently cloned copula, and the non-allocating
        /// <see cref="SampledBivariateHazard.FillConditionalBins"/> kernel over the precomputed
        /// trapezoid nodes and weights. <see cref="IRiskFunction.SetupSampler"/> must be called
        /// first.
        /// </summary>
        /// <param name="realizationIndex">The realization index in [0, sample size).</param>
        /// <returns>The frozen bivariate snapshot for the realization.</returns>
        SampledBivariateHazard SampleBivariate(int realizationIndex);

        /// <summary>
        /// Evaluates the conditional secondary discretization at a primary hazard level for one
        /// realization, returning the (Y, Weight) node list with weights summing to one. A
        /// convenience for diagnostics, oracles, and consuming-layer previews only — it allocates
        /// per call; the engine path is the <see cref="SampleBivariate(int)"/> overloads +
        /// <see cref="SampledBivariateHazard.FillConditionalBins"/> over caller-owned buffers.
        /// </summary>
        /// <param name="realizationIndex">The realization index in [0, sample size).</param>
        /// <param name="xHazardLevel">
        /// The primary hazard level conditioning the secondary distribution; converted to its
        /// non-exceedance probability through the sampled X marginal and clamped to
        /// [1e-16, 1 − 1e-16].
        /// </param>
        /// <returns>The conditional (Y, Weight) nodes, weights summing to one.</returns>
        IReadOnlyList<(double Y, double Weight)> SampleConditionalYGivenX(int realizationIndex, double xHazardLevel);
    }
}
