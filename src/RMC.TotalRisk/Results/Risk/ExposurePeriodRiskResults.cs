using System;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// Exposure-period and life-cycle risk conversions over the stored epistemic ensemble: the
    /// probability of at least one failure over the period, and the cumulative, discounted, and
    /// equivalent-annual expected consequences.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// A plain query result — never serialized — computed per realization from the stored annual
    /// measures and reduced with the stored realization weights. The period failure probability
    /// uses the binomial (independent annual trials) convention P_T = 1 − (1 − p)^T, exact for
    /// the engine's annualized failure probability; the Poisson rate form 1 − exp(−λT), which
    /// applies when the annual value is interpreted as a rate, agrees to a relative difference
    /// of approximately p·(T − 1)/2 — about 0.25% at an annual 10⁻⁴ over 50 years and shrinking
    /// proportionally below. The consequence conversions treat the selected stream's expected
    /// annual consequence m as a level annual amount: the cumulative expectation is T·m, the
    /// present value is m·(1 − (1 + r)^−T)/r at discount rate r (T·m at r = 0), and the
    /// equivalent annual value — the level annual amount whose present value matches — is the
    /// annuity round trip of that present value, which for a stationary analysis is m itself
    /// (the identity is deliberate; the year-interpolated base-versus-future composition lives
    /// in the future cost-benefit layer, whose v1.0 reference is <c>PlanRow.EquivalentAnnual</c>).
    /// </para>
    /// <para>
    /// Every conversion assumes stationarity and inter-year independence — the same annual
    /// distribution every year of the period, with no deterioration, climate trend, or
    /// intervention. That caveat is structural: a genuine time axis is the life-cycle
    /// capability's motivation, not a post-processing conversion.
    /// </para>
    /// </remarks>
    public sealed class ExposurePeriodRiskResults
    {
        /// <summary>
        /// Initializes the query result.
        /// </summary>
        /// <param name="outputLabel">The human-readable scope label.</param>
        /// <param name="periodYears">The exposure period in years.</param>
        /// <param name="discountRate">The annual discount rate (0 for undiscounted).</param>
        /// <param name="consequenceTypeIndex">The consequence-type position (0 = primary).</param>
        /// <param name="validRealizations">The realizations surviving pairwise filtering.</param>
        /// <param name="periodFailureProbability">The P_T reduction.</param>
        /// <param name="cumulativeExpectedConsequence">The T·m reduction.</param>
        /// <param name="presentValueOfExpectedConsequences">The discounted-sum reduction.</param>
        /// <param name="equivalentAnnualConsequence">The annuity round-trip reduction.</param>
        /// <exception cref="ArgumentNullException">Thrown when any interval is null.</exception>
        public ExposurePeriodRiskResults(string outputLabel, int periodYears, double discountRate,
            int consequenceTypeIndex, int validRealizations,
            ExposurePeriodInterval periodFailureProbability,
            ExposurePeriodInterval cumulativeExpectedConsequence,
            ExposurePeriodInterval presentValueOfExpectedConsequences,
            ExposurePeriodInterval equivalentAnnualConsequence)
        {
            OutputLabel = outputLabel;
            PeriodYears = periodYears;
            DiscountRate = discountRate;
            ConsequenceTypeIndex = consequenceTypeIndex;
            ValidRealizations = validRealizations;
            PeriodFailureProbability = periodFailureProbability ?? throw new ArgumentNullException(nameof(periodFailureProbability));
            CumulativeExpectedConsequence = cumulativeExpectedConsequence ?? throw new ArgumentNullException(nameof(cumulativeExpectedConsequence));
            PresentValueOfExpectedConsequences = presentValueOfExpectedConsequences ?? throw new ArgumentNullException(nameof(presentValueOfExpectedConsequences));
            EquivalentAnnualConsequence = equivalentAnnualConsequence ?? throw new ArgumentNullException(nameof(equivalentAnnualConsequence));
        }

        /// <summary>
        /// The human-readable scope label ("stream — scope").
        /// </summary>
        public string OutputLabel { get; }

        /// <summary>
        /// The exposure period in years.
        /// </summary>
        public int PeriodYears { get; }

        /// <summary>
        /// The annual discount rate (0 = undiscounted).
        /// </summary>
        public double DiscountRate { get; }

        /// <summary>
        /// The consequence-type position the consequence conversions read (0 = primary).
        /// </summary>
        public int ConsequenceTypeIndex { get; }

        /// <summary>
        /// The realizations surviving pairwise NaN filtering.
        /// </summary>
        public int ValidRealizations { get; }

        /// <summary>
        /// The probability of at least one failure over the period,
        /// P_T = 1 − (1 − p)^T of the scope's annualized failure probability — reduced over the
        /// weighted ensemble (the mean is the expected P_T, not P_T of the expected p).
        /// </summary>
        public ExposurePeriodInterval PeriodFailureProbability { get; }

        /// <summary>
        /// The cumulative (undiscounted) expected consequence over the period, T·m.
        /// </summary>
        public ExposurePeriodInterval CumulativeExpectedConsequence { get; }

        /// <summary>
        /// The present value of the period's expected consequences at the discount rate.
        /// </summary>
        public ExposurePeriodInterval PresentValueOfExpectedConsequences { get; }

        /// <summary>
        /// The equivalent annual consequence — the level annual amount whose present value over
        /// the period matches; the annuity round trip that reproduces the annual expected
        /// consequence for a stationary analysis.
        /// </summary>
        public ExposurePeriodInterval EquivalentAnnualConsequence { get; }
    }
}
