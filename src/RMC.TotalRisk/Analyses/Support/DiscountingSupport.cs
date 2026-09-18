using System;
using Numerics;

namespace RMC.TotalRisk.Analyses.Support
{
    /// <summary>
    /// Discounting arithmetic shared by the risk engine's horizon queries and the cost-benefit
    /// layer: the annuity present-value factor and the single-year discount factor, both
    /// evaluated in log space so every consumer reproduces the same values bit-for-bit.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     These are the engine's horizon conventions: exposure is end-of-year (year k's amount
    ///     discounts by (1 + r)^−k, so a year-zero amount is undiscounted while the first
    ///     exposure year's risk discounts one full period), and both factors take their exact
    ///     r → 0 limits rather than evaluating an indeterminate form.
    /// </para>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    internal static class DiscountingSupport
    {
        /// <summary>
        /// The annuity present-value factor for one unit per year over the given horizon —
        /// (1 − (1 + r)^−n)/r with the exact r → 0 limit n, evaluated in log space (the
        /// exposure-period conversions' exact expression shape, so stationary life-cycle
        /// aggregates reproduce them bit-for-bit).
        /// </summary>
        /// <param name="years">The horizon in years.</param>
        /// <param name="discountRate">The annual discount rate.</param>
        /// <returns>The annuity factor.</returns>
        internal static double AnnuityFactor(int years, double discountRate)
        {
            return discountRate > 0d
                ? -Tools.Expm1(-years * Tools.Log1p(discountRate)) / discountRate
                : years;
        }

        /// <summary>
        /// The present-value factor for a single amount at the given year — (1 + r)^−year,
        /// evaluated in log space. Year zero is exactly one (an amount falling now is
        /// undiscounted), as is every year at a zero rate.
        /// </summary>
        /// <param name="year">The year the amount falls in (0 = now).</param>
        /// <param name="discountRate">The annual discount rate.</param>
        /// <returns>The discount factor.</returns>
        internal static double DiscountFactor(int year, double discountRate)
        {
            return Math.Exp(-year * Tools.Log1p(discountRate));
        }
    }
}
