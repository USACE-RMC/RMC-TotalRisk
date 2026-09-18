using System;
using System.Collections.Generic;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// One bivariate component's secondary-axis discretization diagnostic: the a-posteriori
    /// answer to "are the conditional bins fine enough" — Richardson error estimates for the
    /// mean-pass annual failure probability and the mean incremental risk per consequence
    /// type, from mean evaluations at the configured, halved, and quartered bin counts.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// A plain query result — never serialized: the diagnostic is recomputable on demand and
    /// touches no stored state, no hash, and no seed. The estimates cover the mean pass; the
    /// per-realization discretization error varies with the sampled marginals, and the mean
    /// pass is where the bin count's adequacy is decided.
    /// </para>
    /// </remarks>
    public sealed class SecondaryDiscretizationDiagnostic
    {
        /// <summary>
        /// Initializes a diagnostic.
        /// </summary>
        /// <param name="componentName">The component's display name. Null coerces to empty.</param>
        /// <param name="bins">The configured bin count.</param>
        /// <param name="halfBins">The halved diagnostic count.</param>
        /// <param name="quarterBins">The quartered diagnostic count.</param>
        /// <param name="failureProbability">The annual-failure-probability estimate.</param>
        /// <param name="meanRisk">The mean incremental-risk estimates, one per consequence type (0 is the primary).</param>
        /// <exception cref="ArgumentNullException">Thrown when either estimate argument is null.</exception>
        public SecondaryDiscretizationDiagnostic(string? componentName, int bins, int halfBins, int quarterBins,
            DiscretizationEstimate failureProbability, IReadOnlyList<DiscretizationEstimate> meanRisk)
        {
            ComponentName = componentName ?? string.Empty;
            Bins = bins;
            HalfBins = halfBins;
            QuarterBins = quarterBins;
            FailureProbability = failureProbability ?? throw new ArgumentNullException(nameof(failureProbability));
            if (meanRisk == null) throw new ArgumentNullException(nameof(meanRisk));
            MeanRisk = Array.AsReadOnly(new List<DiscretizationEstimate>(meanRisk).ToArray());
        }

        /// <summary>
        /// The component's display name.
        /// </summary>
        public string ComponentName { get; }

        /// <summary>
        /// The configured secondary integration bin count the estimates apply to.
        /// </summary>
        public int Bins { get; }

        /// <summary>
        /// The halved diagnostic count behind the Richardson difference.
        /// </summary>
        public int HalfBins { get; }

        /// <summary>
        /// The quartered diagnostic count behind the convergence-ratio check.
        /// </summary>
        public int QuarterBins { get; }

        /// <summary>
        /// The mean-pass annual failure probability's discretization estimate.
        /// </summary>
        public DiscretizationEstimate FailureProbability { get; }

        /// <summary>
        /// The mean-pass incremental-risk (Excess-stream mean) discretization estimates, one
        /// per consequence type in declared order (0 is the primary type).
        /// </summary>
        public IReadOnlyList<DiscretizationEstimate> MeanRisk { get; }
    }
}
