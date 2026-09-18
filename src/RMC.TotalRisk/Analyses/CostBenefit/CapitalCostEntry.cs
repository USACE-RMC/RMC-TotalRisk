using System;
using System.Xml.Linq;
using Numerics;
using RMC.TotalRisk.Core;

namespace RMC.TotalRisk.Analyses.CostBenefit
{
    /// <summary>
    /// One dated capital amount in a cost stream: the year it falls in, the signed amount, and
    /// a display label.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The entry is immutable (replace it to edit — the declared-consequence-type idiom).
    /// A year-zero amount is undiscounted, and a year-k amount discounts by (1 + r)^−k —
    /// term-for-term consistent with the life-cycle trajectory, whose first exposure year
    /// discounts one full period. Signed amounts are legal: a negative entry is a credit
    /// (salvage or residual value). The year's bound against the study horizon is validated
    /// at the study level, where the horizon lives. Element and attribute names are
    /// append-only serialized contract.
    /// </para>
    /// </remarks>
    public sealed class CapitalCostEntry
    {
        #region Construction

        /// <summary>
        /// Initializes a dated capital amount.
        /// </summary>
        /// <param name="year">The year the amount falls in (0 = now; non-negative).</param>
        /// <param name="amount">The signed amount (finite; negative = a credit).</param>
        /// <param name="label">The display label; null reads as empty.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown for a negative year or a non-finite amount.</exception>
        public CapitalCostEntry(int year, double amount, string? label = null)
        {
            if (year < 0)
                throw new ArgumentOutOfRangeException(nameof(year), "The capital year must not be negative.");
            if (!Tools.IsFinite(amount))
                throw new ArgumentOutOfRangeException(nameof(amount), "The capital amount must be finite.");
            Year = year;
            Amount = amount;
            Label = label ?? string.Empty;
        }

        /// <summary>
        /// Restores an entry from its serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the stored values violate the construction guards.</exception>
        public CapitalCostEntry(XElement xElement)
            : this(SerializationUtilities.ReadInt32(SerializationUtilities.RequireElement(xElement, nameof(xElement)), nameof(Year)),
                SerializationUtilities.ReadDouble(xElement, nameof(Amount)),
                SerializationUtilities.ReadString(xElement, nameof(Label)))
        {
        }

        #endregion

        #region Members

        /// <summary>
        /// The year the amount falls in (0 = now).
        /// </summary>
        public int Year { get; }

        /// <summary>
        /// The signed amount; a negative entry is a credit.
        /// </summary>
        public double Amount { get; }

        /// <summary>
        /// The display label (empty when none was given).
        /// </summary>
        public string Label { get; }

        #endregion

        #region Serialization

        /// <summary>
        /// Serializes the entry. Element and attribute names are append-only contract.
        /// </summary>
        /// <returns>The serialized form.</returns>
        public XElement ToXElement()
        {
            var element = new XElement(nameof(CapitalCostEntry));
            element.SetAttributeValue(nameof(Year), Year);
            element.SetAttributeValue(nameof(Amount), SerializationUtilities.FormatDouble(Amount));
            element.SetAttributeValue(nameof(Label), Label);
            return element;
        }

        #endregion
    }
}
