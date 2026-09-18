namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// One exposure-period quantity reduced over the epistemic ensemble: the weighted lower,
    /// median, and upper percentiles and the weighted mean.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// A plain query result — never serialized. NaN throughout when no valid realization
    /// survives the pairwise filtering (the all-NaN convention).
    /// </para>
    /// </remarks>
    public sealed class ExposurePeriodInterval
    {
        /// <summary>
        /// Initializes the interval.
        /// </summary>
        /// <param name="lower">The lower percentile of the quantity.</param>
        /// <param name="median">The median of the quantity.</param>
        /// <param name="mean">The weighted mean of the quantity.</param>
        /// <param name="upper">The upper percentile of the quantity.</param>
        public ExposurePeriodInterval(double lower, double median, double mean, double upper)
        {
            Lower = lower;
            Median = median;
            Mean = mean;
            Upper = upper;
        }

        /// <summary>
        /// The lower percentile (at half the complement of the configured confidence width).
        /// </summary>
        public double Lower { get; }

        /// <summary>
        /// The median (the 50th percentile).
        /// </summary>
        public double Median { get; }

        /// <summary>
        /// The weighted mean.
        /// </summary>
        public double Mean { get; }

        /// <summary>
        /// The upper percentile (the complement of <see cref="Lower"/>'s level).
        /// </summary>
        public double Upper { get; }
    }
}
