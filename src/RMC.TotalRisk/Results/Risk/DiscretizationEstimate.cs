using System;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// One quantity's secondary-axis discretization estimate: the mean-pass values at the
    /// configured, halved, and quartered bin counts, the Richardson extrapolation at the
    /// second-order trapezoid rate, the resulting relative-error estimate for the configured
    /// count, and the observed convergence ratio that says whether the asymptotic rate the
    /// extrapolation assumes is actually in force.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The conditional trapezoid rule converges as O(N⁻²) on smooth integrands, so halving the
    /// bin count multiplies the discretization error by four: the extrapolated value is
    /// V* = V_N + (V_N − V_(N/2))/3 and the error estimate for V_N is |V_(N/2) − V_N|/3. The
    /// observed ratio (V_(N/4) − V_(N/2))/(V_(N/2) − V_N) is the regime check — near four the
    /// asymptotic rate holds and the estimate is trustworthy; far from four (heavy conditional
    /// tail concentration is the documented mechanism) the estimate is indicative only and
    /// more bins, or the collapse arrangement, deserve consideration.
    /// </para>
    /// </remarks>
    public sealed class DiscretizationEstimate
    {
        /// <summary>
        /// Initializes an estimate.
        /// </summary>
        /// <param name="value">The mean-pass value at the configured bin count.</param>
        /// <param name="halfValue">The value at the halved count.</param>
        /// <param name="quarterValue">The value at the quartered count.</param>
        public DiscretizationEstimate(double value, double halfValue, double quarterValue)
        {
            Value = value;
            HalfValue = halfValue;
            QuarterValue = quarterValue;
        }

        /// <summary>
        /// The mean-pass value at the configured bin count.
        /// </summary>
        public double Value { get; }

        /// <summary>
        /// The mean-pass value at the halved bin count.
        /// </summary>
        public double HalfValue { get; }

        /// <summary>
        /// The mean-pass value at the quartered bin count.
        /// </summary>
        public double QuarterValue { get; }

        /// <summary>
        /// The Richardson extrapolation at the second-order rate: Value + (Value − HalfValue)/3.
        /// </summary>
        public double ExtrapolatedValue => Value + (Value - HalfValue) / 3d;

        /// <summary>
        /// The estimated absolute discretization error of <see cref="Value"/>:
        /// |HalfValue − Value|/3.
        /// </summary>
        public double EstimatedError => Math.Abs(HalfValue - Value) / 3d;

        /// <summary>
        /// The estimated relative discretization error of <see cref="Value"/> against the
        /// extrapolated value; NaN when the extrapolated value is zero or non-finite.
        /// </summary>
        public double EstimatedRelativeError
        {
            get
            {
                double reference = Math.Abs(ExtrapolatedValue);
                return reference > 0d && double.IsFinite(reference) ? EstimatedError / reference : double.NaN;
            }
        }

        /// <summary>
        /// The observed convergence ratio (QuarterValue − HalfValue)/(HalfValue − Value) — near
        /// four when the second-order rate the extrapolation assumes is in force; NaN when the
        /// finer difference is zero.
        /// </summary>
        public double ObservedRatio
        {
            get
            {
                double fine = HalfValue - Value;
                return fine != 0d && double.IsFinite(fine) ? (QuarterValue - HalfValue) / fine : double.NaN;
            }
        }
    }
}
