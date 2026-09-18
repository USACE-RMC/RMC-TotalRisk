using System;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Results.CostBenefit
{
    /// <summary>
    /// One consequence-reduction row of a cost-benefit study: an alternative's signed
    /// reduction vs the designated baseline for one consequence type on one stream, in both
    /// accounting conventions, with the monetized present-value reduction when the type is
    /// priced.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// A presentation-contract row. Reductions are signed baseline − alternative (positive is
    /// good) and never clamped; unprefixed members carry the non-absorbing convention and the
    /// Absorbing twins the first-failure-terminates one. The monetized reduction is NaN when
    /// the type carries no price.
    /// </para>
    /// </remarks>
    public sealed class ConsequenceReduction
    {
        /// <summary>
        /// Initializes a consequence-reduction row.
        /// </summary>
        /// <param name="alternativeName">The alternative's display name.</param>
        /// <param name="consequenceType">The consequence-type position (0 = the primary type).</param>
        /// <param name="stream">The consequence stream the row reads.</param>
        /// <param name="label">The type's declared label.</param>
        /// <param name="unit">The type's declared unit.</param>
        /// <param name="baselinePresentValue">The baseline's present-value expected consequences (non-absorbing).</param>
        /// <param name="alternativePresentValue">The alternative's present-value expected consequences (non-absorbing).</param>
        /// <param name="presentValueReduction">The signed present-value reduction (non-absorbing).</param>
        /// <param name="equivalentAnnualReduction">The signed equivalent-annual reduction (non-absorbing).</param>
        /// <param name="cumulativeReduction">The signed undiscounted cumulative reduction (non-absorbing).</param>
        /// <param name="absorbingPresentValueReduction">The signed present-value reduction (absorbing).</param>
        /// <param name="absorbingCumulativeReduction">The signed cumulative reduction (absorbing).</param>
        /// <param name="monetizedPresentValueReduction">The priced present-value reduction; NaN when the type is unpriced.</param>
        /// <exception cref="ArgumentNullException">Thrown when a name, label, or unit is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown for a negative consequence-type position.</exception>
        public ConsequenceReduction(string alternativeName, int consequenceType, RiskType stream,
            string label, string unit,
            double baselinePresentValue, double alternativePresentValue,
            double presentValueReduction, double equivalentAnnualReduction, double cumulativeReduction,
            double absorbingPresentValueReduction, double absorbingCumulativeReduction,
            double monetizedPresentValueReduction)
        {
            AlternativeName = alternativeName ?? throw new ArgumentNullException(nameof(alternativeName));
            if (consequenceType < 0)
                throw new ArgumentOutOfRangeException(nameof(consequenceType),
                    "The consequence-type position must not be negative.");
            ConsequenceType = consequenceType;
            Stream = stream;
            Label = label ?? throw new ArgumentNullException(nameof(label));
            Unit = unit ?? throw new ArgumentNullException(nameof(unit));
            BaselinePresentValue = baselinePresentValue;
            AlternativePresentValue = alternativePresentValue;
            PresentValueReduction = presentValueReduction;
            EquivalentAnnualReduction = equivalentAnnualReduction;
            CumulativeReduction = cumulativeReduction;
            AbsorbingPresentValueReduction = absorbingPresentValueReduction;
            AbsorbingCumulativeReduction = absorbingCumulativeReduction;
            MonetizedPresentValueReduction = monetizedPresentValueReduction;
        }

        /// <summary>The alternative's display name.</summary>
        public string AlternativeName { get; }

        /// <summary>The consequence-type position (0 = the primary type).</summary>
        public int ConsequenceType { get; }

        /// <summary>The consequence stream the row reads.</summary>
        public RiskType Stream { get; }

        /// <summary>The type's declared label.</summary>
        public string Label { get; }

        /// <summary>The type's declared unit.</summary>
        public string Unit { get; }

        /// <summary>The baseline's present-value expected consequences (non-absorbing).</summary>
        public double BaselinePresentValue { get; }

        /// <summary>The alternative's present-value expected consequences (non-absorbing).</summary>
        public double AlternativePresentValue { get; }

        /// <summary>The signed present-value reduction, baseline − alternative (non-absorbing).</summary>
        public double PresentValueReduction { get; }

        /// <summary>The signed equivalent-annual reduction (non-absorbing).</summary>
        public double EquivalentAnnualReduction { get; }

        /// <summary>The signed undiscounted cumulative reduction (non-absorbing).</summary>
        public double CumulativeReduction { get; }

        /// <summary>The signed present-value reduction (absorbing).</summary>
        public double AbsorbingPresentValueReduction { get; }

        /// <summary>The signed cumulative reduction (absorbing).</summary>
        public double AbsorbingCumulativeReduction { get; }

        /// <summary>The priced present-value reduction; NaN when the type is unpriced.</summary>
        public double MonetizedPresentValueReduction { get; }
    }
}
