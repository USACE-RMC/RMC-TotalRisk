using System;
using System.Xml.Linq;
using Numerics;
using RMC.TotalRisk.Core;

namespace RMC.TotalRisk.Analyses.CostBenefit
{
    /// <summary>
    /// One recurring annual amount in a cost stream: a signed annual amount accruing over the
    /// exposure years after its start, optionally ending before the study horizon.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The segment is immutable (replace it to edit). It accrues over the exposure years
    /// (startYear, endYear] — the first accrual falls one year after the start and discounts
    /// by (1 + r)^−(startYear + 1) — so its present value is the annual amount times the
    /// annuity-factor difference A(endYear) − A(startYear), exactly the life-cycle
    /// trajectory's per-epoch segments. A null end runs to the study horizon. Signed amounts
    /// are legal: a negative segment is a recurring saving. Year bounds against the study
    /// horizon are validated at the study level. Element and attribute names are append-only
    /// serialized contract; the end year is written only when bounded.
    /// </para>
    /// </remarks>
    public sealed class RecurringCostSegment
    {
        #region Construction

        /// <summary>
        /// Initializes a recurring annual amount.
        /// </summary>
        /// <param name="startYear">The year the accrual starts after (0 = now; non-negative).</param>
        /// <param name="annualAmount">The signed annual amount (finite; negative = a saving).</param>
        /// <param name="endYear">The last accruing year, or null to run to the study horizon.</param>
        /// <param name="label">The display label; null reads as empty.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown for a negative start year, a non-finite amount, or an end year at or before
        /// the start.
        /// </exception>
        public RecurringCostSegment(int startYear, double annualAmount, int? endYear = null, string? label = null)
        {
            if (startYear < 0)
                throw new ArgumentOutOfRangeException(nameof(startYear), "The segment start year must not be negative.");
            if (!Tools.IsFinite(annualAmount))
                throw new ArgumentOutOfRangeException(nameof(annualAmount), "The annual amount must be finite.");
            if (endYear.HasValue && endYear.Value <= startYear)
                throw new ArgumentOutOfRangeException(nameof(endYear),
                    "The segment accrues over (startYear, endYear], so the end year must exceed the start year.");
            StartYear = startYear;
            AnnualAmount = annualAmount;
            EndYear = endYear;
            Label = label ?? string.Empty;
        }

        /// <summary>
        /// Restores a segment from its serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the stored values violate the construction guards.</exception>
        public RecurringCostSegment(XElement xElement)
            : this(SerializationUtilities.ReadInt32(SerializationUtilities.RequireElement(xElement, nameof(xElement)), nameof(StartYear)),
                SerializationUtilities.ReadDouble(xElement, nameof(AnnualAmount)),
                ReadEndYear(xElement),
                SerializationUtilities.ReadString(xElement, nameof(Label)))
        {
        }

        /// <summary>
        /// Reads the optional end year: absent means the segment runs to the study horizon.
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <returns>The bounded end year, or null.</returns>
        private static int? ReadEndYear(XElement xElement)
        {
            return xElement.Attribute(nameof(EndYear)) == null
                ? null
                : SerializationUtilities.ReadInt32(xElement, nameof(EndYear));
        }

        #endregion

        #region Members

        /// <summary>
        /// The year the accrual starts after (0 = now); the first accruing exposure year is
        /// the one following it.
        /// </summary>
        public int StartYear { get; }

        /// <summary>
        /// The signed annual amount; a negative segment is a recurring saving.
        /// </summary>
        public double AnnualAmount { get; }

        /// <summary>
        /// The last accruing year, or null to run to the study horizon.
        /// </summary>
        public int? EndYear { get; }

        /// <summary>
        /// The display label (empty when none was given).
        /// </summary>
        public string Label { get; }

        #endregion

        #region Serialization

        /// <summary>
        /// Serializes the segment. Element and attribute names are append-only contract; the
        /// end year is written only when bounded.
        /// </summary>
        /// <returns>The serialized form.</returns>
        public XElement ToXElement()
        {
            var element = new XElement(nameof(RecurringCostSegment));
            element.SetAttributeValue(nameof(StartYear), StartYear);
            element.SetAttributeValue(nameof(AnnualAmount), SerializationUtilities.FormatDouble(AnnualAmount));
            if (EndYear.HasValue)
            {
                element.SetAttributeValue(nameof(EndYear), EndYear.Value);
            }
            element.SetAttributeValue(nameof(Label), Label);
            return element;
        }

        #endregion
    }
}
