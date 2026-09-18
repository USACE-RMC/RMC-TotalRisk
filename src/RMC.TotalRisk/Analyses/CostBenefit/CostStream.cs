using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using RMC.TotalRisk.Core;

namespace RMC.TotalRisk.Analyses.CostBenefit
{
    /// <summary>
    /// An alternative's tagged cost stream: dated capital entries, operations-and-maintenance
    /// segments, and operating-change segments, each kind priced separately.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The stream is immutable (replace it to edit) and empty by default — the typical
    /// baseline. Operating changes are a separate kind because the cost-adjusted cost per
    /// statistical life saved (ER 1110-2-1156 Appendix L) subtracts the operating-cost delta
    /// in its numerator while the annualized cost carries capital plus operations and
    /// maintenance; folding them together would double-count. Costs are commitments — never
    /// survival-weighted. Element and child names are append-only serialized contract.
    /// </para>
    /// </remarks>
    public sealed class CostStream
    {
        #region Construction

        /// <summary>
        /// Initializes a cost stream.
        /// </summary>
        /// <param name="capital">The dated capital entries; null or empty for none.</param>
        /// <param name="operationsAndMaintenance">The operations-and-maintenance segments; null or empty for none.</param>
        /// <param name="operatingChanges">The operating-change segments; null or empty for none.</param>
        /// <exception cref="ArgumentException">Thrown when a list contains a null entry.</exception>
        public CostStream(IReadOnlyList<CapitalCostEntry>? capital = null,
            IReadOnlyList<RecurringCostSegment>? operationsAndMaintenance = null,
            IReadOnlyList<RecurringCostSegment>? operatingChanges = null)
        {
            Capital = SnapshotEntries(capital, nameof(capital));
            OperationsAndMaintenance = SnapshotSegments(operationsAndMaintenance, nameof(operationsAndMaintenance));
            OperatingChanges = SnapshotSegments(operatingChanges, nameof(operatingChanges));
        }

        /// <summary>
        /// Restores a cost stream from its serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        public CostStream(XElement xElement)
            : this(ReadCapital(SerializationUtilities.RequireElement(xElement, nameof(xElement))),
                ReadSegments(xElement, "OperationsAndMaintenance"),
                ReadSegments(xElement, "OperatingChanges"))
        {
        }

        /// <summary>
        /// Snapshots the capital list, refusing null entries.
        /// </summary>
        /// <param name="entries">The supplied list, or null for none.</param>
        /// <param name="parameterName">The parameter name for the diagnostic.</param>
        /// <returns>The read-only snapshot.</returns>
        /// <exception cref="ArgumentException">Thrown when the list contains a null entry.</exception>
        private static IReadOnlyList<CapitalCostEntry> SnapshotEntries(
            IReadOnlyList<CapitalCostEntry>? entries, string parameterName)
        {
            var snapshot = entries == null ? Array.Empty<CapitalCostEntry>() : entries.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (snapshot[i] == null)
                    throw new ArgumentException("The capital list contains a null entry.", parameterName);
            }
            return Array.AsReadOnly(snapshot);
        }

        /// <summary>
        /// Snapshots a segment list, refusing null entries.
        /// </summary>
        /// <param name="segments">The supplied list, or null for none.</param>
        /// <param name="parameterName">The parameter name for the diagnostic.</param>
        /// <returns>The read-only snapshot.</returns>
        /// <exception cref="ArgumentException">Thrown when the list contains a null entry.</exception>
        private static IReadOnlyList<RecurringCostSegment> SnapshotSegments(
            IReadOnlyList<RecurringCostSegment>? segments, string parameterName)
        {
            var snapshot = segments == null ? Array.Empty<RecurringCostSegment>() : segments.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (snapshot[i] == null)
                    throw new ArgumentException("The segment list contains a null entry.", parameterName);
            }
            return Array.AsReadOnly(snapshot);
        }

        /// <summary>
        /// Reads the capital entries from the serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <returns>The capital entries.</returns>
        private static IReadOnlyList<CapitalCostEntry> ReadCapital(XElement xElement)
        {
            var wrapper = xElement.Element("Capital");
            if (wrapper == null) return Array.Empty<CapitalCostEntry>();
            var entries = new List<CapitalCostEntry>();
            foreach (XElement child in wrapper.Elements(nameof(CapitalCostEntry)))
            {
                entries.Add(new CapitalCostEntry(child));
            }
            return entries;
        }

        /// <summary>
        /// Reads one kind's recurring segments from the serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <param name="wrapperName">The kind's wrapper element name.</param>
        /// <returns>The segments.</returns>
        private static IReadOnlyList<RecurringCostSegment> ReadSegments(XElement xElement, string wrapperName)
        {
            var wrapper = xElement.Element(wrapperName);
            if (wrapper == null) return Array.Empty<RecurringCostSegment>();
            var segments = new List<RecurringCostSegment>();
            foreach (XElement child in wrapper.Elements(nameof(RecurringCostSegment)))
            {
                segments.Add(new RecurringCostSegment(child));
            }
            return segments;
        }

        #endregion

        #region Members

        /// <summary>
        /// The dated capital entries.
        /// </summary>
        public IReadOnlyList<CapitalCostEntry> Capital { get; }

        /// <summary>
        /// The operations-and-maintenance segments.
        /// </summary>
        public IReadOnlyList<RecurringCostSegment> OperationsAndMaintenance { get; }

        /// <summary>
        /// The operating-change segments — the operating-cost term the cost-adjusted
        /// life-saved ratio subtracts separately.
        /// </summary>
        public IReadOnlyList<RecurringCostSegment> OperatingChanges { get; }

        #endregion

        #region Serialization

        /// <summary>
        /// Serializes the stream. Element and child names are append-only contract; each kind
        /// writes under its own wrapper child.
        /// </summary>
        /// <returns>The serialized form.</returns>
        public XElement ToXElement()
        {
            var element = new XElement(nameof(CostStream));
            var capital = new XElement("Capital");
            for (int i = 0; i < Capital.Count; i++)
            {
                capital.Add(Capital[i].ToXElement());
            }
            element.Add(capital);
            var om = new XElement("OperationsAndMaintenance");
            for (int i = 0; i < OperationsAndMaintenance.Count; i++)
            {
                om.Add(OperationsAndMaintenance[i].ToXElement());
            }
            element.Add(om);
            var operating = new XElement("OperatingChanges");
            for (int i = 0; i < OperatingChanges.Count; i++)
            {
                operating.Add(OperatingChanges[i].ToXElement());
            }
            element.Add(operating);
            return element;
        }

        #endregion
    }
}
