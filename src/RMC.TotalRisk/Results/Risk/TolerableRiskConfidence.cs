namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// One evaluated tolerable-risk confidence statement: the criterion echo (measure, stream,
    /// consequence-type position, threshold) and the epistemic exceedance probability — the
    /// fraction of realization weight whose measure strictly exceeds the threshold.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Entries ride <c>EnsembleSummary.TolerableRiskConfidence</c>, one per configured
    /// criterion in declared order — append-only results JSON, absent when no criteria are
    /// configured. The measure and stream echo by enum name so the statement is
    /// self-describing without the analysis configuration. A criterion whose surviving
    /// (non-NaN) realizations carry zero total weight reports NaN, the all-NaN convention.
    /// </para>
    /// </remarks>
    public sealed class TolerableRiskConfidence
    {
        /// <summary>
        /// The scalar risk measure's enum name (e.g., "Mean").
        /// </summary>
        public string Measure { get; set; } = string.Empty;

        /// <summary>
        /// The system stream's enum name (e.g., "Excess").
        /// </summary>
        public string RiskType { get; set; } = string.Empty;

        /// <summary>
        /// The consequence-type position the measure read (0 = the primary type).
        /// </summary>
        public int ConsequenceTypeIndex { get; set; }

        /// <summary>
        /// The tolerable-risk threshold the ensemble was compared against.
        /// </summary>
        public double Threshold { get; set; }

        /// <summary>
        /// The epistemic confidence statement P(measure &gt; threshold): the realization-weight
        /// fraction strictly exceeding the threshold. NaN when no surviving realization carries
        /// weight for the measure.
        /// </summary>
        public double ExceedanceProbability { get; set; }
    }
}
