namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// The joint (VEGAS) integration's per-run convergence certificate: the recording-pass
    /// chi-squared consistency and relative standard error of the mean pass and the ensemble,
    /// assembled from quantities the integration passes already produce.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// A runtime-only diagnostic — never serialized, no influence on any computed or stored
    /// value; present only after a joint-method run. The distinction it exists to carry: the
    /// persisted per-realization <c>ChiSquared</c> (and the summary's convergence aggregates)
    /// describe the <b>warm-up</b> pass — the importance grid's adaptation consistency — while
    /// the values here describe the <b>recording</b> passes that produced the published curves,
    /// which the integrator recomputes after its accumulators reset and the engine previously
    /// discarded. The chi-squared is the integrator's per-degree-of-freedom pass-to-pass
    /// consistency statistic (values near one indicate consistent passes); the relative standard
    /// error is the recording standard error over the recorded integral magnitude. The mean-pass
    /// slots populate on a mean-only run — the full-uncertainty pass assembles its published
    /// mean from the ensemble percentiles and integrates no separate mean pass, so those slots
    /// are NaN there while the ensemble aggregates carry the run.
    /// </para>
    /// </remarks>
    public sealed class JointConvergenceCertificate
    {
        /// <summary>
        /// Initializes the certificate.
        /// </summary>
        /// <param name="realizationCount">The ensemble passes recorded (0 for a mean-only run).</param>
        /// <param name="meanPassRecordingChiSquared">The mean pass's recording-pass chi-squared per degree of freedom.</param>
        /// <param name="meanPassRelativeStandardError">The mean pass's recording standard error over its integral magnitude.</param>
        /// <param name="meanRecordingChiSquared">The ensemble mean of the recording-pass chi-squared values.</param>
        /// <param name="maxRecordingChiSquared">The ensemble maximum of the recording-pass chi-squared values.</param>
        /// <param name="meanRelativeStandardError">The ensemble mean of the recording relative standard errors.</param>
        /// <param name="maxRelativeStandardError">The ensemble maximum of the recording relative standard errors.</param>
        internal JointConvergenceCertificate(int realizationCount,
            double meanPassRecordingChiSquared, double meanPassRelativeStandardError,
            double meanRecordingChiSquared, double maxRecordingChiSquared,
            double meanRelativeStandardError, double maxRelativeStandardError)
        {
            RealizationCount = realizationCount;
            MeanPassRecordingChiSquared = meanPassRecordingChiSquared;
            MeanPassRelativeStandardError = meanPassRelativeStandardError;
            MeanRecordingChiSquared = meanRecordingChiSquared;
            MaxRecordingChiSquared = maxRecordingChiSquared;
            MeanRelativeStandardError = meanRelativeStandardError;
            MaxRelativeStandardError = maxRelativeStandardError;
        }

        /// <summary>
        /// The ensemble passes recorded (0 for a mean-only run; the ensemble aggregates are NaN
        /// then).
        /// </summary>
        public int RealizationCount { get; }

        /// <summary>
        /// The mean pass's recording-pass chi-squared per degree of freedom (NaN on a
        /// full-uncertainty run, which integrates no separate mean pass).
        /// </summary>
        public double MeanPassRecordingChiSquared { get; }

        /// <summary>
        /// The mean pass's recording standard error over its recorded integral magnitude (NaN on
        /// a full-uncertainty run).
        /// </summary>
        public double MeanPassRelativeStandardError { get; }

        /// <summary>
        /// The ensemble mean of the recording-pass chi-squared values.
        /// </summary>
        public double MeanRecordingChiSquared { get; }

        /// <summary>
        /// The ensemble maximum of the recording-pass chi-squared values.
        /// </summary>
        public double MaxRecordingChiSquared { get; }

        /// <summary>
        /// The ensemble mean of the recording relative standard errors.
        /// </summary>
        public double MeanRelativeStandardError { get; }

        /// <summary>
        /// The ensemble maximum of the recording relative standard errors.
        /// </summary>
        public double MaxRelativeStandardError { get; }
    }
}
