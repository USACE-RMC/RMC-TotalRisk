using Numerics.Data;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Core.Support
{
    /// <summary>
    /// Maps the model-level <see cref="ExtrapolationPolicy"/> onto the Numerics lookup surface.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// <see cref="ExtrapolationPolicy.None"/> through <see cref="ExtrapolationPolicy.Both"/> map
    /// to the numerically equal <see cref="ExtrapolationSides"/> members.
    /// <see cref="ExtrapolationPolicy.Error"/> maps to <see cref="ExtrapolationSides.None"/> —
    /// the sampled wrapper holds its endpoints while the range check and the loud diagnostic are
    /// applied by the model library's evaluation guards, so a guard that is somehow bypassed
    /// degrades to the historical hold rather than to a silent extension.
    /// </para>
    /// </remarks>
    internal static class ExtrapolationSupport
    {
        /// <summary>
        /// Maps a model extrapolation policy onto the Numerics extrapolation sides forwarded to
        /// the sampled lookup wrappers.
        /// </summary>
        /// <param name="policy">The model-level policy.</param>
        /// <returns>The sides the sampled wrapper extends; <see cref="ExtrapolationSides.None"/>
        /// for both <see cref="ExtrapolationPolicy.None"/> and
        /// <see cref="ExtrapolationPolicy.Error"/>.</returns>
        internal static ExtrapolationSides Map(ExtrapolationPolicy policy)
        {
            switch (policy)
            {
                case ExtrapolationPolicy.Below:
                    return ExtrapolationSides.Below;
                case ExtrapolationPolicy.Above:
                    return ExtrapolationSides.Above;
                case ExtrapolationPolicy.Both:
                    return ExtrapolationSides.Both;
                default:
                    return ExtrapolationSides.None;
            }
        }
    }
}
