using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Numerics;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Analyses.CostBenefit
{
    /// <summary>
    /// The declaration of an expected-utility ranking: the utility-function family and its
    /// risk-aversion parameter.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The declaration is immutable (replace it to edit). The parameter is the exponential
    /// family's absolute risk aversion or the power family's relative risk aversion; rankings
    /// report the certainty equivalent so results stay in consequence units. Element and
    /// attribute names are append-only serialized contract.
    /// </para>
    /// </remarks>
    public sealed class UtilityDeclaration
    {
        #region Construction

        /// <summary>
        /// Initializes a utility declaration.
        /// </summary>
        /// <param name="form">The utility-function family.</param>
        /// <param name="riskAversion">The family's risk-aversion parameter (finite and positive).</param>
        public UtilityDeclaration(UtilityFunctionForm form, double riskAversion)
        {
            Form = form;
            RiskAversion = riskAversion;
        }

        /// <summary>
        /// Restores a declaration from its serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        public UtilityDeclaration(XElement xElement)
            : this(SerializationUtilities.ReadEnum(SerializationUtilities.RequireElement(xElement, nameof(xElement)), nameof(Form), UtilityFunctionForm.ExponentialCara),
                SerializationUtilities.ReadDouble(xElement, nameof(RiskAversion), 1d))
        {
        }

        #endregion

        #region Members

        /// <summary>
        /// The utility-function family.
        /// </summary>
        public UtilityFunctionForm Form { get; }

        /// <summary>
        /// The family's risk-aversion parameter.
        /// </summary>
        public double RiskAversion { get; }

        #endregion

        #region IModel Methods

        /// <summary>
        /// Validates the declaration: a recognized family and a finite, positive parameter.
        /// </summary>
        /// <returns>The validity flag and messages.</returns>
        public (bool IsValid, List<string> ValidationMessages) Validate()
        {
            var messages = new List<string>();
            if (!Enum.IsDefined(Form))
                messages.Add("Error: The utility-function form is not a recognized member.");
            if (!Tools.IsFinite(RiskAversion) || RiskAversion <= 0d)
                messages.Add("Error: The risk-aversion parameter must be finite and positive.");
            return (messages.Count == 0, messages);
        }

        #endregion

        #region Serialization

        /// <summary>
        /// Serializes the declaration. Element and attribute names are append-only contract.
        /// </summary>
        /// <returns>The serialized form.</returns>
        public XElement ToXElement()
        {
            var element = new XElement(nameof(UtilityDeclaration));
            element.SetAttributeValue(nameof(Form), Form.ToString());
            element.SetAttributeValue(nameof(RiskAversion), SerializationUtilities.FormatDouble(RiskAversion));
            return element;
        }

        #endregion
    }
}
