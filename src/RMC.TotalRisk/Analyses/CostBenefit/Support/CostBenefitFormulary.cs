using System;
using System.Collections.Generic;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Analyses.CostBenefit.Support
{
    /// <summary>
    /// The cost-effectiveness formulary: total expected annual cost, the cost-per-statistical-
    /// life-saved family, cost per statistical failure prevented, the disproportionality ratio,
    /// and the ALARP justification bands, as pure scalar algebra over already-priced study
    /// quantities.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The unadjusted and adjusted cost per statistical life saved follow USACE
    /// ER 1110-2-1156 Appendix L exactly, including the negative-numerator-to-zero proviso on
    /// the adjusted form; the equity-weighted form follows Serrano-Lombillo et al. (2016) with
    /// the individual-risk floor; the absorbing-basis adjusted form composes
    /// Fluixá-Sanmartín et al. (2020) on the study's first-failure-terminates aggregates. Ratio
    /// discipline: every ratio takes numerator and denominator from one accounting convention
    /// and one annualization basis, which makes the family basis-invariant — the present-value
    /// and equivalent-annual quotients are identical because the horizon annuity cancels and
    /// the zero clamp commutes with positive scaling. Any ratio with a non-positive denominator
    /// is NaN (cost per unit of nothing saved is undefined — never negative, never clamped);
    /// the zero clamp applies to the adjusted numerators only.
    /// </para>
    /// <para>
    /// Two documented readings where sources are silent or ambiguous: the absorbing adjusted
    /// form's cost base is the capital plus operations-and-maintenance present value — the
    /// operating-change stream is accounted through its own reduction term, and folding it into
    /// the cost base as well would count it twice — and its numerator carries the Appendix L
    /// zero clamp for family consistency, a choice its source does not state.
    /// </para>
    /// </remarks>
    internal static class CostBenefitFormulary
    {
        /// <summary>
        /// The total expected annual cost: the equivalent annual cost plus the monetized
        /// equivalent-annual expected consequences of the study's benefit stream. Minimizing
        /// it is equivalent to maximizing net benefits on a fixed alternative set with shared
        /// monetization, because both differ from the monetized equivalent-annual total by a
        /// constant.
        /// </summary>
        /// <param name="equivalentAnnualCost">The equivalent annual cost.</param>
        /// <param name="monetizedEquivalentAnnualConsequences">The monetized equivalent-annual expected consequences (NaN when no types are monetized).</param>
        /// <returns>The total expected annual cost; NaN when no types are monetized.</returns>
        internal static double TotalExpectedAnnualCost(double equivalentAnnualCost,
            double monetizedEquivalentAnnualConsequences)
        {
            return equivalentAnnualCost + monetizedEquivalentAnnualConsequences;
        }

        /// <summary>
        /// The unadjusted cost per statistical life saved: the annualized implementation cost
        /// over the equivalent-annual life-loss reduction.
        /// </summary>
        /// <param name="annualizedCost">The annualized implementation cost (capital plus operations and maintenance).</param>
        /// <param name="livesSavedEquivalentAnnual">The equivalent-annual life-loss reduction.</param>
        /// <returns>The ratio; NaN when the life-loss reduction is not positive or not available.</returns>
        internal static double CostPerLifeSavedUnadjusted(double annualizedCost, double livesSavedEquivalentAnnual)
        {
            if (!(livesSavedEquivalentAnnual > 0d)) return double.NaN;
            return annualizedCost / livesSavedEquivalentAnnual;
        }

        /// <summary>
        /// The adjusted cost per statistical life saved: the annualized implementation cost net
        /// of the economic risk reduction and the operating-cost reduction, clamped at zero,
        /// over the equivalent-annual life-loss reduction. A measure that adds operating cost
        /// carries a negative operating-cost reduction and therefore a larger numerator.
        /// </summary>
        /// <param name="annualizedCost">The annualized implementation cost (capital plus operations and maintenance).</param>
        /// <param name="economicBenefitEquivalentAnnual">The equivalent-annual economic risk reduction (the monetized aggregate excluding the life-safety type).</param>
        /// <param name="operatingReductionEquivalentAnnual">The equivalent-annual operating-cost reduction (baseline minus alternative).</param>
        /// <param name="livesSavedEquivalentAnnual">The equivalent-annual life-loss reduction.</param>
        /// <returns>The ratio; NaN when the life-loss reduction is not positive or a term is not available.</returns>
        internal static double CostPerLifeSavedAdjusted(double annualizedCost,
            double economicBenefitEquivalentAnnual, double operatingReductionEquivalentAnnual,
            double livesSavedEquivalentAnnual)
        {
            if (!(livesSavedEquivalentAnnual > 0d)) return double.NaN;
            double numerator = Math.Max(0d,
                annualizedCost - economicBenefitEquivalentAnnual - operatingReductionEquivalentAnnual);
            return numerator / livesSavedEquivalentAnnual;
        }

        /// <summary>
        /// The equity-weighted adjusted cost per statistical life saved: the adjusted ratio
        /// divided by the floored individual-risk ratio raised to the equity exponent. The
        /// floor keeps the weight bounded where either side's individual risk falls below the
        /// individual-risk limit; an exponent of zero recovers the adjusted ratio and larger
        /// exponents weight equity over efficiency.
        /// </summary>
        /// <param name="costPerLifeSavedAdjusted">The adjusted cost per statistical life saved.</param>
        /// <param name="baselineIndividualRisk">The baseline-side individual risk per year.</param>
        /// <param name="alternativeIndividualRisk">The alternative-side individual risk per year.</param>
        /// <param name="individualRiskLimit">The individual-risk floor.</param>
        /// <param name="equityExponent">The equity-versus-efficiency exponent.</param>
        /// <returns>The weighted ratio; NaN when a term is not available.</returns>
        internal static double EquityWeightedCostPerLifeSaved(double costPerLifeSavedAdjusted,
            double baselineIndividualRisk, double alternativeIndividualRisk,
            double individualRiskLimit, double equityExponent)
        {
            double weight = Math.Pow(
                Math.Max(baselineIndividualRisk, individualRiskLimit)
                    / Math.Max(alternativeIndividualRisk, individualRiskLimit),
                equityExponent);
            return costPerLifeSavedAdjusted / weight;
        }

        /// <summary>
        /// The cost per statistical failure prevented: the annualized implementation cost over
        /// the annualized failure-probability reduction.
        /// </summary>
        /// <param name="annualizedCost">The annualized implementation cost (capital plus operations and maintenance).</param>
        /// <param name="annualizedFailureProbabilityReduction">The annualized failure-probability reduction (baseline minus alternative).</param>
        /// <returns>The ratio; NaN when the probability reduction is not positive.</returns>
        internal static double CostPerFailurePrevented(double annualizedCost,
            double annualizedFailureProbabilityReduction)
        {
            if (!(annualizedFailureProbabilityReduction > 0d)) return double.NaN;
            return annualizedCost / annualizedFailureProbabilityReduction;
        }

        /// <summary>
        /// The absorbing-basis adjusted cost per statistical life saved: the capital plus
        /// operations-and-maintenance present value net of the operating-cost reduction and the
        /// absorbing economic risk reduction, clamped at zero, over the absorbing cumulative
        /// life-loss reduction.
        /// </summary>
        /// <param name="capitalAndOperationsPresentValue">The capital plus operations-and-maintenance present value.</param>
        /// <param name="operatingReductionPresentValue">The present-value operating-cost reduction (baseline minus alternative).</param>
        /// <param name="absorbingEconomicBenefitPresentValue">The absorbing economic present-value risk reduction.</param>
        /// <param name="absorbingLivesSavedCumulative">The absorbing cumulative life-loss reduction.</param>
        /// <returns>The ratio; NaN when the life-loss reduction is not positive or a term is not available.</returns>
        internal static double AbsorbingCostPerLifeSavedAdjusted(double capitalAndOperationsPresentValue,
            double operatingReductionPresentValue, double absorbingEconomicBenefitPresentValue,
            double absorbingLivesSavedCumulative)
        {
            if (!(absorbingLivesSavedCumulative > 0d)) return double.NaN;
            double numerator = Math.Max(0d,
                capitalAndOperationsPresentValue - operatingReductionPresentValue
                    - absorbingEconomicBenefitPresentValue);
            return numerator / absorbingLivesSavedCumulative;
        }

        /// <summary>
        /// The disproportionality ratio: the adjusted cost per statistical life saved over the
        /// willingness to pay per statistical life.
        /// </summary>
        /// <param name="costPerLifeSavedAdjusted">The adjusted cost per statistical life saved.</param>
        /// <param name="willingnessToPay">The willingness to pay per statistical life.</param>
        /// <returns>The ratio; NaN when the willingness to pay is not positive or not declared.</returns>
        internal static double DisproportionalityRatio(double costPerLifeSavedAdjusted, double willingnessToPay)
        {
            if (!(willingnessToPay > 0d)) return double.NaN;
            return costPerLifeSavedAdjusted / willingnessToPay;
        }

        /// <summary>
        /// The ALARP band thresholds for a tolerable-risk proximity, per ER 1110-2-1156
        /// Tables 5.1 and 5.2: {1, 4, 20} for risks just below the tolerable limit and
        /// {0.3, 1, 6} for risks just above the broadly acceptable level.
        /// </summary>
        /// <param name="proximity">The declared proximity.</param>
        /// <returns>The three ascending band thresholds.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined proximity.</exception>
        internal static IReadOnlyList<double> AlarpBandThresholds(AlarpProximity proximity)
        {
            return proximity switch
            {
                AlarpProximity.JustBelowTolerableLimit => new[] { 1d, 4d, 20d },
                AlarpProximity.JustAboveBroadlyAcceptable => new[] { 0.3d, 1d, 6d },
                _ => throw new ArgumentOutOfRangeException(nameof(proximity), proximity,
                    "The tolerable-risk proximity is not defined."),
            };
        }

        /// <summary>
        /// The ALARP justification-band label for a disproportionality ratio: Very Strong at or
        /// below the first threshold, then Strong, Moderate, and Poor above the third. A ratio
        /// exactly at a boundary reports the stronger band. A label, never a verdict — chart
        /// placement and process conclusions stay with the presentation layer.
        /// </summary>
        /// <param name="ratio">The disproportionality ratio.</param>
        /// <param name="thresholds">The three ascending band thresholds.</param>
        /// <returns>The band label, or an empty string when the ratio is NaN.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the thresholds are null.</exception>
        /// <exception cref="ArgumentException">Thrown when the thresholds are not exactly three values.</exception>
        internal static string AlarpBandLabel(double ratio, IReadOnlyList<double> thresholds)
        {
            if (thresholds == null) throw new ArgumentNullException(nameof(thresholds));
            if (thresholds.Count != 3)
                throw new ArgumentException("The ALARP band table requires exactly three thresholds.", nameof(thresholds));
            if (double.IsNaN(ratio)) return string.Empty;
            if (ratio <= thresholds[0]) return "Very Strong";
            if (ratio <= thresholds[1]) return "Strong";
            if (ratio <= thresholds[2]) return "Moderate";
            return "Poor";
        }
    }
}
