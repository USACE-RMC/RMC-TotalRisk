using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using RMC.TotalRisk.Core;

namespace RMC.TotalRisk.Analyses.CostBenefit
{
    /// <summary>
    /// The study's monetization map: the monetary unit every price is expressed in, and the
    /// per-type factors converting declared consequence types into that unit.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The map is immutable (replace it to edit). A declared type whose unit equals the
    /// monetary unit is identity-monetized at factor one — one price level — so supplying a
    /// factor for it is refused at the study level, where the type declarations live.
    /// Monetization is opt-in: an empty map makes the monetary aggregates undefined (NaN)
    /// rather than asserting that no benefits exist. Element and child names are append-only
    /// serialized contract.
    /// </para>
    /// </remarks>
    public sealed class ConsequenceMonetization
    {
        #region Construction

        /// <summary>
        /// Initializes a monetization map.
        /// </summary>
        /// <param name="factors">The per-type factors; null or empty for none.</param>
        /// <param name="monetaryUnit">The monetary unit every price is expressed in (non-blank).</param>
        /// <exception cref="ArgumentException">
        /// Thrown when the factor list contains a null entry or two factors price one type
        /// position, or when the monetary unit is blank.
        /// </exception>
        public ConsequenceMonetization(IReadOnlyList<MonetizationFactor>? factors = null,
            string monetaryUnit = "$")
        {
            if (string.IsNullOrWhiteSpace(monetaryUnit))
                throw new ArgumentException("The monetary unit must not be blank.", nameof(monetaryUnit));
            var snapshot = factors == null ? Array.Empty<MonetizationFactor>() : factors.ToArray();
            var seenTypes = new HashSet<int>();
            for (int i = 0; i < snapshot.Length; i++)
            {
                var factor = snapshot[i]
                    ?? throw new ArgumentException("The factor list contains a null entry.", nameof(factors));
                if (!seenTypes.Add(factor.ConsequenceType))
                    throw new ArgumentException(
                        $"Two factors price consequence-type position {factor.ConsequenceType}; one price level per type.",
                        nameof(factors));
            }
            Factors = Array.AsReadOnly(snapshot);
            MonetaryUnit = monetaryUnit;
        }

        /// <summary>
        /// Restores a monetization map from its serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        /// <exception cref="ArgumentException">Thrown when the stored values violate the construction guards.</exception>
        public ConsequenceMonetization(XElement xElement)
            : this(ReadFactors(SerializationUtilities.RequireElement(xElement, nameof(xElement))),
                SerializationUtilities.ReadString(xElement, nameof(MonetaryUnit), "$"))
        {
        }

        /// <summary>
        /// Reads the per-type factors from the serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <returns>The factors.</returns>
        private static IReadOnlyList<MonetizationFactor> ReadFactors(XElement xElement)
        {
            var factors = new List<MonetizationFactor>();
            foreach (XElement child in xElement.Elements(nameof(MonetizationFactor)))
            {
                factors.Add(new MonetizationFactor(child));
            }
            return factors;
        }

        #endregion

        #region Members

        /// <summary>
        /// The per-type factors, one per priced consequence-type position.
        /// </summary>
        public IReadOnlyList<MonetizationFactor> Factors { get; }

        /// <summary>
        /// The monetary unit every price is expressed in.
        /// </summary>
        public string MonetaryUnit { get; }

        #endregion

        #region Serialization

        /// <summary>
        /// Serializes the map. Element and child names are append-only contract.
        /// </summary>
        /// <returns>The serialized form.</returns>
        public XElement ToXElement()
        {
            var element = new XElement(nameof(ConsequenceMonetization));
            element.SetAttributeValue(nameof(MonetaryUnit), MonetaryUnit);
            for (int i = 0; i < Factors.Count; i++)
            {
                element.Add(Factors[i].ToXElement());
            }
            return element;
        }

        #endregion
    }
}
