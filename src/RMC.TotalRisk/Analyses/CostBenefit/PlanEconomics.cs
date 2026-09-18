using System;
using Numerics;

namespace RMC.TotalRisk.Analyses.CostBenefit
{
    /// <summary>
    /// The v1.0 plan-comparison economics: the equivalent-annual consequence of one condition
    /// described at two points in time — a base value ramping linearly to a most-likely-future
    /// value and holding flat — discounted end-of-year over the period of analysis.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The computation is the v1.0 reference (<c>PlanRow.EquivalentAnnual</c>, the HEC-FDA
    /// equivalent-annual procedure) preserved exactly for positive rates: exactly n
    /// end-of-year terms with the base year discounted one full period (the year-b value
    /// carries (1 + r)^−1), a linear ramp from (baseYear, baseValue) to (futureYear,
    /// futureValue), then a plateau at the future value, and the capital recovery factor
    /// r(1 + r)ⁿ/((1 + r)ⁿ − 1) applied to the total present value. Two v1.0 behaviors are
    /// preserved deliberately and must be understood by callers: a future year beyond the
    /// end of the period silently truncates the ramp (the plateau is never reached and no
    /// diagnostic is raised), and a future year at or before the base year makes the whole
    /// stream the future value. A zero rate returns the exact limit — the plain average of
    /// the n year values — a documented deliberate improvement over v1.0, whose capital
    /// recovery factor evaluated an indeterminate form there; the same limit convention the
    /// engine's annuity factor takes.
    /// </para>
    /// <para>
    /// The technical-report draft of this procedure (its Appendix H, Equations 252–256)
    /// differs from the shipped v1.0 code on the discounting index: the document leaves the
    /// base year undiscounted and its bounds read as n + 1 terms, while the code discounts
    /// the base year one full period over exactly n terms. This port anchors to the code —
    /// the reference-results reading of the porting rule — and records the document
    /// discrepancy here; a convention selector is the recorded seat if the document's
    /// reading is ever needed.
    /// </para>
    /// </remarks>
    public static class PlanEconomics
    {
        /// <summary>
        /// Computes the equivalent-annual consequence of a base-to-future ramp-and-plateau
        /// stream over the period of analysis.
        /// </summary>
        /// <param name="baseEac">The expected annual consequence at the base year.</param>
        /// <param name="baseYear">The base year (the stream's first exposure year is the one after it).</param>
        /// <param name="futureEac">The most-likely-future expected annual consequence.</param>
        /// <param name="futureYear">The year the future value is reached; at or before the base year, the whole stream holds the future value.</param>
        /// <param name="discountRate">The annual discount rate (0 = the exact undiscounted limit).</param>
        /// <param name="periodYears">The period of analysis in years (at least one).</param>
        /// <returns>The equivalent-annual consequence.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown for a negative or non-finite discount rate, or a period below one year.
        /// </exception>
        public static double EquivalentAnnualConsequences(double baseEac, int baseYear,
            double futureEac, int futureYear, double discountRate, int periodYears)
        {
            if (!Tools.IsFinite(discountRate) || discountRate < 0d)
                throw new ArgumentOutOfRangeException(nameof(discountRate),
                    "The discount rate must be finite and non-negative.");
            if (periodYears < 1)
                throw new ArgumentOutOfRangeException(nameof(periodYears),
                    "The period of analysis must be at least one year.");

            // The v1.0 stream: year starts at the base year while the discount index starts
            // at one, so the base year's value discounts one full period and the stream has
            // exactly n terms.
            double totalPresentValue = 0d;
            double totalValue = 0d;
            int year = baseYear;
            for (int i = 1; i <= periodYears; i++)
            {
                double value = year >= futureYear
                    ? futureEac
                    : baseEac + (year - baseYear) / (double)(futureYear - baseYear) * (futureEac - baseEac);
                totalPresentValue += value * Math.Pow(1d + discountRate, -i);
                totalValue += value;
                year += 1;
            }

            if (discountRate == 0d)
            {
                // The exact r → 0 limit of CRF·TPV: the plain average of the year values.
                return totalValue / periodYears;
            }

            double capitalRecoveryFactor = discountRate * Math.Pow(1d + discountRate, periodYears)
                / (Math.Pow(1d + discountRate, periodYears) - 1d);
            return capitalRecoveryFactor * totalPresentValue;
        }
    }
}
