using System;
using System.Xml.Linq;
using Numerics;
using RMC.TotalRisk.Core;

namespace RMC.TotalRisk.Analyses.CostBenefit
{
    /// <summary>
    /// One per-type monetization factor: the declared consequence-type position it prices, the
    /// monetary amount per unit of that type, and the label and vintage documenting where the
    /// price came from.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The factor is immutable (replace it to edit). No price ships as a default — every
    /// supplied value carries its vintage into the results echo so a reviewer can see what
    /// price level produced a monetized number. Bounds against the declared consequence-type
    /// axis, and the refusal of factors on identity-monetized types, are validated at the
    /// study level, where the type declarations live. Element and attribute names are
    /// append-only serialized contract.
    /// </para>
    /// </remarks>
    public sealed class MonetizationFactor
    {
        #region Construction

        /// <summary>
        /// Initializes a monetization factor.
        /// </summary>
        /// <param name="consequenceType">The consequence-type position priced (0 = the primary type).</param>
        /// <param name="amountPerUnit">The monetary amount per unit of the type (finite and positive).</param>
        /// <param name="label">The display label; null reads as empty.</param>
        /// <param name="vintage">The price-level vintage (for example a year and source); null reads as empty.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown for a negative type position or a non-finite or non-positive amount.
        /// </exception>
        public MonetizationFactor(int consequenceType, double amountPerUnit, string? label = null,
            string? vintage = null)
        {
            if (consequenceType < 0)
                throw new ArgumentOutOfRangeException(nameof(consequenceType),
                    "The consequence-type position must not be negative.");
            if (!Tools.IsFinite(amountPerUnit) || amountPerUnit <= 0d)
                throw new ArgumentOutOfRangeException(nameof(amountPerUnit),
                    "The amount per unit must be finite and positive.");
            ConsequenceType = consequenceType;
            AmountPerUnit = amountPerUnit;
            Label = label ?? string.Empty;
            Vintage = vintage ?? string.Empty;
        }

        /// <summary>
        /// Restores a factor from its serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the stored values violate the construction guards.</exception>
        public MonetizationFactor(XElement xElement)
            : this(SerializationUtilities.ReadInt32(SerializationUtilities.RequireElement(xElement, nameof(xElement)), nameof(ConsequenceType)),
                SerializationUtilities.ReadDouble(xElement, nameof(AmountPerUnit), 1d),
                SerializationUtilities.ReadString(xElement, nameof(Label)),
                SerializationUtilities.ReadString(xElement, nameof(Vintage)))
        {
        }

        #endregion

        #region Members

        /// <summary>
        /// The consequence-type position priced (0 = the primary type).
        /// </summary>
        public int ConsequenceType { get; }

        /// <summary>
        /// The monetary amount per unit of the type.
        /// </summary>
        public double AmountPerUnit { get; }

        /// <summary>
        /// The display label (empty when none was given).
        /// </summary>
        public string Label { get; }

        /// <summary>
        /// The price-level vintage echoed into the results (empty when none was given).
        /// </summary>
        public string Vintage { get; }

        #endregion

        #region Serialization

        /// <summary>
        /// Serializes the factor. Element and attribute names are append-only contract.
        /// </summary>
        /// <returns>The serialized form.</returns>
        public XElement ToXElement()
        {
            var element = new XElement(nameof(MonetizationFactor));
            element.SetAttributeValue(nameof(ConsequenceType), ConsequenceType);
            element.SetAttributeValue(nameof(AmountPerUnit), SerializationUtilities.FormatDouble(AmountPerUnit));
            element.SetAttributeValue(nameof(Label), Label);
            element.SetAttributeValue(nameof(Vintage), Vintage);
            return element;
        }

        #endregion
    }
}
