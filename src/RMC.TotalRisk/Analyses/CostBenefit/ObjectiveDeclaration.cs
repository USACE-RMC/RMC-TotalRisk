using System;
using System.Collections.Generic;
using System.Xml.Linq;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Analyses.CostBenefit
{
    /// <summary>
    /// One declared study objective: a display name, the metric selector it reads, and the
    /// optimization direction.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The declaration is immutable (replace it to edit). Element and attribute names are
    /// append-only serialized contract; the metric writes as the single child.
    /// </para>
    /// </remarks>
    public sealed class ObjectiveDeclaration
    {
        #region Construction

        /// <summary>
        /// Initializes an objective declaration.
        /// </summary>
        /// <param name="name">The display name (non-blank).</param>
        /// <param name="metric">The metric selector the objective reads.</param>
        /// <param name="direction">The optimization direction.</param>
        /// <exception cref="ArgumentException">Thrown when the name is blank.</exception>
        /// <exception cref="ArgumentNullException">Thrown when the metric is null.</exception>
        public ObjectiveDeclaration(string name, CostBenefitMetric metric, ObjectiveDirection direction)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("The objective name must not be blank.", nameof(name));
            Name = name;
            Metric = metric ?? throw new ArgumentNullException(nameof(metric));
            Direction = direction;
        }

        /// <summary>
        /// Restores a declaration from its serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        /// <exception cref="ArgumentException">Thrown when the stored payload violates the construction guards.</exception>
        public ObjectiveDeclaration(XElement xElement)
            : this(SerializationUtilities.ReadString(SerializationUtilities.RequireElement(xElement, nameof(xElement)), nameof(Name), "Objective"),
                ReadMetric(xElement),
                SerializationUtilities.ReadEnum(xElement, nameof(Direction), ObjectiveDirection.Minimize))
        {
        }

        /// <summary>
        /// Reads the metric child; a missing child reads as the mean-total-risk selector so
        /// older forms load forward.
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <returns>The metric selector.</returns>
        private static CostBenefitMetric ReadMetric(XElement xElement)
        {
            XElement? child = xElement.Element(nameof(CostBenefitMetric));
            return child == null
                ? CostBenefitMetric.ForRiskMeasure(RiskMeasure.Mean, RiskType.Total)
                : new CostBenefitMetric(child);
        }

        #endregion

        #region Members

        /// <summary>
        /// The display name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The metric selector the objective reads.
        /// </summary>
        public CostBenefitMetric Metric { get; }

        /// <summary>
        /// The optimization direction.
        /// </summary>
        public ObjectiveDirection Direction { get; }

        #endregion

        #region IModel Methods

        /// <summary>
        /// Validates the declaration: a recognized direction and a valid metric.
        /// </summary>
        /// <returns>The validity flag and messages.</returns>
        public (bool IsValid, List<string> ValidationMessages) Validate()
        {
            var messages = new List<string>();
            if (!Enum.IsDefined(Direction))
                messages.Add("Error: The objective direction is not a recognized member.");
            (_, List<string> metricMessages) = Metric.Validate();
            messages.AddRange(metricMessages);
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
            var element = new XElement(nameof(ObjectiveDeclaration));
            element.SetAttributeValue(nameof(Name), Name);
            element.SetAttributeValue(nameof(Direction), Direction.ToString());
            element.Add(Metric.ToXElement());
            return element;
        }

        #endregion
    }
}
