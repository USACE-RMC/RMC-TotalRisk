using System;
using Numerics;
using RMC.TotalRisk.Core.Interfaces;

namespace RMC.TotalRisk.Analyses.Risk
{
    /// <summary>
    /// A runtime-only epistemic conditioning pin: holds one named risk function at a fixed
    /// knowledge percentile while the rest of the ensemble varies.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Pins produce conditional risk statements — "risk given the 95th-percentile hazard
    /// curve" — the hazard-fractile cross-tabulation a seismic or SSHAC-style review expects,
    /// and clean one-at-a-time holds. A pin is immutable (replace to edit) and keys on
    /// <see cref="IRiskFunction.Id"/> — the rename-proof, clone-stable identity that is
    /// stripped from canonical hashing, so a pin can never perturb a seed. Pins are consumed at
    /// sampler setup by overwriting the target function's own pre-allocated percentile matrix:
    /// every other function's matrix is generated from its own dedicated stream, so no other
    /// draw can move — the seed-inertness the runtime-only contract promises. The pinned
    /// function's knowledge column becomes the constant pin percentile, which the sensitivity
    /// and value-of-information surfaces report as an inert (zero-share) input.
    /// </para>
    /// </remarks>
    public sealed class FractilePin
    {
        /// <summary>
        /// Initializes a fractile pin.
        /// </summary>
        /// <param name="functionId">The <see cref="IRiskFunction.Id"/> of the function to hold.</param>
        /// <param name="percentile">The knowledge percentile to hold it at, strictly inside (0, 1).</param>
        /// <exception cref="ArgumentException">Thrown when the function id is empty.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the percentile is not strictly inside (0, 1).</exception>
        public FractilePin(Guid functionId, double percentile)
        {
            if (functionId == Guid.Empty)
                throw new ArgumentException("The fractile pin requires a function id.", nameof(functionId));
            if (!Tools.IsFinite(percentile) || percentile <= 0d || percentile >= 1d)
                throw new ArgumentOutOfRangeException(nameof(percentile), "The pinned percentile must lie strictly inside (0, 1).");
            FunctionId = functionId;
            Percentile = percentile;
        }

        /// <summary>
        /// The id of the function to hold (<see cref="IRiskFunction.Id"/> — rename-proof and
        /// clone-stable).
        /// </summary>
        public Guid FunctionId { get; }

        /// <summary>
        /// The knowledge percentile the function is held at, strictly inside (0, 1).
        /// </summary>
        public double Percentile { get; }
    }
}
