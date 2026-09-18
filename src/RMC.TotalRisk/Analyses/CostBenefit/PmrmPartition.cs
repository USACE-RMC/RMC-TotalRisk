using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Numerics;
using RMC.TotalRisk.Core;

namespace RMC.TotalRisk.Analyses.CostBenefit
{
    /// <summary>
    /// The declared partitioned multiobjective risk-method (PMRM) partition: the exceedance
    /// probabilities dividing the annual-loss exceedance curve into ranges whose conditional
    /// expectations are reported side by side.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The partition is immutable (replace it to edit). Boundaries are exceedance
    /// probabilities in strictly descending order — for example 0.1 | 0.01 | 0.001 splits the
    /// curve into a high-probability range, two middle ranges, and the low-probability
    /// high-consequence tail whose conditional expectation the conditional value-at-risk
    /// specializes. Element and attribute names are append-only serialized contract.
    /// </para>
    /// </remarks>
    public sealed class PmrmPartition
    {
        #region Construction

        /// <summary>
        /// Initializes a partition declaration.
        /// </summary>
        /// <param name="exceedanceBoundaries">The exceedance-probability boundaries, strictly descending.</param>
        /// <exception cref="ArgumentNullException">Thrown when the boundary list is null.</exception>
        public PmrmPartition(IReadOnlyList<double> exceedanceBoundaries)
        {
            if (exceedanceBoundaries == null) throw new ArgumentNullException(nameof(exceedanceBoundaries));
            ExceedanceBoundaries = Array.AsReadOnly(exceedanceBoundaries.ToArray());
        }

        /// <summary>
        /// Restores a partition from its serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        public PmrmPartition(XElement xElement)
            : this(ReadBoundaries(SerializationUtilities.RequireElement(xElement, nameof(xElement))))
        {
        }

        /// <summary>
        /// Reads the pipe-delimited boundaries from the serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <returns>The boundaries.</returns>
        private static IReadOnlyList<double> ReadBoundaries(XElement xElement)
        {
            string text = SerializationUtilities.ReadString(xElement, nameof(ExceedanceBoundaries));
            if (string.IsNullOrEmpty(text)) return Array.Empty<double>();
            string[] parts = text.Split('|', StringSplitOptions.RemoveEmptyEntries);
            var boundaries = new List<double>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                boundaries.Add(SerializationUtilities.ParseDouble(parts[i]));
            }
            return boundaries;
        }

        #endregion

        #region Members

        /// <summary>
        /// The exceedance-probability boundaries, strictly descending, each in (0, 1).
        /// </summary>
        public IReadOnlyList<double> ExceedanceBoundaries { get; }

        #endregion

        #region IModel Methods

        /// <summary>
        /// Validates the partition: at least one boundary, each in (0, 1), strictly
        /// descending.
        /// </summary>
        /// <returns>The validity flag and messages.</returns>
        public (bool IsValid, List<string> ValidationMessages) Validate()
        {
            var messages = new List<string>();
            if (ExceedanceBoundaries.Count == 0)
                messages.Add("Error: The partition requires at least one exceedance boundary.");
            for (int i = 0; i < ExceedanceBoundaries.Count; i++)
            {
                double boundary = ExceedanceBoundaries[i];
                if (!Tools.IsFinite(boundary) || boundary <= 0d || boundary >= 1d)
                {
                    messages.Add("Error: Every partition boundary must lie in (0, 1).");
                    break;
                }
            }
            for (int i = 1; i < ExceedanceBoundaries.Count; i++)
            {
                if (!(ExceedanceBoundaries[i] < ExceedanceBoundaries[i - 1]))
                {
                    messages.Add("Error: The partition boundaries must be strictly descending exceedance probabilities.");
                    break;
                }
            }
            return (messages.Count == 0, messages);
        }

        #endregion

        #region Serialization

        /// <summary>
        /// Serializes the partition. Element and attribute names are append-only contract.
        /// </summary>
        /// <returns>The serialized form.</returns>
        public XElement ToXElement()
        {
            var element = new XElement(nameof(PmrmPartition));
            string[] parts = new string[ExceedanceBoundaries.Count];
            for (int i = 0; i < ExceedanceBoundaries.Count; i++)
            {
                parts[i] = SerializationUtilities.FormatDouble(ExceedanceBoundaries[i]);
            }
            element.SetAttributeValue(nameof(ExceedanceBoundaries), string.Join("|", parts));
            return element;
        }

        #endregion
    }
}
