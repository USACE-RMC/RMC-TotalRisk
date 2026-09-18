using System.Collections.Generic;
using Numerics;

namespace RMC.TotalRisk.Results.Support
{
    /// <summary>
    /// Applies the engine's deterministic unit-budget boundary to an ordered probability partition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each cell is accepted in its established enumeration order and clipped only against the
    /// remaining unit budget. Earlier cells are never rescaled, so this is clipping rather than
    /// proportional normalization.
    /// </para>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    internal static class ProbabilityPartitionBoundary
    {
        /// <summary>
        /// Clips an exclusive partition sequentially against its remaining unit probability budget.
        /// </summary>
        /// <param name="probabilities">The exclusive cells in deterministic enumeration order.</param>
        internal static void ClipInPlace(List<double> probabilities)
        {
            double remaining = 1d;
            for (int i = 0; i < probabilities.Count; i++)
            {
                double accepted = Tools.Clamp(probabilities[i], 0d, remaining);
                probabilities[i] = accepted;
                remaining = Tools.Clamp(remaining - accepted, 0d, 1d);
            }
        }
    }
}
